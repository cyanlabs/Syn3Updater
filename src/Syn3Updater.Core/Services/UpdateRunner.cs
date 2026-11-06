using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

public class UpdateRequest
{
    public required List<Ivsu> Queue { get; init; }
    public required SpecialTools Tools { get; init; }
    public UpdateAction Action { get; init; } = UpdateAction.Main;
    public string InstallMode { get; init; } = Models.InstallModes.Autoinstall;
    public string Release { get; init; } = "";
    public string Region { get; init; } = "";
    public bool ModeForced { get; init; }
    public bool DownloadOnly { get; init; }
    /// <summary>The USB drive, or a pseudo-drive (IsFolder) whose MountPath is the target folder.</summary>
    public DriveInfoModel? Drive { get; init; }
    public bool SkipFormat { get; init; }
    public required string DownloadDir { get; init; }
    public int Connections { get; init; } = 1;
    public string AppVersion { get; init; } = "";
    public string Branch { get; init; } = "Stable";
}

public record RunProgress(string Status, int FilePercent, int TotalPercent, string? CurrentFile, string Phase = "")
{
    /// <summary>Bytes actually moved so far in this phase (downloaded or copied), summed over all parallel connections.</summary>
    public long TransferredBytes { get; init; }
    /// <summary>Estimate of the bytes still to move in this phase (files already in place are not counted).</summary>
    public long RemainingBytes { get; init; }
    /// <summary>Connections in use for the current transfer (1 while copying).</summary>
    public int Connections { get; init; }
}

/// <summary>An IProgress that runs its callback immediately on the reporting thread (Progress&lt;T&gt; would reorder updates through a SynchronizationContext).</summary>
internal sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) => action(value);
}

/// <summary>Runs the whole job: (format) -> download -> validate -> copy -> write autoinstall.lst/reformat.lst.</summary>
public class UpdateRunner
{
    private readonly IDriveService _drives;
    private readonly FileService _files;
    private readonly AppLog _log;

    public UpdateRunner(IDriveService drives, FileService files, AppLog log) { _drives = drives; _files = files; _log = log; }

    public string? LastError { get; private set; }

    /// <summary>
    /// Where files were really written. Formatting relabels a drive (e.g. DVD_ROM -> CYANLABS), so the OS may
    /// remount it at a different path than the one that was selected; anything done after the run must use this.
    /// </summary>
    public string? TargetRoot { get; private set; }

    private void Info(string m) { _log.Info(m); }

    private static bool HasSize(string path, long size) => size > 0 && File.Exists(path) && new FileInfo(path).Length == size;

    public async Task<bool> RunAsync(UpdateRequest req, IProgress<RunProgress> progress, CancellationToken ct)
    {
        LastError = null; TargetRoot = null;
        string? target = null;
        try
        {
            if (!req.DownloadOnly)
            {
                target = await PrepareTargetAsync(req, progress, ct);
                if (target == null) return false;
                TargetRoot = target;
            }

            var queue = new List<Ivsu>(req.Queue);
            Directory.CreateDirectory(req.DownloadDir);
            if (FileService.CleanStalePartials(req.DownloadDir, TimeSpan.FromDays(14)) is > 0 and var stale) Info($"Removed {stale} abandoned partial download(s) older than 14 days");
            int verifySteps = req.Drive is { IsFolder: false } && !req.DownloadOnly ? queue.Count : 0;
            int steps = queue.Count * (req.DownloadOnly ? 2 : 4) + verifySteps, done = 0;
            string phase = "download";
            int connections = req.Connections;
            // byte accounting for speed / ETA: what has been moved in this phase and what is still expected
            long planned = queue.Sum(i => HasSize(Path.Combine(req.DownloadDir, i.FileName), i.FileSize) ? 0 : i.FileSize);
            long movedBefore = 0, fileBytes = 0; int filePct = 0; string? currentFile = null;
            var gate = new object();
            void Total(string status, int file = 0, string? cur = null)
            {
                lock (gate)
                {
                    filePct = file; currentFile = cur ?? currentFile;
                    var transferred = movedBefore + fileBytes;
                    progress.Report(new(status, file, steps == 0 ? 0 : Math.Min(100, done * 100 / steps), cur, phase)
                    {
                        TransferredBytes = transferred, RemainingBytes = Math.Max(0, planned - transferred), Connections = phase == "download" ? connections : 1,
                    });
                }
            }
            IProgress<long> Bytes(string file) => new InlineProgress<long>(b => { fileBytes = b; Total("", filePct, file); });

            // ---- download + validate ----
            var extras = new List<Ivsu>();
            foreach (var item in queue)
            {
                ct.ThrowIfCancellationRequested();
                var local = Path.Combine(req.DownloadDir, item.FileName);
                var fp = new Progress<int>(p => Total("", p, item.FileName));

                Total($"Checking existing file: {item.FileName}", 0, item.FileName);
                if ((await _files.ValidateAsync(item.Url, local, item.Md5, false, fp, ct)).Ok)
                {
                    Info($"Validated: {item.FileName} (Skipping Download)");
                    done += 2;
                }
                else
                {
                    bool ok = false;
                    for (int attempt = 1; attempt <= 3 && !ok; attempt++)
                    {
                        ct.ThrowIfCancellationRequested();
                        Info(attempt == 1 ? $"Downloading: {item.FileName}" : $"Downloading (Attempt #{attempt}): {item.FileName}");
                        // a file that was thought to be in place (same size, bad checksum) or a retry still has to be transferred
                        if (attempt > 1 || HasSize(local, item.FileSize)) lock (gate) planned += item.FileSize;
                        fileBytes = 0;
                        Total($"Downloading{(attempt > 1 ? $" (attempt #{attempt})" : "")}: {item.FileName}", 0, item.FileName);
                        if (!await _files.DownloadAsync(item.Url, local, connections, fp, ct, Bytes(item.FileName), alreadyThere =>
                            {
                                // continuing an interrupted download: those bytes will not be transferred again, so they leave the "still to come" total
                                lock (gate) planned = Math.Max(0, planned - alreadyThere);
                                var pct = item.FileSize > 0 ? Math.Min(100, (int)(alreadyThere * 100 / item.FileSize)) : 0;
                                Info($"Resuming: {item.FileName} ({pct}% was already downloaded)");
                                Total($"Resuming: {item.FileName} ({pct}% already downloaded)", pct, item.FileName);
                            })) return false;
                        lock (gate) { movedBefore += fileBytes; fileBytes = 0; }
                        done++;
                        Total($"Validating: {item.FileName}", 0, item.FileName);
                        var v = await _files.ValidateAsync(item.Url, local, item.Md5, false, fp, ct);
                        if (v.Ok) { done++; ok = true; Info($"Downloaded: {item.FileName}"); }
                        else
                        {
                            if (attempt == 2 && connections != 1)
                            {
                                Info($"Unable to validate {item.FileName} after 2 attempts, attempting once more with 'connections' forced to 1");
                                connections = 1;
                            }
                            if (attempt == 3)
                            {
                                LastError = $"Unable to validate {item.FileName} after 3 attempts";
                                Info(LastError + ", ABORTING PROCESS!");
                                return false;
                            }
                        }
                    }
                }

                if (item.Source == "naviextras")
                {
                    Info($"Extracting: {item.FileName} (This may take some time!)");
                    Total($"Extracting: {item.FileName}", 0, item.FileName);
                    extras.AddRange(await _files.ExtractMultiPackageAsync(item, req.DownloadDir, ct));
                    Info("Added MultiPackage files to Queue");
                }
            }

            if (req.DownloadOnly)
            {
                progress.Report(new("Download complete", 100, 100, null));
                return true;
            }

            // ---- copy ----
            queue.AddRange(extras);
            verifySteps = req.Drive is { IsFolder: false } ? queue.Count : 0;
            steps = queue.Count * 4 + verifySteps; // download steps already counted above; extras only copy
            var syncDir = Path.Combine(target!, "SyncMyRide");
            Directory.CreateDirectory(syncDir);
            lock (gate)
            {
                phase = "copy"; movedBefore = 0; fileBytes = 0;
                planned = queue.Where(i => i.Source != "naviextras").Sum(i => HasSize(Path.Combine(syncDir, i.FileName), i.FileSize) ? 0 : i.FileSize);
            }
            Info("Preparing USB drive");
            foreach (var item in queue)
            {
                ct.ThrowIfCancellationRequested();
                if (item.Source == "naviextras") { done++; continue; }
                var src = Path.Combine(req.DownloadDir, item.FileName);
                var dst = Path.Combine(syncDir, item.FileName);
                var fp = new Progress<int>(p => Total("", p, item.FileName));

                if ((await _files.ValidateAsync(src, dst, item.Md5, true, fp, ct)).Ok)
                {
                    Info($"{item.FileName} exists and validated successfully, skipping copy");
                    done += 2; continue;
                }

                bool ok = false;
                for (int attempt = 1; attempt <= 3 && !ok; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    Info(attempt == 1 ? $"Copying: {item.FileName}" : $"Copying (Attempt #{attempt}): {item.FileName}");
                    if (attempt > 1) lock (gate) planned += item.FileSize;
                    fileBytes = 0;
                    Total($"Copying{(attempt > 1 ? $" (attempt #{attempt})" : "")}: {item.FileName}", 0, item.FileName);
                    if (!await FileService.CopyAsync(src, dst, fp, ct, Bytes(item.FileName))) return false;
                    lock (gate) { movedBefore += fileBytes; fileBytes = 0; }
                    done++;
                    Total($"Validating: {item.FileName}", 0, item.FileName);
                    if ((await _files.ValidateAsync(src, dst, item.Md5, true, fp, ct)).Ok) { done++; ok = true; Info($"Copied: {item.FileName}"); }
                    else if (attempt == 3)
                    {
                        LastError = $"Unable to validate {item.FileName} after 3 copy attempts";
                        Info(LastError + ", ABORTING PROCESS!");
                        return false;
                    }
                }
            }

            WriteInstallFiles(req, queue, target!);
            progress.Report(new("Flushing writes to the drive", 100, 99, null));
            if (req.Drive is { IsFolder: false } drive)
            {
                await _drives.FlushAsync(drive, ct);
                // The check right after each copy is served from the OS cache. Once everything is flushed, read it all back from
                // the stick itself: a bad stick or a silent write failure shows up here instead of in the car.
                lock (gate) { phase = "verify"; movedBefore = 0; fileBytes = 0; planned = queue.Where(i => i.Source != "naviextras").Sum(i => i.FileSize); }
                Info("Verifying the files on the drive");
                foreach (var item in queue)
                {
                    ct.ThrowIfCancellationRequested();
                    if (item.Source == "naviextras") { done++; continue; }
                    var src = Path.Combine(req.DownloadDir, item.FileName);
                    var dst = Path.Combine(syncDir, item.FileName);
                    var fp = new Progress<int>(p => Total("", p, item.FileName));
                    Total($"Verifying: {item.FileName}", 0, item.FileName);
                    if (!(await _files.ValidateAsync(src, dst, item.Md5, true, fp, ct, bypassCache: true)).Ok)
                    {
                        LastError = $"{item.FileName} on the drive does not match the downloaded file. The drive may be faulty; try another USB stick.";
                        Info($"Unable to verify {item.FileName} on the drive, ABORTING PROCESS!");
                        return false;
                    }
                    lock (gate) { movedBefore += item.FileSize; fileBytes = 0; }
                    done++;
                    Info($"Verified: {item.FileName}");
                }
                Info("All files on the drive verified");
            }
            progress.Report(new("Completed", 100, 100, null));
            Info(req.Drive?.IsFolder == true
                ? "ALL FILES DOWNLOADED AND COPIED TO THE SELECTED FOLDER SUCCESSFULLY!"
                : "ALL FILES DOWNLOADED AND COPIED TO THE USB DRIVE SUCCESSFULLY!");
            return true;
        }
        catch (OperationCanceledException) { Info("Process cancelled by user"); return false; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or Drives.FormatException)
        {
            LastError = e.Message; _log.Error(e.Message); return false;
        }
    }

    /// <summary>Formats the USB drive / clears the folder as needed and returns the root path files are copied to.</summary>
    private async Task<string?> PrepareTargetAsync(UpdateRequest req, IProgress<RunProgress> progress, CancellationToken ct)
    {
        var drive = req.Drive ?? throw new InvalidOperationException("No destination selected");
        if (drive.IsFolder)
        {
            if (!Directory.Exists(drive.MountPath)) Directory.CreateDirectory(drive.MountPath);
            if (!req.SkipFormat)
            {
                Info("Clearing Selected Folder");
                progress.Report(new("Clearing selected folder", 0, 0, null));
                try
                {
                    foreach (var f in Directory.GetFiles(drive.MountPath)) File.Delete(f);
                    foreach (var d in Directory.GetDirectories(drive.MountPath)) Directory.Delete(d, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    LastError = "Unable to clear the selected folder: " + e.Message; Info(LastError); return null;
                }
            }
            return drive.MountPath;
        }

        if (!req.SkipFormat)
        {
            progress.Report(new("Formatting USB drive", 0, 0, null));
            Info("Formatting USB drive");
            return await _drives.FormatAsync(drive, new Progress<string>(Info), ct);
        }
        return await _drives.EnsureMountedAsync(drive, ct);
    }

    private void WriteInstallFiles(UpdateRequest req, List<Ivsu> queue, string target)
    {
        string Write(string name, string content)
        {
            var p = Path.Combine(target, name);
            File.WriteAllText(p, content);
            return p;
        }

        if (req.Action == UpdateAction.Main && req.InstallMode is InstallModes.Downgrade or InstallModes.Reformat)
        {
            Info("Generating reformat.lst");
            Write("reformat.lst", AutoInstallBuilder.CreateReformatList(queue, req.InstallMode, req.Tools, req.Release));
            Info("Generating autoinstall.lst");
            Write("autoinstall.lst", AutoInstallBuilder.CreateReformatAutoInstall(req.InstallMode, req.Tools, req.Release, req.Region,
                req.ModeForced, req.AppVersion, req.Branch));
        }
        else
        {
            Info("Generating Autoinstall.lst");
            Write("autoinstall.lst", AutoInstallBuilder.CreateAutoInstall(queue, req.Tools, req.Action, req.Release, req.Region,
                req.ModeForced, req.AppVersion, req.Branch));
        }
        File.WriteAllBytes(Path.Combine(target, "DONTINDX.MSA"), Array.Empty<byte>());
    }
}
