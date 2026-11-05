using System.Diagnostics;
using Avalonia;
using Avalonia.Logging;

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
            }
            catch { /* nothing more we can do */ }
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("Unhandled exception", e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Write("Unobserved task exception", e.Exception);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace(LogEventLevel.Warning);
}
