namespace Cyanlabs.Syn3Updater.Core.Drives;

/// <summary>
///     Notices that a drive was plugged in or removed by comparing a cheap fingerprint of the system's drives between polls.
///     A change only counts once the fingerprint is the same on two polls in a row, so the burst of changes while a stick is being
///     detected and mounted produces one refresh, after things have settled.
/// </summary>
public sealed class DriveChangeDetector
{
    private readonly Func<string?> _fingerprint;
    private string? _reported;
    private string? _pending;
    private string? _beforeReport;

    public DriveChangeDetector(Func<string?> fingerprint) { _fingerprint = fingerprint; }

    /// <summary>Takes the current state as "already known" (call after a refresh the user asked for).</summary>
    public void Prime() { _reported = Read(); _pending = null; }

    /// <returns>True when the drives changed and have stopped changing since the previous poll.</returns>
    public bool Poll()
    {
        var now = Read();
        if (now == null) return false;
        if (_reported == null) { _reported = now; return false; }       // the first poll only learns the starting state
        if (now == _reported) { _pending = null; return false; }
        if (now == _pending) { _beforeReport = _reported; _reported = now; _pending = null; return true; }
        _pending = now;
        return false;
    }

    /// <summary>The refresh for the last reported change failed: report that change again on the next settled poll instead of losing it.</summary>
    public void Retry() { if (_beforeReport != null) _reported = _beforeReport; _pending = null; }

    private string? Read()
    {
        try { return _fingerprint(); }
        catch { return null; }                                           // a failed look at the system is "no news", never a crash
    }
}

/// <summary>A cheap description of which drives exist, per operating system. It only has to change when a drive appears, disappears or (re)mounts.</summary>
public static class DriveFingerprint
{
    public static string Current() =>
        OperatingSystem.IsWindows() ? Windows() : OperatingSystem.IsMacOS() ? Mac() : Linux();

    private static readonly string[] IgnoredBlockPrefixes = { "loop", "ram", "zram", "dm-", "md", "sr", "fd", "nbd" };

    private static string Linux()
    {
        var disks = Directory.Exists("/sys/block")
            ? Directory.GetFileSystemEntries("/sys/block").Select(Path.GetFileName).OfType<string>()
                .Where(n => !IgnoredBlockPrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal))).OrderBy(n => n, StringComparer.Ordinal)
            : Enumerable.Empty<string>();
        var mounts = File.Exists("/proc/self/mountinfo") ? ParseMountInfo(File.ReadLines("/proc/self/mountinfo")) : Enumerable.Empty<string>();
        return string.Join("\n", disks.Concat(mounts));
    }

    /// <summary>The mount points a person plugs drives into (/run/media, /media, /mnt), from /proc/self/mountinfo lines.</summary>
    public static IEnumerable<string> ParseMountInfo(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var parts = line.Split(' ');
            if (parts.Length < 5) continue;
            var mount = Unescape(parts[4]);
            if (mount.StartsWith("/run/media/", StringComparison.Ordinal) || mount.StartsWith("/media/", StringComparison.Ordinal) || mount.StartsWith("/mnt/", StringComparison.Ordinal))
                yield return "m:" + mount;
        }
    }

    /// <summary>mountinfo writes spaces and tabs in paths as octal escapes (\040).</summary>
    private static string Unescape(string s)
    {
        if (!s.Contains('\\')) return s;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && s.Length >= i + 4 && IsOctal(s[i + 1]) && IsOctal(s[i + 2]) && IsOctal(s[i + 3]))
            { sb.Append((char)Convert.ToInt32(s.Substring(i + 1, 3), 8)); i += 3; }
            else sb.Append(s[i]);
        }
        return sb.ToString();
    }

    private static bool IsOctal(char c) => c >= '0' && c <= '7';

    private static string Windows() =>
        string.Join("\n", DriveInfo.GetDrives().Where(d => d.DriveType != DriveType.Network).Select(d =>
        {
            try { return d.IsReady ? $"{d.Name}|{d.DriveType}|{d.VolumeLabel}|{d.DriveFormat}|{d.TotalSize}" : $"{d.Name}|{d.DriveType}|not ready"; }
            catch (IOException) { return $"{d.Name}|{d.DriveType}|error"; }
        }).OrderBy(s => s, StringComparer.Ordinal));

    private static string Mac()
    {
        var volumes = Directory.Exists("/Volumes") ? Directory.GetFileSystemEntries("/Volumes").Select(Path.GetFileName).OfType<string>() : Enumerable.Empty<string>();
        var devices = Directory.Exists("/dev") ? Directory.GetFileSystemEntries("/dev", "disk*").Select(Path.GetFileName).OfType<string>() : Enumerable.Empty<string>();
        return string.Join("\n", volumes.Select(v => "v:" + v).Concat(devices.Select(d => "d:" + d)).OrderBy(s => s, StringComparer.Ordinal));
    }
}
