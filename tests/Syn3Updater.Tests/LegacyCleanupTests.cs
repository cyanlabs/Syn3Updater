using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Tests;
using Xunit;

namespace Syn3Updater.Tests;

public class LegacyCleanupTests : IDisposable
{
    private readonly TempDir _t = new();
    private string Dir => _t.Sub("install");
    public void Dispose() => _t.Dispose();
    private static readonly Func<string, SemVer?> Stub = _ => SemVer.Parse("3.0.0-test20+8c75e01");

    private string Put(string rel, string content = "x")
    {
        var p = Path.Combine(Dir, rel.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, content); return p;
    }

    [Fact]
    public void Every_listed_leftover_is_removed_when_Launcher_exe_is_the_v3_stub()
    {
        Put("Launcher.exe", "stub"); Put("Syn3Updater.exe", "app");
        foreach (var f in LegacyCleanup.ObsoleteFiles) Put(f);
        var removed = LegacyCleanup.Run(Dir, Stub);
        Assert.Equal(LegacyCleanup.ObsoleteFiles.Count, removed.Count);
        foreach (var f in LegacyCleanup.ObsoleteFiles) Assert.False(File.Exists(Path.Combine(Dir, f)), f);
        Assert.True(File.Exists(Path.Combine(Dir, "Launcher.exe"))); Assert.True(File.Exists(Path.Combine(Dir, "Syn3Updater.exe")));          // the program and the stub are never touched
    }

    [Fact]
    public void The_list_never_names_a_file_the_v3_package_ships()
    {
        foreach (var shipped in new[] { "Syn3Updater.exe", "Launcher.exe", "Launcher.Core.dll", "FluentWPF.dll", "Newtonsoft.Json.dll", ".install-method", "install-manifest.json", "LICENSE", "README.txt", "uninst.exe" })
            Assert.DoesNotContain(shipped, LegacyCleanup.ObsoleteFiles, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_list_has_no_duplicates_no_paths_and_no_wildcards()
    {
        Assert.Equal(LegacyCleanup.ObsoleteFiles.Count, LegacyCleanup.ObsoleteFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(LegacyCleanup.ObsoleteFiles, f => { Assert.DoesNotContain('/', f); Assert.DoesNotContain('\\', f); Assert.DoesNotContain('*', f); Assert.DoesNotContain("..", f); });
    }

    [Fact]
    public void Nothing_is_removed_while_an_old_Launcher_is_still_in_place_because_it_needs_those_files()
    {
        Put("Launcher.exe", "old"); Put("Octokit.dll"); Put("Launcher.exe.config"); Put("Launcher_OldVersion.exe");
        Assert.Empty(LegacyCleanup.Run(Dir, _ => SemVer.Parse("2.14.2.1")));
        Assert.Empty(LegacyCleanup.Run(Dir, _ => SemVer.Parse("1.0.0.0")));
        Assert.True(File.Exists(Path.Combine(Dir, "Octokit.dll"))); Assert.True(File.Exists(Path.Combine(Dir, "Launcher.exe.config")));
    }

    [Fact]
    public void If_the_Launchers_version_cannot_be_read_nothing_is_removed()
    {
        Put("Launcher.exe", "?"); Put("Octokit.dll");
        var logs = new List<string>();
        Assert.Empty(LegacyCleanup.Run(Dir, _ => null, logs.Add));
        Assert.True(File.Exists(Path.Combine(Dir, "Octokit.dll"))); Assert.Contains(logs, l => l.Contains("cannot be read"));
    }

    [Fact]
    public void With_no_Launcher_at_all_after_the_stub_has_been_retired_the_leftovers_are_still_removed()
    {
        Put("Syn3Updater.exe"); Put("Octokit.dll"); Put("Launcher_OldVersion.exe");
        var removed = LegacyCleanup.Run(Dir, _ => throw new InvalidOperationException("must not be asked"));
        Assert.Equal(new[] { "Launcher_OldVersion.exe", "Octokit.dll" }, removed.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void A_file_the_install_manifest_lists_is_kept()
    {
        Put("Launcher.exe"); Put("Octokit.dll"); Put("Launcher.pdb"); Put("System.Memory.dll");
        Put("install-manifest.json", "{\"Version\":\"3.1.0.0\",\"Files\":[\"Launcher.exe\",\"octokit.dll\",\"System.Memory.dll\"]}");        // installed on purpose (case-insensitive, like Windows)
        var removed = LegacyCleanup.Run(Dir, Stub);
        Assert.Equal(new[] { "Launcher.pdb" }, removed);
        Assert.True(File.Exists(Path.Combine(Dir, "Octokit.dll"))); Assert.True(File.Exists(Path.Combine(Dir, "System.Memory.dll")));
    }

    [Fact]
    public void Only_the_root_is_touched_never_a_same_named_file_in_a_sub_folder_and_never_unrelated_files()
    {
        Put("Launcher.exe"); Put("tools/Octokit.dll", "someone else's"); Put("OtherApp/System.Memory.dll", "theirs"); Put("notes.txt", "mine"); Put("MyOwn.dll", "mine"); Put("backup/Launcher.pdb", "x");
        Assert.Empty(LegacyCleanup.Run(Dir, Stub));
        foreach (var f in new[] { "tools/Octokit.dll", "OtherApp/System.Memory.dll", "notes.txt", "MyOwn.dll", "backup/Launcher.pdb" }) Assert.True(File.Exists(Path.Combine(Dir, f.Replace('/', Path.DirectorySeparatorChar))), f);
    }

    [Fact]
    public void The_v2_Languages_folder_goes_only_when_it_holds_nothing_but_json_files()
    {
        Put("Launcher.exe"); Put("Languages/de.json"); Put("Languages/en-US.json");
        Assert.Contains("Languages/", LegacyCleanup.Run(Dir, Stub)); Assert.False(Directory.Exists(Path.Combine(Dir, "Languages")));
    }

    [Theory]
    [InlineData("Languages/notes.txt")] [InlineData("Languages/sub/x.json")] [InlineData("Languages/Custom.dll")]
    public void A_Languages_folder_with_anything_else_in_it_is_left_alone(string extra)
    {
        Put("Launcher.exe"); Put("Languages/de.json"); Put(extra);
        Assert.DoesNotContain("Languages/", LegacyCleanup.Run(Dir, Stub)); Assert.True(File.Exists(Path.Combine(Dir, "Languages", "de.json")));
    }

    [Fact]
    public void A_Languages_folder_the_manifest_lists_is_kept()
    {
        Put("Launcher.exe"); Put("Languages/de.json"); Put("install-manifest.json", "{\"Version\":\"3.1.0.0\",\"Files\":[\"Languages/de.json\"]}");
        Assert.DoesNotContain("Languages/", LegacyCleanup.Run(Dir, Stub)); Assert.True(File.Exists(Path.Combine(Dir, "Languages", "de.json")));
    }

    [Fact]
    public void Running_it_again_changes_nothing_and_a_missing_folder_is_fine()
    {
        Put("Launcher.exe"); Put("Octokit.dll");
        Assert.Single(LegacyCleanup.Run(Dir, Stub)); Assert.Empty(LegacyCleanup.Run(Dir, Stub));
        Assert.Empty(LegacyCleanup.Run(_t.Sub("nowhere"), Stub));
    }

    [Fact]
    public void A_file_that_cannot_be_deleted_is_logged_and_kept_for_the_next_start_and_the_rest_still_goes()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;                   // needs a folder whose files cannot be deleted
        Put("Launcher.exe"); Put("Octokit.dll"); Put("Launcher.pdb");
        var logs = new List<string>(); File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try { Assert.Empty(LegacyCleanup.Run(Dir, Stub, logs.Add)); }
        finally { File.SetUnixFileMode(Dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        Assert.Contains(logs, l => l.Contains("trying again at the next start"));
        Assert.Equal(2, LegacyCleanup.Run(Dir, Stub).Count);                                          // once it can be deleted, it goes
    }

    [Fact]
    public void Only_the_listed_names_match_so_a_look_alike_is_never_removed()
    {
        Put("Launcher.exe"); foreach (var f in new[] { "Octokit.dll.bak", "Octokit.dll2", "MyOctokit.dll", "Launcher_OldVersion.exe.keep", "Launcher.pdb.old", "ModernWpf.dll.config" }) Put(f);
        Assert.Empty(LegacyCleanup.Run(Dir, Stub));
    }
}

public class LegacyCleanupAppTests
{
    [AvaloniaFact]
    public async Task The_app_removes_leftovers_in_the_background_at_start_on_a_Windows_install()
    {
        using var env = new AppEnv(); using var t = new TempDir();
        var dir = t.Sub("install"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "Syn3Updater.exe"), "app"); File.WriteAllText(Path.Combine(dir, "Octokit.dll"), "x"); File.WriteAllText(Path.Combine(dir, "Launcher_OldVersion.exe"), "x");
        Ctx.Install = new InstallInfo(InstallKind.WindowsFolder, dir, UpdateRoute.InPlace, "t");
        Cyanlabs.Syn3Updater.App.ViewModels.MainWindowViewModel.CleanUpLegacyFiles(windows: true);
        Assert.False(File.Exists(Path.Combine(dir, "Octokit.dll"))); Assert.False(File.Exists(Path.Combine(dir, "Launcher_OldVersion.exe"))); Assert.True(File.Exists(Path.Combine(dir, "Syn3Updater.exe")));
        Assert.Contains(Ctx.Log.Lines, l => l.Contains("Removed leftover file Octokit.dll"));
        await Task.CompletedTask;
    }

    [AvaloniaTheory]
    [InlineData(InstallKind.LinuxTar)] [InlineData(InstallKind.WindowsPortable)] [InlineData(InstallKind.Development)] [InlineData(InstallKind.LinuxDeb)]
    public async Task Other_kinds_of_install_are_never_cleaned(InstallKind kind)
    {
        using var env = new AppEnv(); using var t = new TempDir();
        var dir = t.Sub("install"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "Octokit.dll"), "x");
        Ctx.Install = new InstallInfo(kind, dir, UpdateRoute.Manual, "t");
        Cyanlabs.Syn3Updater.App.ViewModels.MainWindowViewModel.CleanUpLegacyFiles(windows: true);
        Assert.True(File.Exists(Path.Combine(dir, "Octokit.dll")));
        await Task.CompletedTask;
    }

    [AvaloniaFact]
    public void Off_Windows_nothing_happens_even_for_a_WindowsFolder()
    {
        using var env = new AppEnv(); using var t = new TempDir();
        var dir = t.Sub("install"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "Octokit.dll"), "x");
        Ctx.Install = new InstallInfo(InstallKind.WindowsFolder, dir, UpdateRoute.InPlace, "t");
        Cyanlabs.Syn3Updater.App.ViewModels.MainWindowViewModel.CleanUpLegacyFiles(windows: false);
        Assert.True(File.Exists(Path.Combine(dir, "Octokit.dll")));
    }

    [AvaloniaFact]
    public void An_unexpected_error_in_the_cleanup_is_logged_never_thrown()
    {
        using var env = new AppEnv();
        Ctx.Install = null!;                                         // the cleanup would throw NullReferenceException
        Cyanlabs.Syn3Updater.App.ViewModels.MainWindowViewModel.CleanUpLegacyFiles(windows: true);
        Assert.Contains(Ctx.Log.Lines, l => l.Contains("Could not remove old leftover files"));
    }
}
