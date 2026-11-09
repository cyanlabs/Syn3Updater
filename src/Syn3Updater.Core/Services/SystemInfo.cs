using System.Globalization;
using System.Runtime.InteropServices;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>Facts about the machine and the app's environment, for the app log, log.txt and the Diagnostics page.</summary>
public static class SystemInfo
{
    /// <summary>"Fedora Linux 42 (Workstation Edition) (X64)" on Linux (from os-release), the OS description elsewhere.</summary>
    public static string OsName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
            {
                var line = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal));
                if (line != null) return line["PRETTY_NAME=".Length..].Trim('"') + " (" + RuntimeInformation.OSArchitecture + ")";
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        if (OperatingSystem.IsWindows()) return WindowsName(Environment.OSVersion.Version) + " (" + RuntimeInformation.OSArchitecture + ")";
        return RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
    }

    /// <summary>
    /// "Windows 11 (10.0.26200)". Windows 11 still reports version 10.0 (Microsoft kept the major version), so the .NET description says "Windows 10.0.26200" and looks like Windows 10.
    /// Build 22000 is the first Windows 11 build.
    /// </summary>
    public static string WindowsName(Version v)
    {
        var number = $"{v.Major}.{v.Minor}.{Math.Max(v.Build, 0)}";
        if (v.Major == 10 && v.Minor == 0) return (v.Build >= 22000 ? "Windows 11 (" : "Windows 10 (") + number + ")";
        return "Windows " + number;
    }

    /// <summary>Replaces the user's home folder with "~" so logs that get uploaded or pasted do not contain the account name.</summary>
    public static string Redact(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 1 && path.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~" + path[home.Length..] : path;
    }

    /// <summary>Ordered key/value facts. <paramref name="exists"/> checks whether a command is installed (injectable for tests).</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Collect(string appVersion, string branch, string? dataDir = null, string? downloadDir = null,
        Func<string, bool>? exists = null, Func<string, string?>? env = null, string? installMethod = null)
    {
        exists ??= Drives.ProcessRunner.Exists;
        env ??= Environment.GetEnvironmentVariable;
        var list = new List<KeyValuePair<string, string>>();
        void Add(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) list.Add(new(k, v)); }

        Add("App version", $"{appVersion} ({branch})");
        Add("Operating system", OsName());
        Add("OS description", RuntimeInformation.OSDescription);
        Add("Architecture", $"{RuntimeInformation.OSArchitecture} (process {RuntimeInformation.ProcessArchitecture})");
        Add(".NET runtime", RuntimeInformation.FrameworkDescription);
        Add("Runtime identifier", RuntimeInformation.RuntimeIdentifier);
        Add("Processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        try
        {
            var mem = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (mem > 0) Add("Memory", Drives.Format.Bytes(mem));
        }
        catch (InvalidOperationException) { }
        Add("Elevated (administrator/root)", Environment.IsPrivilegedProcess ? "yes" : "no");
        Add("UI culture", CultureInfo.CurrentUICulture.Name);
        Add("Time zone", TimeZoneInfo.Local.Id);
        Add("Install location", Redact(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)));
        Add("Install method", installMethod);

        if (OperatingSystem.IsLinux())
        {
            Add("Desktop", env("XDG_CURRENT_DESKTOP"));
            Add("Session type", env("XDG_SESSION_TYPE"));
            Add("Locale", env("LANG"));
            var packaging = new List<string>();
            if (!string.IsNullOrEmpty(env("APPIMAGE"))) packaging.Add("AppImage");
            if (File.Exists("/.flatpak-info")) packaging.Add("Flatpak");
            if (!string.IsNullOrEmpty(env("SNAP"))) packaging.Add("Snap");
            if (!string.IsNullOrEmpty(env("WSL_DISTRO_NAME"))) packaging.Add("WSL");
            if (File.Exists("/.dockerenv")) packaging.Add("container");
            Add("Sandbox", packaging.Count == 0 ? "none" : string.Join(", ", packaging));
        }

        Add("Data folder", Redact(dataDir));
        Add("Download folder", Redact(downloadDir));
        foreach (var (label, dir) in new[] { ("Data folder free", dataDir), ("Download folder free", downloadDir) })
            Add(label, FreeSpace(dir));

        var tools = ToolsFor().Select(t => $"{t}={(exists(t) ? "yes" : "NO")}").ToList();
        if (tools.Count > 0) Add("Tools", string.Join(", ", tools));
        return list;
    }

    /// <summary>The external programs the current OS's drive code depends on.</summary>
    public static string[] ToolsFor() =>
        OperatingSystem.IsWindows() ? new[] { "powershell", "diskpart" }
        : OperatingSystem.IsMacOS() ? new[] { "diskutil" }
        : new[] { "lsblk", "findmnt", "sfdisk", "wipefs", "mkfs.exfat", "pkexec", "udisksctl", "sync", "xdg-open" };

    public static string? FreeSpace(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir)) return null;
            var root = Path.GetPathRoot(Path.GetFullPath(dir));
            // walk up to an existing folder so a download folder that has not been created yet still reports
            var probe = Path.GetFullPath(dir);
            while (!Directory.Exists(probe) && Path.GetDirectoryName(probe) is { Length: > 0 } up) probe = up;
            return Drives.Format.Bytes(new DriveInfo(Directory.Exists(probe) ? probe : root ?? probe).AvailableFreeSpace);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { return null; }
    }

    /// <summary>The facts as "Key: value" lines.</summary>
    public static string ToText(IEnumerable<KeyValuePair<string, string>> facts, string newline = "\n") =>
        string.Join(newline, facts.Select(f => $"{f.Key}: {f.Value}"));
}
