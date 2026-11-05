using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

public class SetupGateTests
{
    [AvaloniaFact]
    public void AnUnconfiguredProfile_StartsOnSettings_WithTheBanner()
    {
        using var env = new AppEnv(region: "", version: 0);
        Assert.Same(env.Main.Settings, env.Main.Current); Assert.True(env.Main.Settings.ConfigRequired); Assert.False(env.Main.ProfileReady);
    }

    [AvaloniaFact]
    public async Task Home_StaysClosed_UntilRegionAndVersionAreSet()
    {
        using var env = new AppEnv(region: "", version: 0); var m = env.Main;
        Assert.False(await m.GoToAsync(m.Home)); Assert.Same(m.Settings, m.Current); Assert.Contains(Ctx.T("App.ConfigRequiredBox"), env.Dialogs.Messages);
        var box = env.Dialogs.Shown.Single(); Assert.Equal(Ctx.T("App.ConfigRequiredTitle"), box.Title); Assert.Equal(new[] { "OK" }, box.Buttons);   // a message box, with a single OK
        await m.Settings.ApplyClickCommand.ExecuteAsync(null);                               // applying nothing does not open Home either
        Assert.Same(m.Settings, m.Current); Assert.Contains(Ctx.T("App.ConfigRequiredBox"), env.Dialogs.Messages);
        m.ShowHome(); Assert.Same(m.Settings, m.Current);

        m.Settings.Region = m.Settings.Regions.First(r => r.Code == "NA");                    // region alone is not enough
        await m.Settings.ApplyClickCommand.ExecuteAsync(null); Assert.Same(m.Settings, m.Current);

        m.Settings.Version = "3.4.20282"; await m.Settings.ApplyClickCommand.ExecuteAsync(null);
        Assert.Same(m.Home, m.Current); Assert.True(m.ProfileReady); Assert.False(m.Settings.ConfigRequired);
    }

    [AvaloniaFact]
    public async Task OtherTabs_StillWork_WhileNotSetUp()
    {
        using var env = new AppEnv(region: "", version: 0); var m = env.Main;
        Assert.True(await m.GoToAsync(m.Utility)); Assert.Same(m.Utility, m.Current);
        Assert.True(await m.GoToAsync(m.About)); Assert.False(await m.GoToAsync(m.Home)); Assert.Same(m.Settings, m.Current);   // even from another tab, Home sends you to Settings
    }

    [AvaloniaFact]
    public async Task SavingValidSettings_FromTheLeavePrompt_OpensHome()
    {
        using var env = new AppEnv(region: "", version: 0); var m = env.Main;
        m.Settings.Region = m.Settings.Regions.First(r => r.Code == "EU"); m.Settings.Version = "3.4.20282";
        env.Dialogs.Choices.Enqueue(2);
        Assert.True(await m.GoToAsync(m.Home)); Assert.Same(m.Home, m.Current);
    }

    [AvaloniaFact]
    public async Task NewProfile_StartsOnSettings_AndCannotReachHomeYet()
    {
        using var env = new AppEnv(); var m = env.Main; Assert.Same(m.Home, m.Current);
        env.Dialogs.PromptAnswer = "Wife"; await m.AddProfileCommand.ExecuteAsync(null);
        Assert.Same(m.Settings, m.Current); Assert.False(await m.GoToAsync(m.Home)); Assert.Same(m.Settings, m.Current);
    }
}
