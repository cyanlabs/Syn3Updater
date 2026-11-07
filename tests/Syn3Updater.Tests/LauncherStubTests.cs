using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;
using Xunit;

namespace Syn3Updater.Tests;

public class LauncherStubTests : IDisposable
{
    private readonly TempDir _t = new();
    private string Dir => _t.Sub("install");
    public void Dispose() => _t.Dispose();
    private void App(string content = "APP") { Directory.CreateDirectory(Dir); File.WriteAllText(Path.Combine(Dir, "Syn3Updater.exe"), content); }

    [Fact]
    public void It_starts_the_app_with_exactly_the_arguments_it_was_given()
    {
        App(); string? exe = null; string[]? args = null; var problems = new List<string>();
        var code = LauncherStub.Run(Dir, new[] { "syn3updater://?region=NA" }, (e, a) => { exe = e; args = a.ToArray(); return true; }, problems.Add);
        Assert.Equal(0, code); Assert.Equal(Path.Combine(Dir, "Syn3Updater.exe"), exe); Assert.Equal(new[] { "syn3updater://?region=NA" }, args); Assert.Empty(problems);
    }

    [Fact]
    public void With_no_arguments_it_just_starts_the_app()
    {
        App(); string[]? args = null;
        Assert.Equal(0, LauncherStub.Run(Dir, Array.Empty<string>(), (_, a) => { args = a.ToArray(); return true; }, _ => { }));
        Assert.Empty(args!);
    }

    [Fact]
    public void An_update_that_was_cut_short_is_undone_before_the_app_starts_so_a_missing_app_comes_back()
    {
        Directory.CreateDirectory(Dir);                                                      // the crash happened after the old exe was moved aside and before the new one was in place
        var backup = Path.Combine(Dir, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "ORIGINAL");
        string? startedContent = null;
        var code = LauncherStub.Run(Dir, Array.Empty<string>(), (e, _) => { startedContent = File.ReadAllText(e); return true; }, _ => { });
        Assert.Equal(0, code); Assert.Equal("ORIGINAL", startedContent);
        Assert.False(Directory.Exists(backup));
    }

    [Fact]
    public void The_old_program_left_by_a_finished_update_is_cleaned_up()
    {
        App("NEW"); var backup = Path.Combine(Dir, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "OLD"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
        LauncherStub.Run(Dir, Array.Empty<string>(), (_, _) => true, _ => { });
        Assert.False(Directory.Exists(backup)); Assert.Equal("NEW", File.ReadAllText(Path.Combine(Dir, "Syn3Updater.exe")));
    }

    [Fact]
    public void A_missing_app_is_reported_to_the_user_and_nothing_is_started()
    {
        Directory.CreateDirectory(Dir); var problems = new List<string>(); var started = false;
        var code = LauncherStub.Run(Dir, Array.Empty<string>(), (_, _) => started = true, problems.Add);
        Assert.Equal(1, code); Assert.False(started); Assert.Contains("reinstall", problems.Single());
    }

    [Fact]
    public void An_app_that_cannot_be_started_is_reported()
    {
        App(); var problems = new List<string>();
        Assert.Equal(1, LauncherStub.Run(Dir, Array.Empty<string>(), (_, _) => false, problems.Add));
        Assert.Contains("could not be started", problems.Single());
    }

    [Fact]
    public void A_cleanup_that_is_not_permitted_does_not_stop_the_app_starting()
    {
        App(); var started = false; var logs = new List<string>();
        // a path that cannot be cleaned (the folder holds a file where the backup folder would be): the stub still starts the app
        File.WriteAllText(Path.Combine(Dir, InstallTransaction.BackupDirName), "a file, not a folder");
        Assert.Equal(0, LauncherStub.Run(Dir, Array.Empty<string>(), (_, _) => started = true, _ => { }, logs.Add));
        Assert.True(started);
    }
}
