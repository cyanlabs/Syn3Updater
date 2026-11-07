using System.Security.Cryptography;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>What to install on Windows: the verified zip (and the hash and size the signed manifest promised for it) into the folder the running app lives in.</summary>
/// <param name="Tag">The release tag the zip came from (v3.1.0, 2.99.99.5): the Launcher records installs by tag.</param>
/// <param name="LauncherPrefsPath">The Launcher's prefs file to keep in step, or null.</param>
public sealed record WindowsUpdate(string InstallDir, string ZipPath, string Sha256, long Size, string Tag, string Version, UpdateChannel Channel, string? LauncherPrefsPath);

/// <summary>
/// The Windows half of an in-place update, done by the running app itself. Windows will not let a running program be overwritten or deleted, but it does let it be RENAMED, which is all
/// the swap needs: every file to be replaced (the running Syn3Updater.exe included) is moved into a backup folder, the new files are moved in, and the app starts the new program and exits.
/// Before that the zip is re-checked against the hash from the signed manifest while it is held open with writes denied, so what was verified is what is installed; any update that was
/// interrupted earlier is undone first; and a failure at any point puts the previous version back. The old program, still running from the backup folder, is removed by the next start.
/// </summary>
public static class WindowsApplier
{
    /// <returns>The program to start next.</returns>
    /// <exception cref="UpdateApplyException">Anything went wrong; the previous version is in place.</exception>
    public static string Apply(WindowsUpdate u, ISwapFileSystem? fs = null, RetryPolicy? retry = null, Action<string>? log = null)
    {
        log ??= _ => { };
        var app = Path.Combine(u.InstallDir, PackageStager.AppExe);
        try
        {
            if (!File.Exists(app)) throw new UpdateApplyException($"{u.InstallDir} does not contain {PackageStager.AppExe}");
            if (!File.Exists(u.ZipPath)) throw new UpdateApplyException("the downloaded update is missing");

            // hold the file with writes denied from the check until the files are out of it
            using var hold = new FileStream(u.ZipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (hold.Length != u.Size) throw new UpdateApplyException("the downloaded update is not the size the signed manifest promised, so it was not installed");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(hold)), u.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateApplyException("the downloaded update does not match its signed checksum, so it was not installed");
            log("Checksum verified");

            if (InstallTransaction.Recover(u.InstallDir, fs)) log("Rolled back an earlier interrupted update");
            var staging = Path.Combine(u.InstallDir, InstallTransaction.StagingDirName);
            try
            {
                var files = PackageStager.Extract(u.ZipPath, staging);
                var stale = StalePlanner.Compute(u.InstallDir, InstallManifest.Load(u.InstallDir), files);
                new InstallTransaction(u.InstallDir, fs, retry).Apply(staging, files, stale);
                try { new InstallManifest { Version = LauncherPrefs.NumericTag(u.Tag) ?? u.Version, Files = files.ToList() }.Save(u.InstallDir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("Could not save the install manifest: " + e.Message); }
            }
            finally { try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }

            if (LauncherPrefs.WriteInstalled(u.LauncherPrefsPath, u.Tag, u.Channel, u.InstallDir)) log("Recorded the install for the Launcher");
            log($"Installed {u.Version}");
            return app;
        }
        catch (UpdateApplyException) { throw; }
        catch (Exception e) when (e is InstallException or PackageException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new UpdateApplyException(e.Message, e);
        }
    }
}
