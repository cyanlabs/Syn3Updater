namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>What a shortcut does, as far as this migration cares.</summary>
public sealed record ShortcutInfo(string Target, string Arguments, string WorkingDirectory, string Description, string IconPath, int IconIndex);

public interface IShortcutStore
{
    /// <summary>The shortcuts that could be ours: the Desktop, Start Menu and taskbar of everyone and of the current user.</summary>
    IEnumerable<string> FindShortcuts();
    ShortcutInfo? Read(string path);
    void Write(string path, ShortcutInfo info);
}

public interface IProtocolStore
{
    /// <summary>The command Windows runs for syn3updater:// links machine-wide (null when there is none).</summary>
    string? GetOpenCommand();
    void SetOpenCommand(string command);
}

public sealed record MigrationResult(IReadOnlyList<string> Shortcuts, bool ProtocolFixed, bool Complete);

/// <summary>
/// The v2 installer made shortcuts (Desktop, Start Menu) and the machine-wide syn3updater:// handler point at Launcher.exe. Now that the app updates itself, they should open
/// Syn3Updater.exe directly: it is the program people mean, and the Launcher can then be retired without leaving broken shortcuts or links. Only things that point at THIS install's
/// Launcher.exe are touched (never the uninstall shortcut, never another copy of the program), and running it again changes nothing.
/// </summary>
public static class LauncherMigration
{
    public const string LauncherExe = "Launcher.exe", AppExe = "Syn3Updater.exe";

    public static MigrationResult Run(string installDir, IShortcutStore shortcuts, IProtocolStore protocol, Action<string>? log = null)
    {
        log ??= _ => { };
        var launcher = Path.Combine(installDir, LauncherExe); var app = Path.Combine(installDir, AppExe);
        var changed = new List<string>(); var complete = true;
        if (!File.Exists(app)) { log($"Not migrating: {app} does not exist"); return new MigrationResult(changed, false, false); }

        IEnumerable<string> found;
        try { found = shortcuts.FindShortcuts().ToList(); }
        catch (Exception e) { log("Could not list shortcuts: " + e.Message); found = Array.Empty<string>(); complete = false; }

        foreach (var lnk in found)
        {
            try
            {
                if (shortcuts.Read(lnk) is not { } info || !Same(info.Target, launcher)) continue;
                var workDir = string.IsNullOrWhiteSpace(info.WorkingDirectory) || Same(info.WorkingDirectory, installDir) ? installDir : info.WorkingDirectory;
                var icon = Same(info.IconPath, launcher) ? app : info.IconPath;            // an icon of its own is the user's choice; the Launcher's icon becomes the app's
                shortcuts.Write(lnk, info with { Target = app, WorkingDirectory = workDir, IconPath = icon, IconIndex = Same(info.IconPath, launcher) ? 0 : info.IconIndex });
                changed.Add(lnk); log($"Shortcut {lnk} now opens {AppExe}");
            }
            catch (Exception e)                                      // best effort, and it runs in the background: whatever goes wrong with one shortcut (COM, rights, anything) is logged, never thrown
            {
                log($"Could not update the shortcut {lnk}: {e.GetType().Name}: {e.Message}"); complete = false;
            }
        }

        var protocolFixed = false;
        try
        {
            if (protocol.GetOpenCommand() is { Length: > 0 } cmd && PointsAtLauncher(cmd, launcher))
            {
                protocol.SetOpenCommand($"\"{app}\" \"%1\"");
                protocolFixed = true; log($"The syn3updater:// handler now opens {AppExe}");
            }
        }
        catch (Exception e)
        {
            log("Could not update the syn3updater:// handler: " + e.Message); complete = false;
        }
        return new MigrationResult(changed, protocolFixed, complete);
    }

    /// <summary>True when the registry command runs this install's Launcher.exe (the v2 installer wrote it without quotes: C:\...\Launcher.exe %1).</summary>
    internal static bool PointsAtLauncher(string command, string launcher) =>
        command.Contains(launcher, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) &&
        string.Equals(Normalise(a!), Normalise(b!), StringComparison.OrdinalIgnoreCase);

    private static string Normalise(string p) => p.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
}
