using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Cyanlabs.Launcher.Updating
{
    public sealed class UpdateRequest
    {
        public string InstallDir { get; set; }
        public ReleaseChannel Channel { get; set; }

        /// <summary>The tag recorded after the last successful update, and the channel it came from.</summary>
        public string InstalledTag { get; set; }
        public ReleaseChannel? InstalledChannel { get; set; }

        /// <summary>Windows build number (0 = unknown, nothing is blocked).</summary>
        public int OsBuild { get; set; }
        public string Arch { get; set; } = "x64";

        /// <summary>Settings folders to copy aside before a major-version jump, and where to put the copy.</summary>
        public string[] DataDirs { get; set; } = new string[0];
        public string BackupRoot { get; set; }

        /// <summary>Called right before files are replaced; the launcher closes any running Syn3 Updater here.</summary>
        public Func<Task> CloseApp { get; set; }
        public Action<string> Log { get; set; } = _ => { };
        public IFileSystem FileSystem { get; set; }
        public RetryPolicy Retry { get; set; }
    }

    public sealed class UpdateProgress
    {
        public UpdateProgress(string message, int? percent = null) { Message = message; Percent = percent; }
        public string Message { get; }
        public int? Percent { get; }
    }

    public enum OutcomeKind
    {
        /// <summary>Already on the right release; nothing changed.</summary>
        UpToDate,
        /// <summary>A new release was installed.</summary>
        Installed,
        /// <summary>No update was done (offline, rate limited, unsupported system) but the installed app can still be started.</summary>
        Skipped,
        /// <summary>Something went wrong; the install was left as it was.</summary>
        Failed
    }

    public sealed class UpdateOutcome
    {
        public OutcomeKind Kind { get; set; }
        public string Message { get; set; } = "";
        public ReleaseVersion Version { get; set; }
        public string DataBackupPath { get; set; }
    }

    public sealed class UpdateEngine
    {
        private readonly IReleaseSource _source;

        public UpdateEngine(IReleaseSource source) { _source = source; }

        public async Task<UpdateOutcome> RunAsync(UpdateRequest req, IProgress<UpdateProgress> progress, CancellationToken ct)
        {
            var log = req.Log ?? (_ => { });
            var appExe = Path.Combine(req.InstallDir, PackageStager.AppExe);
            var appExists = File.Exists(appExe);
            progress?.Report(new UpdateProgress("Checking for updates..."));

            IReadOnlyList<ReleaseInfo> releases;
            try { releases = await _source.GetReleasesAsync(ct).ConfigureAwait(false); }
            catch (RateLimitedException)
            {
                log("GitHub rate limit reached");
                return appExists ? Skip("GitHub request limit reached. Skipping the update check.") : Fail("GitHub request limit reached and Syn3 Updater is not installed yet. Please try again later.");
            }
            catch (NetworkException e)
            {
                log("Network error: " + e.Message);
                return appExists ? Skip("Unable to check for updates. Starting the installed version.") : Fail("Unable to download Syn3 Updater. Please check your internet connection or try again later.");
            }

            Func<ReleaseVersion, bool> supported = v => Compatibility.IsSupported(v, req.OsBuild);
            var pick = ReleasePicker.Pick(releases, req.Channel, supported);
            var newestAny = ReleasePicker.Pick(releases, req.Channel);
            if (pick == null)
                return appExists ? Skip("No compatible release found. Starting the installed version.") : Fail("No release of Syn3 Updater is available for this computer.");
            log($"Channel {req.Channel}: newest compatible release {pick.TagName}; installed '{req.InstalledTag}' ({req.InstalledChannel})");

            var decision = UpdateRules.Decide(req.InstalledTag, req.InstalledChannel, req.Channel, pick.Version, appExists);
            if (decision == UpdateDecision.UpToDate)
            {
                var held = newestAny != null && newestAny.Version > pick.Version;
                return new UpdateOutcome
                {
                    Kind = OutcomeKind.UpToDate, Version = pick.Version,
                    Message = held ? $"Version {newestAny.Version.NumericString} needs Windows 10 (version 1607) or later. Staying on {pick.Version.NumericString}." : "Up to date."
                };
            }

            var asset = AssetPicker.Pick(pick.Assets, req.Arch);
            if (asset == null)
                return appExists ? Skip($"Release {pick.TagName} has no download for this computer ({req.Arch}). Starting the installed version.") : Fail($"Release {pick.TagName} has no download for this computer ({req.Arch}).");

            var work = Path.Combine(Path.GetTempPath(), "syn3updater-launcher-" + Guid.NewGuid().ToString("N"));
            var staging = Path.Combine(req.InstallDir, InstallTransaction.StagingDirName);
            try
            {
                Directory.CreateDirectory(work);
                var zipPath = Path.Combine(work, asset.Name);

                // 1. download
                progress?.Report(new UpdateProgress($"Installing {req.Channel} release {pick.Version.NumericString}"));
                var dl = new Progress<KeyValuePair<long, long>>(p =>
                {
                    var mb = p.Key / 1000000;
                    progress?.Report(p.Value > 0
                        ? new UpdateProgress($"Downloaded {mb}MB of {p.Value / 1000000}MB", (int)(p.Key * 100 / p.Value))
                        : new UpdateProgress($"Downloaded {mb}MB"));
                });
                await _source.DownloadFileAsync(asset.DownloadUrl, zipPath, dl, ct).ConfigureAwait(false);

                // 2. verify, when the release publishes a checksum
                var expected = await FindExpectedHashAsync(pick, asset, ct).ConfigureAwait(false);
                if (expected != null)
                {
                    progress?.Report(new UpdateProgress("Verifying download..."));
                    if (!Checksums.Matches(expected, Checksums.ComputeSha256(zipPath)))
                        throw new PackageException("The download is corrupt (its checksum does not match). Nothing was changed.");
                    log("Checksum verified");
                }
                else log("No checksum published for this asset; skipping verification");

                // 3. unpack and check before touching the install
                progress?.Report(new UpdateProgress("Extracting..."));
                var files = PackageStager.Extract(zipPath, staging);

                // 4. keep a copy of the user's settings across a major-version jump (v2 -> v3)
                string backupPath = null;
                var installed = ReleaseVersion.ParseOrNull(req.InstalledTag);
                if (installed != null && installed.Major != pick.Version.Major && !string.IsNullOrEmpty(req.BackupRoot))
                {
                    backupPath = DataBackup.Create(req.BackupRoot, installed.NumericString, req.DataDirs);
                    log("Settings backed up to " + backupPath);
                }

                // 5. swap the files, all or nothing
                if (req.CloseApp != null) await req.CloseApp().ConfigureAwait(false);
                var stale = StalePlanner.Compute(req.InstallDir, InstallManifest.Load(req.InstallDir), files);
                new InstallTransaction(req.InstallDir, req.FileSystem, req.Retry).Apply(staging, files, stale);
                try { new InstallManifest { Version = pick.Version.NumericString, Files = files.ToList() }.Save(req.InstallDir); }
                catch (Exception e) { log("Could not save the install manifest: " + e.Message); }

                log("Installed " + pick.TagName);
                return new UpdateOutcome { Kind = OutcomeKind.Installed, Version = pick.Version, DataBackupPath = backupPath, Message = "Installed " + pick.Version.NumericString };
            }
            catch (NetworkException e)
            {
                log("Download failed: " + e.Message);
                return appExists ? Skip("The download failed. Starting the installed version.") : Fail("The download failed. Please check your internet connection and try again.");
            }
            catch (Exception e) when (e is PackageException || e is InstallException || e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                log("Update failed: " + e);
                return appExists ? Fail(e.Message + " The installed version was left as it was.") : Fail(e.Message);
            }
            finally
            {
                try { Directory.Delete(work, true); } catch { /* temp folder; the OS clears it eventually */ }
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { /* removed by Recover() next start */ }
            }
        }

        private async Task<string> FindExpectedHashAsync(ReleaseInfo release, ReleaseAsset asset, CancellationToken ct)
        {
            var fromDigest = Checksums.Normalize(asset.Digest);
            if (fromDigest != null) return fromDigest;
            try
            {
                var sums = release.Assets.FirstOrDefault(a => a.Name.StartsWith("SHA256SUMS", StringComparison.OrdinalIgnoreCase));
                if (sums != null)
                {
                    var h = Checksums.FindInSums(await _source.DownloadStringAsync(sums.DownloadUrl, ct).ConfigureAwait(false), asset.Name);
                    if (h != null) return h;
                }
                var single = release.Assets.FirstOrDefault(a => a.Name.Equals(asset.Name + ".sha256", StringComparison.OrdinalIgnoreCase));
                if (single != null)
                {
                    var text = await _source.DownloadStringAsync(single.DownloadUrl, ct).ConfigureAwait(false);
                    return Checksums.Normalize(text.Trim().Split(' ', '\t', '\r', '\n')[0]);
                }
            }
            catch (NetworkException) { /* a missing checksum list must not block an otherwise good update */ }
            return null;
        }

        private static UpdateOutcome Skip(string message) => new UpdateOutcome { Kind = OutcomeKind.Skipped, Message = message };
        private static UpdateOutcome Fail(string message) => new UpdateOutcome { Kind = OutcomeKind.Failed, Message = message };
    }
}
