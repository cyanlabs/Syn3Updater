using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// The Windows Launcher (v2, and the 2.14.2.1+ one) keeps the release channel it follows in launcherPrefs.json under the shared program-data folder, and v2's
/// Settings page wrote to that file when you changed it. v3 does the same, so the channel chosen in the app is the one the Launcher installs. The file is plain
/// Newtonsoft JSON: {"ReleaseBranch":1,"ReleaseInstalled":"2.14.2.1","ReleaseTypeInstalled":1} with ReleaseBranch 0 = Stable, 1 = Beta, 2 = Alpha.
/// </summary>
public static class LauncherPrefs
{
    public const int Stable = 0, Beta = 1, Alpha = 2;

    /// <summary>
    /// Windows only: the Launcher exists only there. Null on Linux and macOS, where CommonApplicationData would point somewhere like /usr/share and nothing
    /// should ever be read or written, so every caller that starts from this path does nothing off Windows.
    /// </summary>
    public static string? DefaultPath() => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CyanLabs", "Syn3Updater", "launcherPrefs.json")
        : null;

    /// <summary>The Launcher only knows Stable and Beta here (v2's own settings page treated Alpha as Beta), and it only reads the production repo, so Nightly is written as Beta.</summary>
    public static int ToBranch(UpdateChannel channel) => channel == UpdateChannel.Stable ? Stable : Beta;
    public static UpdateChannel FromBranch(int branch) => branch == Stable ? UpdateChannel.Stable : UpdateChannel.Beta;

    /// <summary>True when there is a Launcher to tell: its prefs file exists, or a Launcher.exe sits next to this program.</summary>
    public static bool HasLauncher(string? path, string? appDir = null) =>
        path != null && (File.Exists(path) || File.Exists(Path.Combine(appDir ?? AppContext.BaseDirectory, "Launcher.exe")));

    /// <summary>The channel stored in the file (0, 1 or 2), or null when there is no file or it cannot be read.</summary>
    public static int? ReadBranch(string? path)
    {
        try
        {
            if (path == null || !File.Exists(path)) return null;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root || root["ReleaseBranch"] is not JsonValue v) return null;
            if (v.TryGetValue<int>(out var i)) return i is >= Stable and <= Alpha ? i : null;              // what Newtonsoft writes: the enum as a number
            if (v.TryGetValue<string>(out var s))                                                         // ...and it reads names too, so accept them
            {
                if (int.TryParse(s, out var n)) return n is >= Stable and <= Alpha ? n : null;
                return Enum.TryParse<LauncherBranch>(s, ignoreCase: true, out var b) ? (int)b : null;
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    /// <summary>Sets ReleaseBranch and leaves every other field (what is installed, and from which channel) exactly as the Launcher keeps it. False when the file cannot be written.</summary>
    public static bool WriteBranch(string? path, int branch)
    {
        if (path == null) return false;
        try
        {
            var root = new JsonObject();
            if (File.Exists(path))
                try { root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject(); }
                catch (JsonException) { /* a corrupt file: the Launcher itself replaces those with defaults */ }
            root["ReleaseBranch"] = branch;
            root["ReleaseInstalled"] ??= "0.0.0.0";
            root["ReleaseTypeInstalled"] ??= Stable;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, root.ToJsonString());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>
    /// The channel to use: the one chosen in the app, unless the Launcher's file says something else (it was changed there, or by v2), in which case the file wins,
    /// as it did in v2. A Nightly choice is stored as Beta, so Beta in the file agrees with it.
    /// </summary>
    public static UpdateChannel Reconcile(UpdateChannel chosen, int? launcherBranch)
    {
        if (launcherBranch is not int b) return chosen;
        var branch = b == Alpha ? Beta : b;
        return ToBranch(chosen) == branch ? chosen : FromBranch(branch);
    }

    private enum LauncherBranch { Stable, Beta, Alpha }
}
