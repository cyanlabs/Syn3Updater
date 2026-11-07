using System.Diagnostics;
using System.Runtime.InteropServices;
using Cyanlabs.Syn3Updater.Core.Services.Updates;

namespace Cyanlabs.Syn3Updater.Updater;

/// <summary>
/// usage: Syn3Updater.Updater apply &lt;base64 request&gt;
/// Waits for the app to close, swaps the files of the install all-or-nothing, records the result for the app, and starts the app again (the new version, or the old one if the swap
/// was rolled back). If the update fails, says so in a message box first: the app is closed at that moment, so there is nothing else to tell the user.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 2 || args[0] != "apply") return 2;
        ApplyRequest request;
        try { request = ApplyRequest.Decode(args[1]); }
        catch (FormatException e) { Fail("Syn3 Updater could not be updated: " + e.Message); return 2; }

        var logFile = Path.Combine(Path.GetDirectoryName(request.ResultFile)!, "updater.log");
        void Log(string line) { try { File.AppendAllText(logFile, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {line}{Environment.NewLine}"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        Log($"Updating {request.InstallDir} to {request.Version} (tag {request.Tag}, {request.Channel})");

        var result = WindowsApplier.Run(request, log: Log);
        result.Write(request.ResultFile);
        if (result.Success) { try { File.Delete(request.ZipPath); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
        else Fail("Syn3 Updater could not be updated.\n\n" + result.Message);

        // start the app again either way: it is the new version after a success, and the restored old one after a failure
        try { Process.Start(new ProcessStartInfo(request.RestartExe) { UseShellExecute = false, WorkingDirectory = request.InstallDir }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { Log("Could not start " + request.RestartExe + ": " + e.Message); }
        return result.Success ? 0 : 1;
    }

    private static void Fail(string message)
    {
        if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "Syn3 Updater", 0x10 /* MB_ICONERROR */);
        else Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
