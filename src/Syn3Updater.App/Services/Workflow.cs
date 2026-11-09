using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Services;

public class JobSpec
{
    public List<Ivsu> Packages { get; init; } = new();
    public string Mode { get; init; } = InstallModes.Autoinstall;
    public UpdateAction Action { get; init; } = UpdateAction.Main;
    public string Release { get; init; } = "";
    public string Region { get; init; } = "";
    public string MapVersion { get; init; } = "";
    public DriveInfoModel? Drive { get; init; }
    public bool DownloadOnly { get; init; }
    public bool NeedDowngradeTools { get; init; }
    public bool NeedReformat { get; init; }
    public bool NeedRwCleaner { get; init; } = true;
    /// <summary>Tool packages Home already looked up and showed in the package list: used as they are, so what was shown is what is downloaded. Anything missing is looked up here.</summary>
    public SpecialTools? Preloaded { get; init; }
    public string? SpecialPackage { get; init; }   // single utility package (gracenotes, voice, ...)
}

/// <summary>Turns a user selection into a confirmed UpdateRequest (the sanity checks of the original app) and starts it.</summary>
public static class Workflow
{
    public static bool Busy { get; set; }

    public static async Task<bool> StartAsync(JobSpec spec)
    {
        if (Busy) { await Dialogs.Error(Ctx.T("MessageBox.DownloadInProgress")); return false; }
        if (!Ctx.Main.DisclaimerAccepted)
        {
            await Dialogs.Error(Ctx.T("MessageBox.DisclaimerNotAccepted"));
            MainWindowViewModel.Instance.Current = MainWindowViewModel.Instance.About;
            return false;
        }

        var tools = new SpecialTools();
        var queue = new List<Ivsu>();
        try
        {
            var pre = spec.Preloaded;
            if (spec.NeedDowngradeTools)
            {
                tools.DowngradeApp = pre?.DowngradeApp ?? await Ctx.Api.GetSpecialPackage(SpecialPackages.DowngradeApp);
                tools.DowngradeTool = pre?.DowngradeTool ?? await Ctx.Api.GetSpecialPackage(SpecialPackages.DowngradeTool);
                queue.Add(tools.DowngradeApp); queue.Add(tools.DowngradeTool);
            }
            if (spec.NeedReformat)
            {
                tools.ReformatTool = pre?.ReformatTool ?? await Ctx.Api.GetSpecialPackage(SpecialPackages.Reformat);
                queue.Add(tools.ReformatTool);
            }
            if (spec.NeedRwCleaner)
            {
                tools.RwDataCleaner = pre?.RwDataCleaner ?? await Ctx.Api.GetSpecialPackage(SpecialPackages.RWDataCleaner);
                queue.Add(tools.RwDataCleaner);
            }
            if (spec.SpecialPackage != null) queue.Add(await Ctx.Api.GetSpecialPackage(spec.SpecialPackage));
        }
        catch (ApiException e)
        {
            Ctx.Log.Error("fetching special package - " + e.Message);
            await Dialogs.Error(e.Message);
            return false;
        }
        queue.AddRange(spec.Packages);
        // The autoinstall builder needs the RWDataCleaner object even for utility actions that never list it.
        if (tools.RwDataCleaner == null)
        {
            try { tools.RwDataCleaner = spec.Preloaded?.RwDataCleaner ?? await Ctx.Api.GetSpecialPackage(SpecialPackages.RWDataCleaner); }
            catch (ApiException e) { await Dialogs.Error(e.Message); return false; }
        }

        bool skipFormat = false;
        var drive = spec.Drive;
        if (!spec.DownloadOnly)
        {
            if (drive == null) return false;
            var check = await SanityCheckAsync(drive);
            if (check == null) return false;
            skipFormat = check.Value;
        }

        if (!await EnoughSpaceAsync(queue, drive, skipFormat, spec.DownloadOnly)) return false;

        var req = new UpdateRequest
        {
            Queue = queue, Tools = tools, Action = spec.Action, InstallMode = spec.Mode, Release = spec.Release,
            Region = spec.Region, ModeForced = Ctx.ModeForced, DownloadOnly = spec.DownloadOnly, Drive = drive,
            SkipFormat = skipFormat, DownloadDir = Ctx.Main.DownloadPath!, Connections = Ctx.Main.DownloadConnections,
            AppVersion = Ctx.Version, Branch = Ctx.Branch,
        };
        Ctx.Log.Info($"Starting process ({spec.Release} - {spec.Region} - {spec.MapVersion})");
        Ctx.Log.Info($"Install mode: {spec.Mode}{(Ctx.ModeForced ? " (forced)" : "")}; MY20: {Ctx.Profile.My20v2?.ToString() ?? "auto"}");

        MainWindowViewModel.Instance.ShowDownloads();
        _ = MainWindowViewModel.Instance.Downloads.RunAsync(req, spec);
        return true;
    }

    /// <summary>Stops before anything is downloaded or erased if the download folder or the drive cannot hold the selected packages.</summary>
    internal static async Task<bool> EnoughSpaceAsync(List<Ivsu> queue, DriveInfoModel? drive, bool skipFormat, bool downloadOnly)
    {
        var dlDir = Ctx.Main.DownloadPath!;
        var dlNeeded = SpaceCheck.DownloadNeeded(queue, dlDir);
        var dlFree = SpaceCheck.FreeBytes(dlDir);

        long targetNeeded = 0; long? targetAvailable = null;
        if (!downloadOnly && drive != null)
        {
            string? mount = string.IsNullOrEmpty(drive.MountPath) ? null : drive.MountPath;
            var syncDir = !skipFormat || mount == null ? null : Path.Combine(mount, "SyncMyRide");
            targetNeeded = SpaceCheck.TargetNeeded(queue, syncDir);
            if (drive.IsFolder)
            {
                var free = SpaceCheck.FreeBytes(drive.MountPath);
                targetAvailable = free < 0 ? null : free + (skipFormat ? 0 : SpaceCheck.FolderSize(drive.MountPath));   // a folder about to be cleared gives its space back
            }
            else if (!skipFormat) targetAvailable = drive.SizeBytes > 0 ? (long)(drive.SizeBytes * 0.98) : null;                                    // freshly formatted: the whole stick, minus file system overhead
            else if (mount != null) { var free = SpaceCheck.FreeBytes(mount); targetAvailable = free < 0 ? null : free; }
        }

        var verdict = SpaceCheck.Evaluate(dlNeeded, dlFree, targetNeeded, targetAvailable);
        if (verdict.Ok) return true;

        var parts = new List<string>();
        if (!verdict.DownloadOk) parts.Add(Ctx.T("App.SpaceDownload", Format.Bytes(verdict.DownloadNeeded), Format.Bytes(verdict.DownloadFree), dlDir, Format.Bytes(verdict.DownloadShortfall)));
        if (!verdict.TargetOk) parts.Add(Ctx.T("App.SpaceTarget", Format.Bytes(verdict.TargetNeeded), Format.Bytes(verdict.TargetAvailable ?? 0)));
        Ctx.Log.Error("Not enough free space: " + string.Join(" | ", parts).Replace("\n", " "));
        await Dialogs.Error(string.Join("\n\n", parts));
        return false;
    }

    /// <summary>Port of SanityCheckHelper.CancelDownloadCheck. Returns skipFormat, or null if the user backed out.</summary>
    private static async Task<bool?> SanityCheckAsync(DriveInfoModel drive)
    {
        var download = NormalizeDir(Ctx.Main.DownloadPath!);
        var target = NormalizeDir(drive.MountPath);
        if (string.IsNullOrEmpty(drive.MountPath) && !drive.IsFolder) target = ""; // not mounted yet; checked again after mounting

        if (target.Length > 0 && (download == target || download.StartsWith(target, StringComparison.Ordinal)))
        {
            await Dialogs.Error(Ctx.T(drive.IsFolder ? "MessageBox.CancelDownloadIsFolder" : "MessageBox.CancelDownloadIsDrive"));
            return null;
        }

        if (drive.IsFolder)
        {
            bool skip = true;
            try
            {
                Directory.CreateDirectory(drive.MountPath);
                if (Directory.EnumerateFileSystemEntries(drive.MountPath).Any())
                {
                    skip = !await Dialogs.Confirm(Ctx.T("MessageBox.OptionalFormat", drive.MountPath));
                    if (!skip && !await Dialogs.Confirm(Ctx.T("MessageBox.CancelDeleteFiles", drive.MountPath), Ctx.T("String.Warning") + "!", warning: true))
                        return null;
                    if (skip) Ctx.Log.Info("Selected folder will not be cleared before being used");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                await Dialogs.Error(e.Message); return null;
            }
            return skip;
        }

        if (drive.Encrypted) { await Dialogs.Error(Ctx.T("MessageBox.EncryptedDrive")); return null; }

        if (drive.CanSkipFormat && !string.IsNullOrEmpty(drive.MountPath))
        {
            var reformat = await Dialogs.Confirm(Ctx.T("MessageBox.OptionalFormatUSB", drive.Name, drive.MountPath));
            if (!reformat) { Ctx.Log.Info("USB Drive not formatted, using existing filesystem and files"); return true; }
        }

        // Destructive: name the exact device so the user can verify it.
        // the drive on its own line after a blank one, without the OS disk number; a drive with no letter yet shows "-" instead of nothing
        var msg = Ctx.T("MessageBox.CancelFormatUSB", $"\n\n{drive.Name} ({Format.Bytes(drive.SizeBytes)})", string.IsNullOrEmpty(drive.MountPath) ? "-" : drive.MountPath.TrimEnd('\\'));
        return await Dialogs.Confirm(msg, Ctx.T("String.Warning") + "!", warning: true) ? false : null;
    }

    private static string NormalizeDir(string p)
    {
        if (string.IsNullOrWhiteSpace(p)) return "";
        var full = Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? full.ToUpperInvariant() : full;
    }
}
