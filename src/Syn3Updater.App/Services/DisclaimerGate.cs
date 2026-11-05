namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>First start: a welcome popup, then the disclaimer popup, until the disclaimer has been accepted.</summary>
public static class DisclaimerGate
{
    /// <summary>Returns true when the disclaimer is (now) accepted. Choosing Exit calls <paramref name="exit"/> and returns false.</summary>
    public static async Task<bool> EnsureAcceptedAsync(Func<Task> exit)
    {
        if (Ctx.Main.DisclaimerAccepted) return true;

        await Dialogs.Show(Ctx.T("App.WelcomeBody"), Ctx.T("App.WelcomeTitle"), new[] { Ctx.T("App.Continue") }, options: new DialogOptions(Logo: true, Markdown: true));

        var choice = await Dialogs.Show(Ctx.T("App.DisclaimerBody"), Ctx.T("App.DisclaimerPopupTitle"),
            new[] { Ctx.T("App.Exit"), Ctx.T("App.Accept") }, defaultIndex: 0, options: new DialogOptions(Markdown: true));
        if (choice == 1)
        {
            Ctx.Main.DisclaimerAccepted = true; Ctx.Save();
            Ctx.Log.Info("Disclaimer accepted");
            return true;
        }
        await exit();
        return false;
    }
}
