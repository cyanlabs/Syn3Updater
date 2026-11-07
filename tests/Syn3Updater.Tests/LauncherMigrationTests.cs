using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Windows;
using Xunit;

namespace Syn3Updater.Tests;

internal sealed class FakeShortcuts : IShortcutStore
{
    public Dictionary<string, ShortcutInfo> Items { get; } = new();
    public List<string> Written { get; } = new();
    public HashSet<string> FailWrite { get; } = new();
    public HashSet<string> Unreadable { get; } = new();
    public bool FailList { get; set; }
    public Exception? ReadThrows { get; set; }
    public IEnumerable<string> FindShortcuts() => FailList ? throw new UnauthorizedAccessException("denied") : Items.Keys.ToList();
    public ShortcutInfo? Read(string p) => ReadThrows != null ? throw ReadThrows : Unreadable.Contains(p) ? null : Items[p];
    public void Write(string p, ShortcutInfo i) { if (FailWrite.Contains(p)) throw new UnauthorizedAccessException("denied"); Items[p] = i; Written.Add(p); }
}

internal sealed class FakeProtocol : IProtocolStore
{
    public string? Command { get; set; }
    public bool Fail { get; set; }
    public string? GetOpenCommand() => Command;
    public void SetOpenCommand(string c) { if (Fail) throw new UnauthorizedAccessException("denied"); Command = c; }
}

public class LauncherMigrationTests : IDisposable
{
    private readonly TempDir _t = new();
    private string Install => _t.Sub("Program Files", "CyanLabs", "Syn3Updater");
    private string Launcher => Path.Combine(Install, "Launcher.exe"); private string App => Path.Combine(Install, "Syn3Updater.exe");
    public void Dispose() => _t.Dispose();

    public LauncherMigrationTests() { }
    private void Present() { Directory.CreateDirectory(Install); File.WriteAllText(App, "a"); File.WriteAllText(Launcher, "l"); }

    private static ShortcutInfo Lnk(string target, string args = "", string dir = "", string desc = "Syn3Updater", string icon = "", int index = 0) => new(target, args, dir, desc, icon, index);

    [Fact]
    public void Shortcuts_to_this_installs_Launcher_are_pointed_at_the_app_and_keep_their_other_settings()
    {
        Present(); var s = new FakeShortcuts(); var p = new FakeProtocol();
        s.Items["Public Desktop/Syn3Updater.lnk"] = Lnk(Launcher, "--x", "", "Ford SYNC 3 updater");
        s.Items["Start Menu/Syn3Updater/Syn3Updater.lnk"] = Lnk(Launcher, "", Install, "Syn3Updater", Launcher, 0);
        var r = LauncherMigration.Run(Install, s, p);
        Assert.Equal(2, r.Shortcuts.Count); Assert.True(r.Complete);
        var d = s.Items["Public Desktop/Syn3Updater.lnk"];
        Assert.Equal(App, d.Target); Assert.Equal("--x", d.Arguments); Assert.Equal("Ford SYNC 3 updater", d.Description); Assert.Equal(Install, d.WorkingDirectory);
        var m = s.Items["Start Menu/Syn3Updater/Syn3Updater.lnk"];
        Assert.Equal(App, m.Target); Assert.Equal(App, m.IconPath); Assert.Equal(0, m.IconIndex);              // the Launcher's icon becomes the app's
    }

    [Fact]
    public void Things_that_are_not_this_installs_Launcher_are_left_alone()
    {
        Present(); var s = new FakeShortcuts();
        s.Items["uninstall.lnk"] = Lnk(Path.Combine(Install, "uninst.exe"));
        s.Items["other-copy.lnk"] = Lnk(Path.Combine(_t.Sub("elsewhere"), "Launcher.exe"));
        s.Items["notepad.lnk"] = Lnk(@"C:\Windows\notepad.exe"); s.Items["already.lnk"] = Lnk(App); s.Items["empty.lnk"] = Lnk("");
        var r = LauncherMigration.Run(Install, s, new FakeProtocol());
        Assert.Empty(r.Shortcuts); Assert.Empty(s.Written);
    }

    [Fact]
    public void A_shortcuts_own_icon_and_working_folder_are_the_users_choice()
    {
        Present(); var s = new FakeShortcuts();
        s.Items["a.lnk"] = Lnk(Launcher, "", @"D:\Mine", "x", @"C:\icons\car.ico", 3);
        LauncherMigration.Run(Install, s, new FakeProtocol());
        var a = s.Items["a.lnk"]; Assert.Equal(App, a.Target); Assert.Equal(@"D:\Mine", a.WorkingDirectory); Assert.Equal(@"C:\icons\car.ico", a.IconPath); Assert.Equal(3, a.IconIndex);
    }

    [Theory]
    [InlineData("LAUNCHER.EXE")] [InlineData("launcher.exe")]
    public void Matching_ignores_case_quotes_and_slash_direction(string name)
    {
        Present(); var s = new FakeShortcuts();
        s.Items["a.lnk"] = Lnk("\"" + Path.Combine(Install, name).Replace('/', '\\') + "\"");
        s.Items["b.lnk"] = Lnk(Path.Combine(Install, name).ToUpperInvariant());
        Assert.Equal(2, LauncherMigration.Run(Install, s, new FakeProtocol()).Shortcuts.Count);
    }

    [Fact]
    public void Running_it_again_changes_nothing()
    {
        Present(); var s = new FakeShortcuts(); var p = new FakeProtocol { Command = Launcher + " %1" };
        s.Items["a.lnk"] = Lnk(Launcher);
        LauncherMigration.Run(Install, s, p); s.Written.Clear();
        var again = LauncherMigration.Run(Install, s, p);
        Assert.Empty(again.Shortcuts); Assert.False(again.ProtocolFixed); Assert.Empty(s.Written); Assert.True(again.Complete);
    }

    [Fact]
    public void One_shortcut_that_cannot_be_written_does_not_stop_the_others_but_the_run_is_not_complete()
    {
        Present(); var s = new FakeShortcuts(); var log = new List<string>();
        s.Items["locked.lnk"] = Lnk(Launcher); s.Items["fine.lnk"] = Lnk(Launcher); s.FailWrite.Add("locked.lnk");
        var r = LauncherMigration.Run(Install, s, new FakeProtocol(), log.Add);
        Assert.Equal(new[] { "fine.lnk" }, r.Shortcuts); Assert.False(r.Complete);
        Assert.Contains(log, l => l.Contains("locked.lnk"));
        Assert.Equal(Launcher, s.Items["locked.lnk"].Target);
    }

    [Fact]
    public void An_unreadable_shortcut_is_skipped_quietly()
    {
        Present(); var s = new FakeShortcuts(); s.Items["weird.lnk"] = Lnk(Launcher); s.Unreadable.Add("weird.lnk");
        var r = LauncherMigration.Run(Install, s, new FakeProtocol());
        Assert.Empty(r.Shortcuts); Assert.True(r.Complete);
    }

    [Fact]
    public void Not_being_able_to_list_shortcuts_is_incomplete_but_the_handler_is_still_fixed()
    {
        Present(); var p = new FakeProtocol { Command = Launcher + " %1" };
        var r = LauncherMigration.Run(Install, new FakeShortcuts { FailList = true }, p);
        Assert.False(r.Complete); Assert.True(r.ProtocolFixed);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public void The_v2_link_handler_is_repaired_whether_or_not_it_was_quoted(bool quoted)
    {
        Present(); var p = new FakeProtocol { Command = quoted ? $"\"{Launcher}\" \"%1\"" : $"{Launcher} %1" };
        var r = LauncherMigration.Run(Install, new FakeShortcuts(), p);
        Assert.True(r.ProtocolFixed); Assert.Equal($"\"{App}\" \"%1\"", p.Command);
    }

    [Fact]
    public void A_link_handler_that_is_not_this_installs_Launcher_is_left_alone()
    {
        Present();
        foreach (var cmd in new[] { $"\"{App}\" \"%1\"", $"\"{_t.Sub("other")}/Launcher.exe\" \"%1\"", "", null })
        {
            var p = new FakeProtocol { Command = cmd };
            Assert.False(LauncherMigration.Run(Install, new FakeShortcuts(), p).ProtocolFixed); Assert.Equal(cmd, p.Command);
        }
    }

    [Fact]
    public void A_handler_that_cannot_be_written_is_reported_as_incomplete()
    {
        Present(); var r = LauncherMigration.Run(Install, new FakeShortcuts(), new FakeProtocol { Command = Launcher + " %1", Fail = true });
        Assert.False(r.ProtocolFixed); Assert.False(r.Complete);
    }

    [Fact]
    public void Nothing_is_touched_when_the_app_itself_is_missing()
    {
        Directory.CreateDirectory(Install); File.WriteAllText(Launcher, "l");                // no Syn3Updater.exe: a shortcut to it would point at nothing
        var s = new FakeShortcuts(); s.Items["a.lnk"] = Lnk(Launcher); var p = new FakeProtocol { Command = Launcher + " %1" };
        var r = LauncherMigration.Run(Install, s, p);
        Assert.Empty(s.Written); Assert.False(r.ProtocolFixed); Assert.False(r.Complete);
    }

    [Theory]
    [InlineData(typeof(NotSupportedException))] [InlineData(typeof(InvalidCastException))] [InlineData(typeof(InvalidOperationException))] [InlineData(typeof(System.Runtime.InteropServices.COMException))]
    public void Whatever_a_shortcut_store_throws_is_logged_never_thrown_and_the_run_is_incomplete_so_it_is_tried_again_next_start(Type exception)
    {
        Present(); var s = new FakeShortcuts { ReadThrows = (Exception)Activator.CreateInstance(exception, "boom")! }; s.Items["a.lnk"] = Lnk(Launcher); var log = new List<string>();
        var r = LauncherMigration.Run(Install, s, new FakeProtocol { Command = Launcher + " %1" }, log.Add);        // does not throw
        Assert.False(r.Complete); Assert.Empty(r.Shortcuts); Assert.True(r.ProtocolFixed);                          // the handler is still repaired
        Assert.Contains(log, l => l.Contains("a.lnk") && l.Contains(exception.Name));
    }

    /// <summary>
    /// The published app must keep built-in COM: the shortcut migration needs it. With BuiltInComInteropSupport=false (an Avalonia template default) it threw NotSupportedException in the real app
    /// on every start (a crash report each time), while every test passed because the test host has COM. The CI job "windows-self-replace" also checks the real published program end to end.
    /// </summary>
    [Fact]
    public void The_app_project_keeps_built_in_COM_enabled()
    {
        var csproj = File.ReadAllText(UpdateTestSupport.FindRepoFile(Path.Combine("src", "Syn3Updater.App", "Syn3Updater.App.csproj")));
        Assert.DoesNotContain("<BuiltInComInteropSupport>false</BuiltInComInteropSupport>", csproj);
        Assert.Contains("<BuiltInComInteropSupport>true</BuiltInComInteropSupport>", csproj);
    }

    // ---- the real Windows implementations: these run on the Windows CI runner and are skipped elsewhere ----

    [Fact]
    public void Real_shortcut_files_are_read_retargeted_and_written_back()
    {
        if (!OperatingSystem.IsWindows()) return;
        Present(); var dir = _t.Sub("lnks"); Directory.CreateDirectory(Path.Combine(dir, "sub"));
        var store = new WindowsShortcutStore(new[] { dir });
        var lnk = Path.Combine(dir, "sub", "Syn3Updater.lnk"); var other = Path.Combine(dir, "Notepad.lnk");
        store.Create(lnk, Lnk(Launcher, "--hello", Install, "Syn3 desc", Launcher, 0)); store.Create(other, Lnk(@"C:\Windows\notepad.exe"));
        Assert.Equal(2, store.FindShortcuts().Count());

        var r = LauncherMigration.Run(Install, store, new FakeProtocol());
        Assert.Equal(new[] { lnk }, r.Shortcuts);
        var back = store.Read(lnk)!;
        Assert.Equal(App, back.Target, ignoreCase: true); Assert.Equal("--hello", back.Arguments); Assert.Equal("Syn3 desc", back.Description); Assert.Equal(App, back.IconPath, ignoreCase: true);
        Assert.Equal(@"C:\Windows\notepad.exe", store.Read(other)!.Target, ignoreCase: true);        // untouched
        Assert.Empty(LauncherMigration.Run(Install, store, new FakeProtocol()).Shortcuts);            // and a second run finds nothing to do
    }

    [Fact]
    public void A_file_that_is_not_a_shortcut_does_not_break_the_scan()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dir = _t.Sub("lnks"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "bad.lnk"), "not a shortcut");
        var store = new WindowsShortcutStore(new[] { dir });
        Assert.Null(store.Read(Path.Combine(dir, "bad.lnk")));
    }

    [Fact]
    public void The_real_registry_handler_is_read_and_rewritten()
    {
        if (!OperatingSystem.IsWindows()) return;
        Present(); var testRoot = @"Software\Syn3UpdaterTests\" + Guid.NewGuid().ToString("N");
        try
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testRoot + @"\syn3updater\shell\open\command")) k.SetValue(null, Launcher + " %1");
            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(testRoot, true)!;
            var store = new WindowsProtocolStore(root, @"syn3updater\shell\open\command");
            Assert.Equal(Launcher + " %1", store.GetOpenCommand());
            Assert.True(LauncherMigration.Run(Install, new FakeShortcuts(), store).ProtocolFixed);
            Assert.Equal($"\"{App}\" \"%1\"", store.GetOpenCommand());
        }
        finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testRoot, false); }
    }

    [Fact]
    public void The_real_default_folders_exist_as_a_list_without_throwing()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.NotEmpty(WindowsShortcutStore.DefaultFolders());
    }
}
