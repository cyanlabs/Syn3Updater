namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>A removable drive, or the special "download to a folder" pseudo-drive.</summary>
public class DriveInfoModel
{
    /// <summary>OS device id: PHYSICALDRIVE number (Windows), /dev/sdX (Linux), /dev/diskN (macOS). Empty for a plain folder.</summary>
    public string DeviceId { get; set; } = "";
    /// <summary>Where files are written: a drive letter (E:\), mount point (/run/media/..) or chosen folder.</summary>
    public string MountPath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Model { get; set; } = "";
    public string VolumeName { get; set; } = "";
    public long SizeBytes { get; set; }
    public long FreeBytes { get; set; }
    public string FileSystem { get; set; } = "";
    public string PartitionType { get; set; } = "";
    public bool Encrypted { get; set; }
    public bool IsFolder { get; set; }
    /// <summary>The last dropdown entry ("Select A Directory..."): choosing it opens a folder picker instead of selecting a drive.</summary>
    public bool IsChooser { get; set; }

    /// <summary>An exFAT/MBR drive already labelled CYANLABS can be reused without reformatting.</summary>
    public bool CanSkipFormat => FileSystem.Equals("exFAT", StringComparison.OrdinalIgnoreCase)
                                 && PartitionType == "MBR" && VolumeName == "CYANLABS";

    /// <summary>"E: SanDisk Ultra (CYANLABS)" on Windows (drive letter is short); name and label elsewhere (mount paths are long).</summary>
    public string Title => IsFolder || IsChooser ? Name
        : $"{(OperatingSystem.IsWindows() ? MountPath.TrimEnd('\\') + " " : "")}{Name}{(string.IsNullOrEmpty(VolumeName) ? "" : $" ({VolumeName})")}".Trim();
    /// <summary>Set by the app so the "Free / Size / File System" labels follow the chosen language; English until then.</summary>
    public static Func<string, string> Label { get; set; } = key => key switch { "free" => "Free", "size" => "Size", _ => "File System" };
    public string Detail => IsFolder || IsChooser ? "" : $"{Label("free")}: {Format.Bytes(FreeBytes)}   {Label("size")}: {Format.Bytes(SizeBytes)}   {Label("fs")}: {FileSystem}{(PartitionType == "" ? "" : $" ({PartitionType})")}";

    public string Display => IsFolder || IsChooser ? Name : $"{Name} ({Format.Bytes(SizeBytes)}){(string.IsNullOrEmpty(MountPath) ? "" : " - " + MountPath)}";
    public override string ToString() => Display;
}

public static class Format
{
    public static string Bytes(long bytes)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB" };
        if (bytes == 0) return "0B";
        long abs = Math.Abs(bytes);
        int place = (int)Math.Floor(Math.Log(abs, 1024));
        place = Math.Min(place, suf.Length - 1);
        return $"{Math.Sign(bytes) * Math.Round(abs / Math.Pow(1024, place), 1)}{suf[place]}";
    }
}

public class FormatException(string message) : Exception(message);

public record EjectResult(bool Ok, string Message);

/// <summary>Runs an external program. Injectable so the exact commands each platform issues can be verified without hardware.</summary>
public delegate Task<ProcessResult> CommandRunner(string file, IEnumerable<string> args, string? stdin, CancellationToken ct, int timeoutMs);

public interface IDriveService
{
    /// <summary>Lists USB / external drives that are safe candidates to write to.</summary>
    Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default);

    /// <summary>Wipes the drive, creates one MBR partition formatted exFAT labelled CYANLABS and returns the mount path.</summary>
    Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default);

    /// <summary>Makes sure the drive has a usable mount path (mounts it if the OS has not), returning that path.</summary>
    Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default);

    /// <summary>Flushes pending writes so the stick can be unplugged.</summary>
    Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default);

    /// <summary>
    /// Unmounts the drive and, where the OS supports it, powers it off so it can be pulled out safely.
    /// <paramref name="mountPath"/> is where the drive is mounted now (a format may have changed it).
    /// </summary>
    Task<EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default);

    string OsDescription { get; }
}

public static class DriveServiceFactory
{
    public static IDriveService Create() =>
        OperatingSystem.IsWindows() ? new WindowsDriveService()
        : OperatingSystem.IsMacOS() ? new MacDriveService()
        : new LinuxDriveService();
}
