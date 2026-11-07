using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Start-up state shared between Program (before Avalonia exists) and the app: the link the app was started with and the single-instance server.</summary>
public static class AppLaunch
{
    public static string? StartupLink { get; set; }
    public static SingleInstance? Primary { get; set; }
    /// <summary>The name the lock was taken under, and what to do with a message from a later launch: kept so the lock can be taken back (see UpdateRestart).</summary>
    public static string? PipeName { get; set; }
    public static Action<string>? OnMessage { get; set; }
}

/// <summary>
/// Handles <c>syn3updater://</c> links, whether they started the app, arrived from a second launch or (macOS) from the OS while it runs.
/// A link only preselects the region / release / maps on Home; the user still presses Start.
/// </summary>
public static class UrlIntents
{
    /// <summary>A link waiting for the car configuration to exist (a fresh profile goes to Settings first); applied when Home becomes available.</summary>
    public static UrlIntent? Pending { get; private set; }

    public static async Task HandleAsync(string raw)
    {
        var intent = UrlIntentParser.Parse(raw);
        if (intent == null) { Ctx.Log.Info("Ignored a syn3updater link that has nothing usable in it"); return; }
        Ctx.Log.Info($"URL intent: {intent}");
        Pending = intent;
        await ApplyPendingAsync();
    }

    public static async Task ApplyPendingAsync()
    {
        if (Pending == null) return;
        var main = MainWindowViewModel.Instance;
        if (Workflow.Busy) { Pending = null; await Dialogs.Error(Ctx.T("MessageBox.DownloadInProgress")); return; }
        if (!main.ProfileReady) { await main.GoToAsync(main.Home); return; }          // lands on Settings; the link stays pending until Home is shown
        if (!await main.GoToAsync(main.Home)) { Pending = null; return; }             // the user chose to stay on unsaved Settings
        var intent = Pending; Pending = null;
        if (intent != null) await main.Home.ApplyIntentAsync(intent);
    }
}
