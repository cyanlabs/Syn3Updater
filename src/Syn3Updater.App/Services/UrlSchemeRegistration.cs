using System.Diagnostics;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>
/// Makes this copy of the app the handler for syn3updater:// links. Windows keeps handlers in the registry (per user, so no administrator rights),
/// and v2 re-wrote its entry on every start; this does the same, pointing at the running Syn3Updater.exe. Linux declares the scheme in the
/// launcher entry (install.sh and the .deb/.rpm/AUR packages) and macOS in the app bundle's Info.plist, so neither needs code.
/// </summary>
public static class UrlSchemeRegistration
{
    public const string RegistryRoot = @"Software\Classes\syn3updater";

    /// <summary>The registry values that make Windows run <paramref name="exePath"/> with the link as its argument.</summary>
    public static IReadOnlyList<(string SubKey, string? Name, string Value)> WindowsEntries(string exePath) => new (string, string?, string)[]
    {
        (RegistryRoot, null, "URL:Syn3 Updater"),
        (RegistryRoot, "URL Protocol", ""),
        (RegistryRoot + @"\DefaultIcon", null, $"\"{exePath}\",0"),
        (RegistryRoot + @"\shell\open\command", null, $"\"{exePath}\" \"%1\""),
    };

    /// <summary>True when <paramref name="exePath"/> is the real program (not `dotnet Syn3Updater.dll` during development, which must not be registered).</summary>
    public static bool IsRegistrable(string? exePath) =>
        !string.IsNullOrEmpty(exePath) && exePath[(exePath.LastIndexOfAny(new[] { '\\', '/' }) + 1)..].Equals("Syn3Updater.exe", StringComparison.OrdinalIgnoreCase);

    public static void EnsureRegistered()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var exe = Environment.ProcessPath;
            if (!IsRegistrable(exe)) return;
            var entries = WindowsEntries(exe!);
            using (var current = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(entries[^1].SubKey))
                if ((current?.GetValue(null) as string) == entries[^1].Value) return;          // already pointing at this copy
            foreach (var (subKey, name, value) in entries)
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(subKey);
                key.SetValue(name, value);
            }
            Ctx.Log.Debug("Registered as the syn3updater:// link handler");
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            Ctx.Log.Debug("Could not register the syn3updater:// link handler: " + e.Message);        // links simply will not open the app; nothing else is affected
        }
    }
}
