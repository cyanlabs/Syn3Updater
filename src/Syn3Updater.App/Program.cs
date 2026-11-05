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
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace(LogEventLevel.Warning);
}
