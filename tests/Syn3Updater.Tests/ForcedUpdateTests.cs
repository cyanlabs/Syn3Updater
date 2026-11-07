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
        using var e = new Env(); var plan = e.Install();
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater")));
        Assert.Single(e.Started); Assert.Equal(1, e.Quits);
        Assert.Empty(e.App.Dialogs.Shown);                       // nothing to confirm: it is required
        Assert.True(e.App.Main.ForcedUpdateActive);              // the overlay stays up while the app closes and the new one opens
        Assert.Equal("3.1.0", Ctx.Main.WhatsNewVersion);
    }

    [AvaloniaFact]
    public async Task The_overlay_text_says_what_is_being_installed_and_carries_the_maintainers_message()
    {
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
    public async Task A_failed_install_offers_to_try_again_and_the_second_try_succeeds()
    {
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
        using var e = new Env(); var plan = e.Install(); e.Served = new byte[] { 9, 9, 9 };
        e.App.Dialogs.Choices.Enqueue(1);          // Quit
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal(1, e.Quits); Assert.Empty(e.Started); Assert.False(e.App.Main.ForcedUpdateActive); Assert.False(Workflow.Busy);
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater")));
    }

    [AvaloniaFact]
    public async Task When_the_release_cannot_be_checked_the_user_is_told_and_can_try_again_or_quit()
    {
        using var e = new Env(); var (plan, _, _, _) = e.Inner.Scenario();
        e.Plan(UpdatePlan.Notify(plan.Install, "the signed manifest could not be downloaded"));
        e.App.Dialogs.Choices.Enqueue(0);          // Try again (still offline)
        e.App.Dialogs.Choices.Enqueue(2);          // Quit
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal(2, e.App.Dialogs.Shown.Count); Assert.Contains("could not be downloaded right now", e.App.Dialogs.Messages[0]);
        Assert.Equal(1, e.Quits);
    }

    [AvaloniaFact]
    public async Task A_release_whose_signature_was_rejected_is_never_installed_even_when_required()
    {
        using var e = new Env(); var (plan, _, _, _) = e.Inner.Scenario();
        e.Plan(UpdatePlan.Reject(plan.Install, "manifest signature rejected"));
        e.App.Dialogs.Choices.Enqueue(2);
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.Equal(1, e.Quits); Assert.Equal(0, e.Downloads);
    }

    [AvaloniaFact]
    public async Task With_the_backend_down_the_cached_policy_still_forces()
    {
        using var e = new Env(); e.Install();
        new UpdatePolicy(DateTimeOffset.UtcNow.AddDays(-2), new[] { new PolicyRow(UpdateChannel.Stable, true, "3.0.5", null, null, null, null) }).Save(Ctx.PolicyCacheFile);
        e.Serve(raw: () => throw new HttpRequestException("offline"));
        await e.App.Main.EnforceMinimumAsync();
        Assert.Single(e.Started);
    }

    [AvaloniaFact]
    public async Task With_the_backend_down_and_nothing_cached_nothing_is_forced()
    {
        using var e = new Env(); e.Install(); File.Delete(Ctx.PolicyCacheFile);
        e.Serve(raw: () => throw new HttpRequestException("offline"));
        await e.App.Main.EnforceMinimumAsync();
        Assert.Empty(e.Started); Assert.Empty(e.App.Dialogs.Shown); Assert.False(e.App.Main.ForcedUpdateActive);
    }

    [AvaloniaFact]
    public async Task A_live_answer_is_cached_for_the_next_time()
    {
        using var e = new Env(); e.Install(); File.Delete(Ctx.PolicyCacheFile);
        await e.App.Main.EnforceMinimumAsync();
        Assert.Equal("3.0.5", UpdatePolicy.Load(Ctx.PolicyCacheFile)!.MinimumFor(UpdateChannel.Stable, UpdateOs.Linux)!.Version);
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
