namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Shows the disclaimer as a popup at start-up until it has been accepted.</summary>
public static class DisclaimerGate
{
    /// <summary>Returns true when the disclaimer is (now) accepted. Choosing Exit calls <paramref name="exit"/> and returns false.</summary>
    public static async Task<bool> EnsureAcceptedAsync(Func<Task> exit)
    {
        if (Ctx.Main.DisclaimerAccepted) return true;
        var message = $"{Ctx.T("MessageBox.DisclaimerNotAccepted")}\n\n{Ctx.T("About.Disclaimer")}\n\n{Ctx.T("About.Endorsed")}";
        var choice = await Dialogs.Show(message, Ctx.T("About.DisclaimerTitle"), new[] { Ctx.T("App.Exit"), Ctx.T("App.Accept") }, defaultIndex: 0);
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
