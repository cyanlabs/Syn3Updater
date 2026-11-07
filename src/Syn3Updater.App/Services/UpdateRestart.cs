using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Starts the freshly installed program and closes this one. The "one copy at a time" lock is let go first, or the new copy would think this one still runs and just hand its message over.</summary>
public static class UpdateRestart
{
    /// <summary>Starts a program (replaced by tests).</summary>
    public static Func<string, bool> Starter { get; set; } = DefaultStart;
    /// <summary>Closes the app (replaced by tests).</summary>
    public static Action Quit { get; set; } = DefaultQuit;

    /// <returns>False when the new program could not be started (this one then keeps running).</returns>
    public static bool Restart(string exe)
    {
        AppLaunch.Primary?.Dispose();                               // the new program must find the lock free, or it would hand its message to this one and exit
        AppLaunch.Primary = null;
        if (Starter(exe)) { Quit(); return true; }
        // it did not start (antivirus, a locked file): this program carries on, so it takes the lock back and is again the one copy that handles links and later launches
        if (AppLaunch.PipeName != null && SingleInstance.TryBecomePrimary(AppLaunch.PipeName) is { } again)
        {
            AppLaunch.Primary = again;
            if (AppLaunch.OnMessage != null) again.Listen(AppLaunch.OnMessage);
        }
        return false;
    }

    private static bool DefaultStart(string exe)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" });
            return p != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { Ctx.Log.Error($"Could not start {exe}: {e.Message}"); return false; }
    }

    private static void DefaultQuit() =>
        Avalonia.Threading.Dispatcher.UIThread.Post(() => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
}
