using System.Xml.Linq;

namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>macOS: diskutil (plist output). Formatting needs no root for external removable media.</summary>
public class MacDriveService : IDriveService
{
    public string OsDescription => "macOS";

    public async Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default)
    {
        var list = await ProcessRunner.RunAsync("diskutil", new[] { "list", "-plist", "external", "physical" }, ct: ct);
        if (!list.Ok) throw new IOException("diskutil failed: " + list.StdErr);
        var drives = new List<DriveInfoModel>();
        var root = Plist.Parse(list.StdOut) as Dictionary<string, object?>;
        foreach (var disk in (root?.GetValueOrDefault("AllDisksAndPartitions") as List<object?>) ?? new())
        {
            if (disk is not Dictionary<string, object?> d) continue;
            if (d.GetValueOrDefault("DeviceIdentifier") is not string id) continue;
            var info = Plist.Parse((await ProcessRunner.RunAsync("diskutil", new[] { "info", "-plist", id }, ct: ct)).StdOut) as Dictionary<string, object?> ?? new();
            if (info.GetValueOrDefault("Internal") is true) continue;
            var scheme = info.GetValueOrDefault("Content") as string;

            string fs = "", vol = "", mount = ""; long free = 0;
            foreach (var p in (d.GetValueOrDefault("Partitions") as List<object?>) ?? new())
            {
                if (p is not Dictionary<string, object?> pd || pd.GetValueOrDefault("MountPoint") is not string mp) continue;
                var pinfo = Plist.Parse((await ProcessRunner.RunAsync("diskutil", new[] { "info", "-plist", pd["DeviceIdentifier"] as string ?? "" }, ct: ct)).StdOut) as Dictionary<string, object?> ?? new();
                mount = mp; vol = pinfo.GetValueOrDefault("VolumeName") as string ?? "";
                fs = NiceFs(pinfo.GetValueOrDefault("FilesystemType") as string);
                free = Convert.ToInt64(pinfo.GetValueOrDefault("APFSContainerFree") ?? pinfo.GetValueOrDefault("FreeSpace") ?? 0L);
                break;
            }
            drives.Add(new DriveInfoModel
            {
                DeviceId = "/dev/" + id, MountPath = mount, VolumeName = vol, FileSystem = fs, FreeBytes = free,
                Name = info.GetValueOrDefault("MediaName") as string ?? id,
                Model = info.GetValueOrDefault("MediaName") as string ?? "",
                SizeBytes = Convert.ToInt64(d.GetValueOrDefault("Size") ?? 0L),
                PartitionType = scheme switch { "FDisk_partition_scheme" => "MBR", "GUID_partition_scheme" => "GPT", _ => scheme ?? "" },
            });
        }
        return drives;
    }

    private static string NiceFs(string? fs) => fs?.ToLowerInvariant() switch { "exfat" => "exFAT", "msdos" => "FAT32", "ntfs" => "NTFS", null => "", var o => o };

    public async Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default)
    {
        if (!(await ListAsync(ct)).Any(d => d.DeviceId == drive.DeviceId))
            throw new Drives.FormatException($"{drive.DeviceId} is no longer an external drive; refusing to format it.");
        log.Report($"Re-creating partition table as MBR and formatting {drive.DeviceId} as exFAT");
        var r = await ProcessRunner.RunAsync("diskutil", new[] { "eraseDisk", "ExFAT", "CYANLABS", "MBR", drive.DeviceId }, ct: ct, timeoutMs: 600_000);
        if (!r.Ok) throw new Drives.FormatException("Formatting failed: " + (r.StdErr + r.StdOut).Trim());
        await Task.Delay(2000, ct);
        return "/Volumes/CYANLABS";
    }

    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) =>
        Task.FromResult(string.IsNullOrEmpty(drive.MountPath) ? throw new Drives.FormatException("The drive is not mounted.") : drive.MountPath);

    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Minimal Apple property-list reader (dict, array, string, integer, real, true, false).</summary>
public static class Plist
{
    public static object? Parse(string xml)
    {
        var doc = XDocument.Parse(xml, LoadOptions.None);
        var el = doc.Root?.Elements().FirstOrDefault();
        return el == null ? null : Read(el);
    }

    private static object? Read(XElement e) => e.Name.LocalName switch
    {
        "dict" => ReadDict(e),
        "array" => e.Elements().Select(Read).ToList(),
        "string" => e.Value,
        "integer" => long.Parse(e.Value),
        "real" => double.Parse(e.Value, System.Globalization.CultureInfo.InvariantCulture),
        "true" => true,
        "false" => false,
        _ => e.Value,
    };

    private static Dictionary<string, object?> ReadDict(XElement e)
    {
        var d = new Dictionary<string, object?>();
        var kids = e.Elements().ToList();
        for (int i = 0; i + 1 < kids.Count; i += 2) d[kids[i].Value] = Read(kids[i + 1]);
        return d;
    }
}
