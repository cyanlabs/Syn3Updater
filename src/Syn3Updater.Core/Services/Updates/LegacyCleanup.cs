using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>
/// Removes files that older versions left in the Windows install folder and that nothing uses any more: the v2 WPF app's libraries and language files (v3 is a single file), the old Launcher's own
/// copy of itself, its config and its debug symbols. A hop through an old Launcher or an update can leave some behind (the old Launcher's own files are locked while it runs), and without an
/// install manifest nothing else ever removes them.
/// It is deliberately narrow: only EXACT names from the v2 and early-v3 packages, only in the folder's root (plus the Languages folder when it holds nothing but .json files), never a file the
/// install manifest lists, and never while an old Launcher is still in place (it needs Octokit.dll and its config). Best effort: a file still in use stays for the next start.
/// </summary>
public static class LegacyCleanup
{
    /// <summary>What v2 (2.0 to 2.14.2.2) and the early v3 test packages put in the install folder, minus everything the v3 package itself ships (Syn3Updater.exe, Launcher.exe, Launcher.Core.dll, FluentWPF.dll, Newtonsoft.Json.dll).</summary>
    public static readonly IReadOnlyList<string> ObsoleteFiles = new[]
    {
        // the old Launcher's own leftovers
        "Launcher_OldVersion.exe", "Launcher.exe.config", "Launcher.pdb", "Launcher.Core.pdb", "Syn3Updater.pdb", "Octokit.dll",
        // the v2 WPF app's libraries
        "AsyncAwaitBestPractices.dll", "AsyncAwaitBestPractices.MVVM.dll", "GraphQL.Client.Abstractions.dll", "GraphQL.Client.Abstractions.Websocket.dll", "GraphQL.Client.dll",
        "GraphQL.Client.Serializer.Newtonsoft.dll", "GraphQL.Primitives.dll", "HtmlToXamlConverter.dll", "ICSharpCode.SharpZipLib.dll", "MahApps.Metro.IconPacks.Core.dll",
        "MahApps.Metro.IconPacks.VaadinIcons.dll", "ModernWpf.Controls.dll", "ModernWpf.dll", "Nito.AsyncEx.Context.dll", "Nito.AsyncEx.Coordination.dll", "Nito.AsyncEx.Interop.WaitHandles.dll",
        "Nito.AsyncEx.Oop.dll", "Nito.AsyncEx.Tasks.dll", "Nito.Cancellation.dll", "Nito.Collections.Deque.dll", "Nito.Disposables.dll", "Ookii.Dialogs.Wpf.dll", "QRCoder.dll",
        "System.Buffers.dll", "System.CodeDom.dll", "System.Collections.Immutable.dll", "System.Memory.dll", "System.Net.WebSockets.Client.Managed.dll", "System.Numerics.Vectors.dll",
        "System.Reactive.dll", "System.Runtime.CompilerServices.Unsafe.dll", "System.Threading.Tasks.Extensions.dll", "System.ValueTuple.dll"
    };

    public const string LanguagesFolder = "Languages";

    /// <param name="launcherVersion">The version of the Launcher.exe at that path, or null when it cannot be read.</param>
    /// <returns>What was removed.</returns>
    public static IReadOnlyList<string> Run(string installDir, Func<string, SemVer?> launcherVersion, Action<string>? log = null)
    {
        log ??= _ => { };
        var removed = new List<string>();
        if (!Directory.Exists(installDir)) return removed;

        // an old Launcher still in place needs Octokit.dll and its config: only clean up once Launcher.exe is the v3 stub (or gone)
        var launcher = Path.Combine(installDir, "Launcher.exe");
        if (File.Exists(launcher))
        {
            var v = launcherVersion(launcher);
            if (v == null) { log("Not removing old files: the version of Launcher.exe cannot be read"); return removed; }
            if (v.Numbers[0] < 3) { log($"Not removing old files: Launcher.exe is still an older Launcher ({string.Join('.', v.Numbers)})"); return removed; }
        }

        // anything an install put there on purpose is never touched
        var keep = new HashSet<string>(InstallManifest.Load(installDir)?.Files ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        foreach (var name in ObsoleteFiles)
        {
            if (keep.Contains(name)) continue;
            var path = Path.Combine(installDir, name);
            if (!File.Exists(path)) continue;
            try { File.Delete(path); removed.Add(name); log($"Removed leftover file {name}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Could not remove leftover file {name} yet ({e.Message}); trying again at the next start"); }
        }

        var languages = Path.Combine(installDir, LanguagesFolder);
        try
        {
            if (Directory.Exists(languages) && !keep.Any(f => f.StartsWith(LanguagesFolder + "/", StringComparison.OrdinalIgnoreCase)))
            {
                var entries = Directory.GetFileSystemEntries(languages);
                if (entries.All(e => File.Exists(e) && e.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))        // only the v2 language files, nothing else
                {
                    Directory.Delete(languages, recursive: true); removed.Add(LanguagesFolder + "/"); log("Removed leftover folder Languages");
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log($"Could not remove the leftover Languages folder yet ({e.Message}); trying again at the next start"); }
        return removed;
    }
}
