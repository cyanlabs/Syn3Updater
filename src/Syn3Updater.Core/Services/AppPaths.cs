namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>Cross-platform data locations (AppData on Windows, ~/.local/share on Linux, ~/Library/Application Support on macOS).</summary>
public class AppPaths
{
    public string DataDir { get; }
    public string ProfileDir => Path.Combine(DataDir, "Profiles");
    public string MainConfigFile => Path.Combine(DataDir, "settings.json");
    public string DefaultLogDir => Path.Combine(DataDir, "Logs");
    public string AppLogFile => Path.Combine(DataDir, "applog.txt");

    /// <summary>The data folder the app uses when nothing overrides it (the SYN3_DATA_DIR environment variable wins).</summary>
    public static string DefaultDataDir() =>
        Environment.GetEnvironmentVariable("SYN3_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanLabs", "Syn3Updater");

    public AppPaths(string? overrideDir = null)
    {
        DataDir = overrideDir ?? DefaultDataDir();
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ProfileDir);
    }

    public string ProfileFile(string profile) => Path.Combine(ProfileDir, $"{FileSafe(profile)}.json");

    public static string FileSafe(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return string.IsNullOrWhiteSpace(name) ? "default" : name;
    }

    public static string DefaultDownloadDir()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DOWNLOAD_DIR");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var downloads = !string.IsNullOrWhiteSpace(xdg) ? xdg : Path.Combine(home, "Downloads");
        return Path.Combine(downloads, "Syn3Updater");
    }
}
