using System.Text;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>Builds the log.txt that is saved next to the update files and can be uploaded for support.</summary>
public static class LogReport
{
    public static string Build(string appVersion, string branch, string os, ProfileSettings p, string installMode, bool modeForced,
        DriveInfoModel? drive, string? targetRoot, string runLog, IEnumerable<KeyValuePair<string, string>>? system = null)
    {
        const string nl = "\r\n";
        var sb = new StringBuilder($"CYANLABS - SYN3 UPDATER - V{appVersion}{nl}Branch: {branch}{nl}Operating System: {os}{nl}{nl}");
        if (system != null)
        {
            sb.Append($"SYSTEM{nl}");
            foreach (var f in system) sb.Append($"{f.Key}: {f.Value}{nl}");
            sb.Append(nl);
        }
        sb.Append($"PREVIOUS CONFIGURATION{nl}");
        sb.Append($"Version: {p.VersionString}{nl}Region: {p.CurrentRegion}{nl}Navigation: {p.CurrentNav}{nl}");
        sb.Append($"Install Mode: {p.InstallMode} ({installMode}){nl}Install Mode Overridden: {modeForced}{nl}");
        sb.Append($"My20 Protection Enabled: {p.My20v2?.ToString() ?? "Auto-Detect"}{nl}{nl}DESTINATION DETAILS{nl}");
        if (drive == null || drive.IsFolder) sb.Append($"Mode: Directory{nl}Path: {SystemInfo.Redact(targetRoot)}{nl}");
        else
        {
            sb.Append($"Mode: Drive{nl}Model: {drive.Name}{nl}Size: {Format.Bytes(drive.SizeBytes)}{nl}FileSystem: {drive.FileSystem}{nl}Partition Type: {drive.PartitionType}{nl}");
            sb.Append($"Device: {drive.DeviceId}{nl}Mount Path: {SystemInfo.Redact(drive.MountPath)}{nl}Free Space: {Format.Bytes(drive.FreeBytes)}{nl}Volume Label: {drive.VolumeName}{nl}Encrypted: {drive.Encrypted}{nl}");
        }

        if (targetRoot != null && Directory.Exists(targetRoot))
        {
            foreach (var lst in new[] { "reformat.lst", "autoinstall.lst" })
            {
                var f = Path.Combine(targetRoot, lst);
                if (File.Exists(f)) sb.Append($"{nl}{lst.ToUpperInvariant()}{nl}{File.ReadAllText(f)}{nl}");
            }
            var sync = Path.Combine(targetRoot, "SyncMyRide");
            if (Directory.Exists(sync))
            {
                var files = new DirectoryInfo(sync).GetFiles("*", SearchOption.AllDirectories);
                sb.Append($"{nl}FILES ({files.Length}){nl}");
                foreach (var f in files) sb.Append($"{f.Name} ({Format.Bytes(f.Length)}){nl}");
                sb.Append(nl);
            }
        }
        sb.Append("LOG").Append(nl).Append(runLog);
        return sb.ToString();
    }
}
