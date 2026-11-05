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

public record RunProgress(string Status, int FilePercent, int TotalPercent, string? CurrentFile, string Phase = "");

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
            int steps = queue.Count * (req.DownloadOnly ? 2 : 4), done = 0;
            string phase = "download";
            void Total(string status, int file = 0, string? cur = null) =>
                progress.Report(new(status, file, steps == 0 ? 0 : Math.Min(100, done * 100 / steps), cur, phase));

            // ---- download + validate ----
            int connections = req.Connections;
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
                        Total($"Downloading{(attempt > 1 ? $" (attempt #{attempt})" : "")}: {item.FileName}", 0, item.FileName);
                        if (!await _files.DownloadAsync(item.Url, local, connections, fp, ct)) return false;
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
            steps = queue.Count * 4; // download steps already counted above; extras only copy
            phase = "copy";
            var syncDir = Path.Combine(target!, "SyncMyRide");
            Directory.CreateDirectory(syncDir);
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
                    Total($"Copying{(attempt > 1 ? $" (attempt #{attempt})" : "")}: {item.FileName}", 0, item.FileName);
                    if (!await FileService.CopyAsync(src, dst, fp, ct)) return false;
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
            if (req.Drive is { IsFolder: false } drive) await _drives.FlushAsync(drive, ct);
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
