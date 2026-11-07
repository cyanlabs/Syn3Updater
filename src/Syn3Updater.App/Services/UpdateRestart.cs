using System.Diagnostics;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;

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
        AppLaunch.Primary?.Dispose();
        AppLaunch.Primary = null;
        if (!Starter(exe)) return false;
        Quit();
        return true;
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
