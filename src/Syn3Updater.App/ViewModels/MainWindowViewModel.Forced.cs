using CommunityToolkit.Mvvm.ComponentModel;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

/// <summary>
/// Required updates. The backend's update policy names the oldest version still allowed on each channel and OS; a copy below it is updated at launch, before anything can be started.
/// Never mid-run, never for a development run, and only when a release that meets the minimum really exists.
/// </summary>
public partial class MainWindowViewModel
{
    /// <summary>The overlay that blocks the window while a required update is installed.</summary>
    [ObservableProperty] private bool _forcedUpdateActive;
    [ObservableProperty] private string _forcedUpdateText = "";

    private const string ReleasesPage = "https://github.com/" + UpdateChecker.Repo + "/releases";

    /// <summary>Checks the policy and, if this copy is below the minimum, updates it (or blocks with instructions when the app cannot). Returns once nothing is required, or when the app is closing.</summary>
    public async Task EnforceMinimumAsync()
    {
        try
        {
            if (Ctx.Install.Kind == InstallKind.Development) return;                         // developers are never locked out of their own build
            var os = ReleaseManifest.CurrentOs(); var channel = Ctx.UpdateChannel;
            var policy = await PolicySource.GetAsync(async ct => UpdatePolicy.FromApi(await Ctx.Api.GetUpdatePolicy(ct), DateTimeOffset.UtcNow),
                Ctx.PolicyCacheFile, DateTimeOffset.UtcNow, TimeSpan.FromSeconds(8), Ctx.Log.Info);
            var req = policy?.MinimumFor(channel, os);
            if (req == null) return;
            if (SemVer.Parse(Ctx.Version) is { } running && SemVer.Parse(req.Version)!.CompareTo(running) <= 0) return;       // meets the minimum: no need to look for releases

            await CheckForUpdateAsync();
            var decision = ForcedUpdate.Evaluate(policy, Ctx.Version, channel, os, Update?.Version);
            Ctx.Log.Info("Update policy: " + decision.Reason);
            if (!decision.Required) return;
            if (Workflow.Busy) { Ctx.Log.Info("A job is running: the required update waits for the next start"); return; }
            await RunRequiredUpdateAsync(decision.Requirement!);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Ctx.Log.Error("Could not check the update policy: " + e.Message);               // a problem here must never stop the app starting
        }
    }

    private async Task RunRequiredUpdateAsync(PolicyRequirement req)
    {
        var extra = string.IsNullOrWhiteSpace(req.Message) ? "" : "\n\n" + req.Message;
        var title = Ctx.T("App.ForcedTitle");
        while (true)
        {
            var plan = Plan; var page = Update?.Url ?? ReleasesPage;
            if (plan is { Kind: PlanKind.Install, Manifest: not null })
            {
                ForcedUpdateText = Ctx.T("App.ForcedBody", plan.Manifest.Version) + extra;
                ForcedUpdateActive = true;
                var (end, error) = await InstallCoreAsync(plan);
                if (end == InstallEnd.Restarting) return;                                      // the overlay stays up while the app closes and the new one opens
                ForcedUpdateActive = false;
                var text = end == InstallEnd.RestartFailed ? Ctx.T("App.UpdateRestartFailed", plan.Manifest.Version) : Ctx.T("App.ForcedFailed", error ?? "");
                if (await Dialogs.Show(text + extra, title, new[] { Ctx.T("App.ForcedRetry"), Ctx.T("App.ForcedQuit") }, 0) == 1) { UpdateRestart.Quit(); return; }
            }
            else if (plan is { Kind: PlanKind.Manual })
            {
                // updated another way (a package manager, a download): say how, and do not let the app be used until it has been
                var body = Ctx.T("App.ForcedManualBody", req.Version, plan.Instructions ?? "") + extra;
                var choice = await Dialogs.Show(body, title, new[] { Ctx.T("App.UpdateCopyCommand"), Ctx.T("App.UpdateView"), Ctx.T("App.ForcedQuit") }, 0);
                if (choice == 2) { UpdateRestart.Quit(); return; }
                if (choice == 0) await Dialogs.SetClipboard(plan.Instructions ?? page); else if (choice == 1) await Dialogs.OpenUrl(page);
                continue;                                                                      // still below the minimum: ask again
            }
            else
            {
                // the release could not be checked or trusted (offline, GitHub down): the same, with a retry
                var body = Ctx.T("App.ForcedUnavailable", req.Version) + extra;
                var choice = await Dialogs.Show(body, title, new[] { Ctx.T("App.ForcedRetry"), Ctx.T("App.UpdateView"), Ctx.T("App.ForcedQuit") }, 0);
                if (choice == 2) { UpdateRestart.Quit(); return; }
                if (choice == 1) await Dialogs.OpenUrl(page);
            }
            await CheckForUpdateAsync();                                                       // look again: the network may be back, or a better release published (the loop asks again if it still cannot be met)
        }
    }
}
