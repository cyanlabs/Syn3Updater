using System.Diagnostics;
using System.Runtime.InteropServices;
using Cyanlabs.Syn3Updater.Core.Services.Updates;

namespace Cyanlabs.Syn3Updater.Launcher;

/// <summary>TEMPORARY stub: see <see cref="LauncherStub"/>.</summary>
internal static class Program
{
    private static int Main(string[] args) =>
        LauncherStub.Run(AppContext.BaseDirectory.TrimEnd('\\', '/'), args, Start, Problem);

    private static bool Start(string exe, IReadOnlyList<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) ?? "" };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return false; }
    }

    private static void Problem(string message)
    {
        if (OperatingSystem.IsWindows()) MessageBoxW(IntPtr.Zero, message, "Syn3 Updater", 0x10 /* MB_ICONERROR */);
        else Console.Error.WriteLine(message);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
