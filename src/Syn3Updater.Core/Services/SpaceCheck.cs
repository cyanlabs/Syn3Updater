using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>What a run needs, and whether the disks involved can hold it.</summary>
public record SpaceVerdict(long DownloadNeeded, long DownloadFree, long TargetNeeded, long? TargetAvailable)
{
    public bool DownloadOk => DownloadFree < 0 || DownloadFree >= DownloadNeeded;
    public bool TargetOk => TargetAvailable is null || TargetAvailable >= TargetNeeded;
    public bool Ok => DownloadOk && TargetOk;
    public long DownloadShortfall => Math.Max(0, DownloadNeeded - Math.Max(0, DownloadFree));
    public long TargetShortfall => TargetAvailable is { } a ? Math.Max(0, TargetNeeded - a) : 0;
}

public static class SpaceCheck
{
    /// <summary>Headroom on top of the packages: temp files, file system overhead and the small .lst/log files.</summary>
    public static long WithMargin(long bytes) => bytes == 0 ? 0 : bytes + bytes / 100 + 32L * 1024 * 1024;

    /// <summary>
    /// Bytes still to download: files already present in the download folder with the expected size are not counted.
    /// Naviextras bundles are unpacked next to the bundle, so they need room twice.
    /// </summary>
    public static long DownloadNeeded(IEnumerable<Ivsu> queue, string downloadDir)
    {
        long total = 0;
        foreach (var i in queue)
        {
            if (!HasFileOfSize(Path.Combine(downloadDir, i.FileName), i.FileSize)) total += i.FileSize;
            if (i.Source == "naviextras") total += i.FileSize;
        }
        return WithMargin(total);
    }

    /// <summary>
    /// Bytes to put on the drive. Files that are already on it with the right size (re-using a prepared stick) are not counted.
    /// A naviextras bundle is not copied itself but its unpacked contents are about the same size, so it counts.
    /// </summary>
    public static long TargetNeeded(IEnumerable<Ivsu> queue, string? syncMyRideDir)
    {
        long total = 0;
        foreach (var i in queue)
        {
            if (syncMyRideDir != null && i.Source != "naviextras" && HasFileOfSize(Path.Combine(syncMyRideDir, i.FileName), i.FileSize)) continue;
            total += i.FileSize;
        }
        return WithMargin(total);
    }

    public static SpaceVerdict Evaluate(long downloadNeeded, long downloadFree, long targetNeeded, long? targetAvailable) =>
        new(downloadNeeded, downloadFree, targetNeeded, targetAvailable);

    /// <summary>Free bytes on the volume holding <paramref name="path"/> (the nearest existing parent is used); -1 when it cannot be determined.</summary>
    public static long FreeBytes(string path)
    {
        try
        {
            var probe = Path.GetFullPath(path);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } parent) probe = parent;
            return new DriveInfo(probe).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return -1; }
    }

    /// <summary>Total size of everything inside a folder (used when a folder target is about to be cleared).</summary>
    public static long FolderSize(string dir)
    {
        try { return Directory.Exists(dir) ? new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
    }

    private static bool HasFileOfSize(string path, long size) =>
        size > 0 && File.Exists(path) && new FileInfo(path).Length == size;
}
