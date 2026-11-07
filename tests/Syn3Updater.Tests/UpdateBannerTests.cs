using System.Net;
using System.Text;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Tests;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

/// <summary>The update banner: what it offers for each kind of install, and what "Update now" does. Real archive, real install folder, fake server; only the process restart is stubbed.</summary>
public class UpdateBannerTests
{
    private sealed class Env : IDisposable
    {
        public AppEnv App = new();
        public UpdateInstallerTests Inner = new();
        public List<string> Started = new(); public int Quits; public bool StartWorks = true;
        public FuncHandler Net = null!;
        public Env()
        {
            UpdateRestart.Starter = exe => { Started.Add(exe); return StartWorks; };
            UpdateRestart.Quit = () => Quits++;
            Ctx.LauncherPrefsPath = null;
        }
        public (UpdatePlan Plan, string Xdg, string AppDir) Ready(byte[]? served = null, string script = UpdateInstallerTests.CopyScript)
        {
            var (plan, bytes, xdg, appDir) = Inner.Scenario(script: script);
            Net = UpdateInstallerTests.Serve(served ?? bytes);
            Ctx.Install = plan.Install;
            Ctx.UpdateInstaller = Inner.Installer(Net, xdg);
            App.Main.Update = new UpdateInfo("3.1.0", "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0", false);
            App.Main.Plan = plan;
            return (plan, xdg, appDir);
        }
        public void Dispose() { UpdateRestart.Starter = null!; UpdateRestart.Quit = null!; Workflow.Busy = false; Ctx.PlanUpdate = i => Ctx.UpdatePlanner.PlanAsync(i, Ctx.Install, Ctx.Version, Ctx.UpdateChannel, ReleaseManifest.CurrentOs(), ReleaseManifest.CurrentArch()); App.Dispose(); Inner.Dispose(); }
    }

    [AvaloniaFact]
    public void An_installable_plan_offers_Update_now_and_nothing_else_does()
    {
        using var e = new Env(); e.Ready();
        var m = e.App.Main;
        Assert.True(m.CanUpdateNow); Assert.False(m.CanShowHowTo); Assert.True(m.NotInstallingUpdate);
        m.Plan = m.Plan! with { Kind = PlanKind.Manual }; Assert.False(m.CanUpdateNow); Assert.True(m.CanShowHowTo);
        m.Plan = m.Plan with { Kind = PlanKind.NotifyOnly }; Assert.False(m.CanUpdateNow); Assert.False(m.CanShowHowTo);
        m.Plan = null; Assert.False(m.CanUpdateNow);
    }

    [AvaloniaFact]
    public async Task Update_now_installs_remembers_the_notes_and_restarts_into_the_new_program()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var (_, _, appDir) = e.Ready();
        e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
        Assert.Equal(new[] { Path.Combine(appDir, "Syn3Updater") }, e.Started);
        Assert.Equal(1, e.Quits);
        Assert.Equal("3.1.0", Ctx.Main.WhatsNewVersion); Assert.Equal("Notes here", Ctx.Main.WhatsNewNotes);
        var asked = e.App.Dialogs.Shown.Single();
        Assert.Equal("Update to Syn3 Updater 3.1.0", asked.Title);
        Assert.Contains("Notes here", e.App.Dialogs.Messages.Single());
        Assert.True(Workflow.Busy);                       // still held while the app closes, so nothing starts in between
    }

    [AvaloniaFact]
    public async Task Saying_no_downloads_nothing()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var (_, _, appDir) = e.Ready();
        e.App.Dialogs.DefaultYes = false;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Empty(e.Net.Requests); Assert.Empty(e.Started); Assert.False(e.App.Main.InstallingUpdate); Assert.False(Workflow.Busy);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
    }

    [AvaloniaFact]
    public async Task A_running_job_blocks_the_update_and_nothing_is_asked_or_downloaded()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Ready(); Workflow.Busy = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("download or update is running", e.App.Dialogs.Messages.Single());
        Assert.Empty(e.Net.Requests); Assert.Empty(e.Started);
        Assert.True(Workflow.Busy);                        // still the other job's flag, untouched
    }

    [AvaloniaFact]
    public async Task A_job_started_while_the_question_was_open_also_blocks_it()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Ready();
        e.App.Dialogs.Say(msg => { Workflow.Busy = true; return true; }, yes: true);          // the user starts something while the dialog is up, then says yes
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Empty(e.Net.Requests); Assert.Empty(e.Started);
    }

    [AvaloniaFact]
    public async Task A_corrupt_download_shows_the_error_and_leaves_everything_as_it_was()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var (_, _, appDir) = e.Ready(served: new byte[] { 1, 2, 3 });
        e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("could not be installed", e.App.Dialogs.Messages.Last());
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
        Assert.Empty(e.Started); Assert.Equal(0, e.Quits);
        Assert.False(e.App.Main.InstallingUpdate); Assert.False(Workflow.Busy);               // free again, so the user can carry on or retry
        Assert.True(e.App.Main.CanUpdateNow);
    }

    [AvaloniaFact]
    public async Task A_failing_install_script_shows_the_error_and_rolls_back()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var (_, _, appDir) = e.Ready(script: "#!/usr/bin/env bash\necho nope >&2\nexit 4\n");
        e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("nope", e.App.Dialogs.Messages.Last());
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
        Assert.False(Workflow.Busy);
    }

    [AvaloniaFact]
    public async Task If_the_new_program_cannot_be_started_the_user_is_told_and_the_app_stays_open()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Ready(); e.StartWorks = false;
        e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("could not be restarted", e.App.Dialogs.Messages.Last());
        Assert.Equal(0, e.Quits); Assert.False(Workflow.Busy); Assert.False(e.App.Main.InstallingUpdate);
    }

    [AvaloniaFact]
    public async Task Pressing_Update_now_twice_does_not_start_two_installs()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Ready(); e.App.Dialogs.DefaultYes = true;
        var m = e.App.Main;
        var first = m.UpdateNowCommand.ExecuteAsync(null); var second = m.UpdateNowCommand.ExecuteAsync(null);
        await Task.WhenAll(first, second);
        Assert.Single(e.Started);
    }

    [AvaloniaFact]
    public async Task Update_now_does_nothing_unless_the_plan_is_an_install()
    {
        using var e = new Env(); e.Ready();
        e.App.Main.Plan = e.App.Main.Plan! with { Kind = PlanKind.Manual };
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Empty(e.App.Dialogs.Messages); Assert.Empty(e.Net.Requests);
    }

    [AvaloniaFact]
    public async Task How_to_update_shows_the_command_and_can_copy_it_or_open_the_release()
    {
        using var e = new Env(); e.Ready();
        var m = e.App.Main;
        m.Plan = m.Plan! with { Kind = PlanKind.Manual, Instructions = "sudo apt install ./x.deb", Reason = "installed from a .deb package" };
        e.App.Dialogs.Choices.Enqueue(1); await m.ShowHowToCommand.ExecuteAsync(null);
        Assert.Contains("sudo apt install ./x.deb", e.App.Dialogs.Messages.Single());
        Assert.Equal(new[] { "sudo apt install ./x.deb" }, e.App.Dialogs.Clipboard);
        e.App.Dialogs.Choices.Enqueue(2); await m.ShowHowToCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0" }, e.App.Dialogs.Opened);
        Assert.Equal(new[] { "OK", "Copy command", "View release" }, e.App.Dialogs.Shown[0].Buttons);
    }

    [AvaloniaFact]
    public void The_banner_text_shows_progress_while_installing_and_the_notice_otherwise()
    {
        using var e = new Env(); e.Ready(); var m = e.App.Main;
        Assert.Equal(m.UpdateText, m.UpdateBannerText);
        m.UpdateStatus = "Downloading..."; m.InstallingUpdate = true;
        Assert.Equal("Downloading...", m.UpdateBannerText); Assert.False(m.CanUpdateNow); Assert.False(m.NotInstallingUpdate);
        m.InstallingUpdate = false; Assert.Equal(m.UpdateText, m.UpdateBannerText);
    }

    [AvaloniaFact]
    public void The_notice_cannot_be_dismissed_while_installing()
    {
        using var e = new Env(); e.Ready(); var m = e.App.Main; m.InstallingUpdate = true;
        m.DismissUpdateCommand.Execute(null);
        Assert.True(m.UpdateAvailable);
    }

    // ---- finding a release and planning it ----

    private static string Releases(string tag, string title, bool pre, string baseUrl) => System.Text.Json.JsonSerializer.Serialize(new[]
    {
        new
        {
            tag_name = tag, name = title, prerelease = pre, draft = false, html_url = "https://github.com/CyanLabs/Syn3Updater/releases/tag/" + tag,
            assets = new[] { new { name = "manifest.json", browser_download_url = baseUrl + "manifest.json" }, new { name = "manifest.json.minisig", browser_download_url = baseUrl + "manifest.json.minisig" } }
        }
    });

    private static void Network(Env e, Install install)
    {
        const string b = "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0-beta.2/";
        var manifest = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json")));
        var sig = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json.minisig")));
        var h = new FuncHandler(r => r.RequestUri!.Host == "api.github.com" ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Releases("v3.1.0-beta.2", "Syn3 Updater 3.1.0-beta.2", true, b), Encoding.UTF8, "application/json") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri.AbsolutePath.EndsWith(".minisig") ? sig : manifest) });
        var http = new HttpClient(h);
        Ctx.Updates = new UpdateChecker(http); var planner = Ctx.UpdatePlanner = new UpdateService(http);
        Ctx.PlanUpdate = i => planner.PlanAsync(i, Ctx.Install, Ctx.Version, Ctx.UpdateChannel, UpdateOs.Linux, UpdateArch.X64);          // the fixture lists Linux files, whatever OS the tests run on
        Ctx.Install = new InstallInfo(install.Kind, "/x", install.Route, "test"); Ctx.Main.UpdateChannel = "Beta";
    }
    private sealed record Install(InstallKind Kind, UpdateRoute Route);

    [AvaloniaFact]
    public async Task A_found_release_is_planned_from_its_signed_manifest()
    {
        using var e = new Env(); Network(e, new(InstallKind.LinuxTar, UpdateRoute.InPlace));
        await e.App.Main.CheckForUpdateAsync();
        Assert.Equal("3.1.0-beta.2", e.App.Main.Update!.Version);
        Assert.Equal(PlanKind.Install, e.App.Main.Plan!.Kind);
        Assert.True(e.App.Main.CanUpdateNow);
    }

    [AvaloniaFact]
    public async Task A_package_install_is_shown_how_to_update_and_not_offered_a_button()
    {
        using var e = new Env(); Network(e, new(InstallKind.LinuxDeb, UpdateRoute.Manual));
        await e.App.Main.CheckForUpdateAsync();
        Assert.True(e.App.Main.CanShowHowTo); Assert.False(e.App.Main.CanUpdateNow);
        Assert.Contains("sudo apt install", e.App.Main.Plan!.Instructions);
    }

    [AvaloniaFact]
    public async Task A_development_run_only_gets_the_notice()
    {
        using var e = new Env(); Network(e, new(InstallKind.Development, UpdateRoute.None));
        await e.App.Main.CheckForUpdateAsync();
        Assert.True(e.App.Main.UpdateAvailable); Assert.False(e.App.Main.CanUpdateNow); Assert.False(e.App.Main.CanShowHowTo);
    }

    [AvaloniaFact]
    public async Task A_dismissed_notice_stays_away_on_the_periodic_check_but_a_manual_check_brings_it_back()
    {
        using var e = new Env(); Network(e, new(InstallKind.LinuxTar, UpdateRoute.InPlace)); var m = e.App.Main;
        await m.CheckForUpdateAsync(); m.DismissUpdateCommand.Execute(null); Assert.False(m.UpdateAvailable);
        await m.CheckForUpdateAsync(periodic: true); Assert.False(m.UpdateAvailable);
        await m.CheckForUpdateAsync(); Assert.True(m.UpdateAvailable);
    }

    [AvaloniaFact]
    public async Task A_check_is_ignored_while_an_install_is_running()
    {
        using var e = new Env(); Network(e, new(InstallKind.LinuxTar, UpdateRoute.InPlace)); var m = e.App.Main;
        m.InstallingUpdate = true; await m.CheckForUpdateAsync();
        Assert.False(m.UpdateAvailable);
    }

    // ---- after an update ----

    [AvaloniaFact]
    public async Task The_new_versions_notes_are_shown_once_and_then_forgotten()
    {
        using var e = new Env(); Ctx.Install = new InstallInfo(InstallKind.Development, "/x", UpdateRoute.None, "t");
        Ctx.Main.WhatsNewVersion = Ctx.Version; Ctx.Main.WhatsNewNotes = "Faster updates.";
        await e.App.Main.OnStartedAsync();
        Assert.Equal("Faster updates.", e.App.Dialogs.Messages.Single());
        Assert.Equal($"Syn3 Updater was updated to {Ctx.Version}", e.App.Dialogs.Shown.Single().Title);
        Assert.Null(Ctx.Main.WhatsNewVersion); Assert.Null(Ctx.Main.WhatsNewNotes);
        await e.App.Main.OnStartedAsync();
        Assert.Single(e.App.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task Notes_for_a_version_that_is_not_running_are_dropped_silently()
    {
        using var e = new Env(); Ctx.Install = new InstallInfo(InstallKind.Development, "/x", UpdateRoute.None, "t");
        Ctx.Main.WhatsNewVersion = "9.9.9"; Ctx.Main.WhatsNewNotes = "x";
        await e.App.Main.OnStartedAsync();
        Assert.Empty(e.App.Dialogs.Messages); Assert.Null(Ctx.Main.WhatsNewVersion);
    }

    [AvaloniaFact]
    public async Task An_update_with_no_notes_shows_no_empty_dialog()
    {
        using var e = new Env(); Ctx.Install = new InstallInfo(InstallKind.Development, "/x", UpdateRoute.None, "t");
        Ctx.Main.WhatsNewVersion = Ctx.Version; Ctx.Main.WhatsNewNotes = "  ";
        await e.App.Main.OnStartedAsync();
        Assert.Empty(e.App.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task The_copy_kept_for_rollback_is_removed_once_the_new_version_has_started()
    {
        using var e = new Env(); var (_, _, appDir) = e.Ready();
        var prev = Path.Combine(appDir, "Syn3Updater" + LinuxTarApplier.PreviousSuffix); File.WriteAllText(prev, "old");
        await e.App.Main.OnStartedAsync();
        Assert.False(File.Exists(prev));
    }
}
