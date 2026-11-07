using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Tests;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

/// <summary>Required updates at launch. Real archive and install folder, fake network; only the restart is stubbed.</summary>
public class ForcedUpdateTests
{
    private sealed class Env : IDisposable
    {
        public AppEnv App = new(); public UpdateInstallerTests Inner = new();
        public List<string> Started = new(); public int Quits;
        public FuncHandler Policy = null!; public FuncHandler Releases = null!;
        public byte[] Served = Array.Empty<byte>(); public int Downloads;
        private readonly string _version = Ctx.Version;
        public Env()
        {
            Ctx.Version = "3.0.0";                                // the test host's own version is something else entirely
            UpdateRestart.Starter = e => { Started.Add(e); return true; }; UpdateRestart.Quit = () => Quits++;
            Ctx.LauncherPrefsPath = null; Ctx.Main.UpdateChannel = "Stable";
            Ctx.Install = new InstallInfo(InstallKind.LinuxTar, "/x", UpdateRoute.InPlace, "t");
            Serve(); Network("3.1.0");
        }

        /// <summary>The backend's answer for the policy: rows as (channel, enabled, minAll, message).</summary>
        public void Serve(string min = "3.0.5", bool enabled = true, string? message = null, string channel = "Stable", Func<HttpResponseMessage>? raw = null)
        {
            var json = JsonSerializer.Serialize(new { data = new { update_policy = new[] { new { channel, enabled, min_version_all = (string?)min, min_version_windows = (string?)null, min_version_linux = (string?)null, min_version_macos = (string?)null, message } } } });
            Policy = new FuncHandler(_ => raw?.Invoke() ?? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            Ctx.Api = new ApiClient(new HttpClient(Policy), () => "t", () => "");
        }

        /// <summary>The newest release on GitHub.</summary>
        public void Network(string newest)
        {
            var json = JsonSerializer.Serialize(new[] { new { tag_name = "v" + newest, name = "Syn3 Updater " + newest, prerelease = false, draft = false, html_url = "https://github.com/CyanLabs/Syn3Updater/releases/tag/v" + newest } });
            Releases = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
            Ctx.Updates = new UpdateChecker(new HttpClient(Releases));
        }

        /// <summary>The plan the app makes for what it found: installable by default.</summary>
        public UpdatePlan Install()
        {
            var (plan, bytes, xdg, _) = Inner.Scenario(); Served = bytes;
            var http = new HttpClient(new FuncHandler(_ => { Downloads++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Served) }; }));
            Ctx.UpdateInstaller = new UpdateInstaller(new UpdateDownloader(http, _ => long.MaxValue), Path.Combine(Inner.Root, "updates"), xdg);
            Ctx.Install = plan.Install; Ctx.PlanUpdate = _ => Task.FromResult(plan);
            return plan;
        }

        public void Plan(UpdatePlan plan) => Ctx.PlanUpdate = _ => Task.FromResult(plan);
        public void Dispose() { Ctx.Version = _version; UpdateRestart.Starter = null!; UpdateRestart.Quit = null!; Workflow.Busy = false; Ctx.PlanUpdate = i => Ctx.UpdatePlanner.PlanAsync(i, Ctx.Install, Ctx.Version, Ctx.UpdateChannel, ReleaseManifest.CurrentOs(), ReleaseManifest.CurrentArch()); App.Dispose(); Inner.Dispose(); }
    }

    private static InstallInfo Pkg(InstallKind k) => new(k, "/opt/syn3updater", UpdateRoute.Manual, "installed from a package");

    [AvaloniaFact]
    public async Task A_version_below_the_minimum_is_updated_at_launch_with_no_question_and_the_window_is_blocked()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var plan = e.Install();
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater")));
        Assert.Single(e.Started); Assert.Equal(1, e.Quits);
        Assert.Empty(e.App.Dialogs.Shown);                       // nothing to confirm: it is required
        Assert.True(e.App.Main.ForcedUpdateActive);              // the overlay stays up while the app closes and the new one opens
        Assert.Equal("3.1.0", Ctx.Main.WhatsNewVersion);
    }

    [AvaloniaFact]
    public async Task The_startup_check_still_running_does_not_make_the_required_update_report_that_it_could_not_be_downloaded()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var installed = e.Install();
        var real = Ctx.PlanUpdate; var calls = 0;
        Ctx.PlanUpdate = async i => { var first = Interlocked.Increment(ref calls) == 1; if (first) await Task.Delay(400); return await real(i); };      // the startup check is slow to plan
        var startup = e.App.Main.CheckForUpdateAsync();           // App.axaml.cs starts this at launch and does not wait for it
        await Task.Delay(50);
        await e.App.Main.EnforceMinimumAsync().WaitAsync(TimeSpan.FromSeconds(20));       // ...and the window opening then enforces the minimum (a limit, so a regression fails instead of hanging)
        await startup.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Empty(e.App.Dialogs.Shown);                        // no "could not be downloaded" question: the plan was there
        Assert.Single(e.Started); Assert.Equal("NEW", File.ReadAllText(Path.Combine(installed.Install.AppDir, "Syn3Updater")));
    }

    [AvaloniaFact]
    public async Task The_overlay_text_says_what_is_being_installed_and_carries_the_maintainers_message()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Serve(message: "Fixes a firmware bug."); e.Install();
        await e.App.Main.EnforceMinimumAsync();
        Assert.Contains("3.1.0", e.App.Main.ForcedUpdateText); Assert.Contains("Fixes a firmware bug.", e.App.Main.ForcedUpdateText);
    }

    [AvaloniaTheory]
    [InlineData("3.0.0", true, false)]            // already at the minimum
    [InlineData("2.0.0", false, false)]           // the row is switched off
    public async Task When_nothing_is_required_nothing_happens_and_GitHub_is_not_even_asked(string min, bool enabled, bool expected)
    {
        using var e = new Env(); e.Install(); e.Serve(min, enabled);
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Releases.Requests); Assert.Empty(e.Started); Assert.Equal(expected, e.App.Main.ForcedUpdateActive); Assert.Empty(e.App.Dialogs.Shown);
    }

    [AvaloniaFact]
    public async Task A_minimum_above_the_newest_release_is_ignored_so_a_typo_cannot_lock_everyone_out()
    {
        using var e = new Env(); e.Install(); e.Serve("99.0.0"); e.Network("3.1.0");
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.False(e.App.Main.ForcedUpdateActive); Assert.Empty(e.App.Dialogs.Shown); Assert.Equal(0, e.Quits);
    }

    [AvaloniaFact]
    public async Task A_policy_for_another_channel_does_not_apply()
    {
        using var e = new Env(); e.Install(); e.Serve(channel: "Beta");
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.Empty(e.Releases.Requests);
    }

    [AvaloniaFact]
    public async Task A_development_run_is_never_forced()
    {
        using var e = new Env(); e.Install(); Ctx.Install = new InstallInfo(InstallKind.Development, "/x", UpdateRoute.None, "dotnet host");
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Policy.Requests); Assert.Empty(e.Started); Assert.Empty(e.App.Dialogs.Shown);
    }

    [AvaloniaFact]
    public async Task Never_mid_run_a_job_in_progress_postpones_it_to_the_next_start()
    {
        using var e = new Env(); e.Install(); Workflow.Busy = true;
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.False(e.App.Main.ForcedUpdateActive); Assert.Empty(e.App.Dialogs.Shown); Assert.True(Workflow.Busy);
    }

    [AvaloniaFact]
    public async Task A_package_managed_copy_is_blocked_with_the_exact_command_until_the_user_quits()
    {
        using var e = new Env(); e.Serve(message: "Security fix");
        var (plan, _, _, _) = e.Inner.Scenario(); Ctx.Install = Pkg(InstallKind.LinuxDeb);
        e.Plan(plan with { Kind = PlanKind.Manual, Install = Pkg(InstallKind.LinuxDeb), Instructions = "sudo apt install ./Syn3Updater-3.1.0-linux-x64.deb" });
        e.App.Dialogs.Choices.Enqueue(0);          // Copy command: still blocked, asked again
        e.App.Dialogs.Choices.Enqueue(1);          // View release: still blocked
        e.App.Dialogs.Choices.Enqueue(2);          // Quit
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal(3, e.App.Dialogs.Shown.Count);
        Assert.Contains("sudo apt install ./Syn3Updater-3.1.0-linux-x64.deb", e.App.Dialogs.Messages[0]); Assert.Contains("Security fix", e.App.Dialogs.Messages[0]);
        Assert.Equal("Update required", e.App.Dialogs.Shown[0].Title);
        Assert.Equal(new[] { "sudo apt install ./Syn3Updater-3.1.0-linux-x64.deb" }, e.App.Dialogs.Clipboard);
        Assert.Equal(new[] { "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0" }, e.App.Dialogs.Opened);
        Assert.Equal(1, e.Quits); Assert.Empty(e.Started); Assert.False(e.App.Main.ForcedUpdateActive);
        Assert.Equal(new[] { "Copy command", "View release", "Quit" }, e.App.Dialogs.Shown[0].Buttons);
    }

    [AvaloniaFact]
    public async Task The_Arch_dialog_is_laid_out_as_a_request_then_the_reason_then_the_commands_then_the_maintainers_message()
    {
        using var e = new Env(); e.Serve(message: "Test of required updates");
        var (plan, _, _, _) = e.Inner.Scenario(); Ctx.Install = Pkg(InstallKind.LinuxPacman);
        const string file = "Syn3Updater-3.1.0-linux-x64.pkg.tar.zst", url = "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0/" + file;
        e.Plan(plan with { Kind = PlanKind.Manual, Install = Pkg(InstallKind.LinuxPacman), Instructions = InstallDetector.ManualInstructions(InstallKind.LinuxPacman, file, url) });
        e.App.Dialogs.Choices.Enqueue(0); e.App.Dialogs.Choices.Enqueue(2);          // Copy command, then Quit
        await e.App.Main.EnforceMinimumAsync();
        var lines = e.App.Dialogs.Messages[0].Split('\n');
        Assert.Equal("Please update Syn3 Updater to continue.", lines[0]);
        Assert.StartsWith("Version 3.0.5 or newer is required.", lines[2]); Assert.Contains("can't update itself", lines[2]);
        Assert.Equal("curl -LO " + url, lines[4]); Assert.Equal("sudo pacman -U " + file, lines[5]);
        Assert.Equal("Then start Syn3 Updater again.", lines[7]); Assert.Equal("Test of required updates", lines[9]);
        Assert.Equal(new[] { $"curl -LO {url} && sudo pacman -U {file}" }, e.App.Dialogs.Clipboard);          // one paste downloads and installs
    }

    [AvaloniaFact]
    public async Task A_copy_no_package_manager_owns_is_pointed_at_the_download_page_and_the_text_does_not_mention_a_package_manager()
    {
        using var e = new Env();
        var (plan, _, _, _) = e.Inner.Scenario(); var portable = new InstallInfo(InstallKind.LinuxPortable, "/home/u/Downloads/x", UpdateRoute.Manual, "nothing installed this");
        Ctx.Install = portable;
        e.Plan(plan with { Kind = PlanKind.Manual, Install = portable, Instructions = InstallDetector.ManualInstructions(InstallKind.LinuxPortable, null) });
        e.App.Dialogs.Choices.Enqueue(0); e.App.Dialogs.Choices.Enqueue(2);          // Copy command, then Quit
        await e.App.Main.EnforceMinimumAsync();
        var lines = e.App.Dialogs.Messages[0].Split('\n');
        Assert.Equal("Please update Syn3 Updater to continue.", lines[0]);
        Assert.Equal("Version 3.0.5 or newer is required. This copy can't update itself because of how it was installed. Download the new version here:", lines[2]);
        Assert.Equal("https://github.com/CyanLabs/Syn3Updater/releases", lines[4]);
        Assert.Equal("Install it the same way as before. Then start Syn3 Updater again.", lines[6]);
        Assert.DoesNotContain("package manager", e.App.Dialogs.Messages[0]);
        Assert.Equal(new[] { "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0" }, e.App.Dialogs.Clipboard);          // no command to copy: the page for this release
    }

    [AvaloniaFact]
    public async Task The_Mac_dialog_gives_the_Terminal_commands_and_the_steps_to_do_it_by_hand()
    {
        using var e = new Env();
        var (plan, _, _, _) = e.Inner.Scenario(); var mac = new InstallInfo(InstallKind.MacApp, "/Applications/Syn3 Updater.app/Contents/MacOS", UpdateRoute.Manual, "in-place updates need a signed build");
        Ctx.Install = mac;
        const string file = "Syn3Updater-3.1.0-macos-arm64.dmg", url = "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0/" + file;
        e.Plan(plan with { Kind = PlanKind.Manual, Install = mac, Instructions = InstallDetector.ManualInstructions(InstallKind.MacApp, file, url) });
        e.App.Dialogs.Choices.Enqueue(0); e.App.Dialogs.Choices.Enqueue(2);          // Copy command, then Quit
        await e.App.Main.EnforceMinimumAsync();
        var lines = e.App.Dialogs.Messages[0].Split('\n');
        Assert.Equal("Please update Syn3 Updater to continue.", lines[0]);
        Assert.Equal("Version 3.0.5 or newer is required. The Mac app can't update itself yet. Quit Syn3 Updater, then update it using Terminal:", lines[2]);
        Assert.Equal("curl -LO " + url, lines[4]); Assert.StartsWith("hdiutil attach", lines[5]); Assert.StartsWith("ditto ", lines[6]); Assert.StartsWith("hdiutil detach", lines[7]);
        Assert.Equal("Or do it by hand: use View release to download the disk image, open it, and drag Syn3 Updater into your Applications folder, replacing the old copy.", lines[9]);
        Assert.Equal("Then start Syn3 Updater again.", lines[11]);
        Assert.StartsWith($"curl -LO {url} && hdiutil attach", e.App.Dialogs.Clipboard.Single()); Assert.EndsWith("hdiutil detach -quiet \"/Volumes/Syn3 Updater\"", e.App.Dialogs.Clipboard.Single());
    }

    [AvaloniaFact]
    public async Task A_failed_install_offers_to_try_again_and_the_second_try_succeeds()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var plan = e.Install(); var good = e.Served; var attempts = 0;
        Ctx.UpdateInstaller = new UpdateInstaller(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new ByteArrayContent(++attempts == 1 ? new byte[] { 1, 2, 3 } : good) })), _ => long.MaxValue), Path.Combine(e.Inner.Root, "updates"), Path.Combine(e.Inner.Root, "xdg"));
        e.App.Dialogs.Choices.Enqueue(0);          // Try again
        await e.App.Main.EnforceMinimumAsync();
        Assert.Contains("could not be installed", e.App.Dialogs.Messages.Single());
        Assert.Equal(new[] { "Try again", "Quit" }, e.App.Dialogs.Shown.Single().Buttons);
        Assert.Equal(2, attempts); Assert.Single(e.Started); Assert.Equal("NEW", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater")));
    }

    [AvaloniaFact]
    public async Task A_failed_install_can_be_answered_with_quit_and_the_old_version_is_untouched()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); var plan = e.Install(); e.Served = new byte[] { 9, 9, 9 };
        e.App.Dialogs.Choices.Enqueue(1);          // Quit
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal(1, e.Quits); Assert.Empty(e.Started); Assert.False(e.App.Main.ForcedUpdateActive); Assert.False(Workflow.Busy);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater")));
    }

    [AvaloniaFact]
    public async Task When_the_network_fails_while_checking_the_release_the_user_is_told_and_can_try_again_or_quit()
    {
        using var e = new Env(); var (plan, _, _, _) = e.Inner.Scenario();
        e.Plan(UpdatePlan.Notify(plan.Install, "the signed manifest could not be downloaded", null, transient: true));      // temporary: trying again may work
        e.App.Dialogs.Choices.Enqueue(0);          // Try again (still offline)
        e.App.Dialogs.Choices.Enqueue(2);          // Quit
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal(2, e.App.Dialogs.Shown.Count); Assert.Contains("could not be downloaded right now", e.App.Dialogs.Messages[0]);
        Assert.Equal(1, e.Quits);
    }

    [AvaloniaFact]
    public async Task A_minimum_that_can_never_be_met_from_here_does_not_lock_anyone_out()
    {
        // the newest release meets the minimum by version number, but it has no signed manifest, no file for this system, or it failed verification: nothing the user could do would satisfy it
        foreach (var plan in new Func<InstallInfo, UpdatePlan>[]
                 { i => UpdatePlan.Notify(i, "this release has no signed manifest"), i => UpdatePlan.Notify(i, "this release has no file for this system"), i => UpdatePlan.Reject(i, "manifest signature rejected") })
        {
            using var e = new Env(); var (p, _, _, _) = e.Inner.Scenario();
            e.Plan(plan(p.Install));
            await e.App.Main.EnforceMinimumAsync();
            Assert.Empty(e.App.Dialogs.Shown); Assert.Empty(e.Started); Assert.Equal(0, e.Quits); Assert.False(e.App.Main.ForcedUpdateActive); Assert.Equal(0, e.Downloads);
            Assert.Contains(Ctx.Log.Lines, l => l.Contains("not forced"));
        }
    }

    [AvaloniaFact]
    public async Task With_the_backend_down_nothing_is_forced_the_app_cannot_do_anything_without_it_anyway()
    {
        using var e = new Env(); e.Install();
        e.Serve(raw: () => throw new HttpRequestException("offline"));
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.Empty(e.App.Dialogs.Shown); Assert.False(e.App.Main.ForcedUpdateActive); Assert.Empty(e.Releases.Requests);
        Assert.Contains(Ctx.Log.Lines, l => l.Contains("nothing is forced"));
    }

    [AvaloniaFact]
    public async Task The_next_start_asks_again_so_a_fixed_backend_enforces_normally()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        using var e = new Env(); e.Install();
        e.Serve(raw: () => throw new HttpRequestException("offline"));
        await e.App.Main.EnforceMinimumAsync(); Assert.Empty(e.Started);
        e.Serve();                                                  // the backend is back
        await e.App.Main.EnforceMinimumAsync(); Assert.Single(e.Started);
    }

    [AvaloniaFact]
    public async Task An_unexpected_error_in_the_check_never_stops_the_app_starting()
    {
        using var e = new Env(); e.Install();
        e.Serve(raw: () => throw new InvalidOperationException("something unforeseen"));
        await e.App.Main.EnforceMinimumAsync();                     // does not throw
        Assert.Empty(e.Started); Assert.Empty(e.App.Dialogs.Shown); Assert.False(e.App.Main.ForcedUpdateActive);
    }

    [AvaloniaFact]
    public async Task A_garbled_policy_answer_forces_nothing()
    {
        using var e = new Env(); e.Install();
        e.Serve(raw: () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>not json</html>") });
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.Empty(e.App.Dialogs.Shown);
    }
}
