using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // SYN3_DEBUG=1 prints Avalonia binding/layout warnings to the console.
        if (Environment.GetEnvironmentVariable("SYN3_DEBUG") == "1")
            Trace.Listeners.Add(new ConsoleTraceListener(true));
        InstallCrashLog();

        // One copy per user: a second launch (typically a syn3updater:// link clicked while the app is open) hands its link to the first and exits.
        AppLaunch.StartupLink = UrlIntentParser.Find(args);
        var pipe = SingleInstance.NameFor(AppPaths.DefaultDataDir());
        var primary = SingleInstance.TryBecomePrimary(pipe);
        if (primary == null && SingleInstance.TrySendAsync(pipe, AppLaunch.StartupLink ?? "activate").GetAwaiter().GetResult()) return 0;
        // (if nobody answered, the other copy is still starting up or stuck: carry on as a normal start rather than refusing to open)
        AppLaunch.Primary = primary;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Writes any unhandled error to crash.txt in the data folder, so a crash on someone else's machine leaves something to send.</summary>
    private static void InstallCrashLog()
    {
        static void Write(string kind, object? error)
        {
            try
            {
                var dir = Environment.GetEnvironmentVariable("SYN3_DATA_DIR") is { Length: > 0 } d ? d : Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanLabs", "Syn3Updater");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {kind} on {System.Runtime.InteropServices.RuntimeInformation.OSDescription} " +
                    $"({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}){Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
                // remembered so the next start can offer to send it (only if the user agrees; see CrashReports)
                CrashReporter.SavePending(dir, CrashReporter.Capture(kind, error, Ctx.Version, Ctx.Branch,
                    System.Runtime.InteropServices.RuntimeInformation.OSDescription, System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString()));
            }
            catch { /* nothing more we can do */ }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Unhandled exception", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Write("Unobserved task exception", e.Exception);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace(LogEventLevel.Warning);
}
