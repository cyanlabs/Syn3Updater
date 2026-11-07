using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>
/// What Launcher.exe does now. The v2 installer pointed the Desktop and Start Menu shortcuts and the syn3updater:// handler at it, and the v2 Launcher needs a file of that name in an
/// update package, so a stub stays for a few months: it undoes an update that was cut short (without which a missing Syn3Updater.exe could never start, and so never recover itself),
/// then starts Syn3Updater.exe with the arguments it was given (the link, for the handler). The app checks for and installs updates itself, so the stub does nothing else.
/// TEMPORARY: remove it, with src/Syn3Updater.Launcher, a few months after 3.0.0.0 (see docs/RELEASING.md).
/// </summary>
public static class LauncherStub
{
    /// <returns>0 when the app was started.</returns>
    /// <param name="installDir">The folder the stub lives in, which is the app's.</param>
    /// <param name="start">Starts a program with arguments; false if it could not be started.</param>
    /// <param name="problem">Tells the user something went wrong (a message box).</param>
    public static int Run(string installDir, IReadOnlyList<string> args, Func<string, IReadOnlyList<string>, bool> start, Action<string> problem, Action<string>? log = null)
    {
        log ??= _ => { };
        try { if (InstallTransaction.Recover(installDir)) log("Rolled back an interrupted update"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log("Could not clean up after an update: " + e.Message); }

        var app = Path.Combine(installDir, PackageStager.AppExe);
        if (!File.Exists(app)) { problem($"{PackageStager.AppExe} was not found in {installDir}. Please reinstall Syn3 Updater from https://cyanlabs.net."); return 1; }
        if (!start(app, args)) { problem($"{PackageStager.AppExe} could not be started."); return 1; }
        return 0;
    }
}
