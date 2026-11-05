using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.App.Views;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class HomeDropdownTests
{
    private static async Task PickEverything(AppEnv env)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 2);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Apply_ClearsRegionVersionAndMaps_ButNotTheDrive()
    {
        using var env = new AppEnv(); var home = env.Main.Home; await PickEverything(env);
        Assert.NotNull(home.SelectedRegion); Assert.NotNull(home.SelectedRelease); Assert.NotNull(home.SelectedMap); var drive = home.SelectedDrive; Assert.True(drive!.IsFolder);

        env.Main.Current = env.Main.Settings;
        await env.Main.Settings.ApplyClickCommand.ExecuteAsync(null);                       // even with nothing changed: Apply starts the choices afresh

        Assert.Same(env.Main.Home, env.Main.Current);
        Assert.Null(home.SelectedRegion); Assert.Null(home.SelectedRelease); Assert.Null(home.SelectedMap);
        Assert.Empty(home.Releases); Assert.Empty(home.MapVersions); Assert.Empty(home.Packages); Assert.False(home.StartEnabled);
        Assert.Same(drive, home.SelectedDrive);                                              // the USB / folder choice stays
        home.ReloadSummary();
        Assert.Null(home.SelectedRegion);                                                    // and the region is not quietly picked again for you
    }

    [AvaloniaFact]
    public async Task ThePlaceholdersAskYouToSelect()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        var view = new HomeView { DataContext = home }; var window = new Window { Content = view }; window.Show();
        Assert.Equal("Select Region", view.FindControl<ComboBox>("RegionBox")!.PlaceholderText);
        Assert.Equal("Select Version", view.FindControl<ComboBox>("ReleaseBox")!.PlaceholderText);
        Assert.Equal("Select Maps", view.FindControl<ComboBox>("MapBox")!.PlaceholderText);
        window.Close(); await Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task LeavingSettings_OnlyClearsTheChoicesWhenTheCarConfigChanged()
    {
        using var env = new AppEnv(); var home = env.Main.Home; await PickEverything(env); var s = env.Main.Settings;

        env.Main.Current = s; env.Main.Current = env.Main.Home;                              // visited Settings, changed nothing
        Assert.NotNull(home.SelectedRegion); Assert.NotNull(home.SelectedMap); Assert.Equal(2, home.Packages.Count);

        env.Main.Current = s; s.Version = "3.3.19052"; env.Main.Current = env.Main.Home;     // changed the version: the old choices no longer fit
        Assert.Equal(3319052, Ctx.Profile.CurrentVersion);
        Assert.Null(home.SelectedRegion); Assert.Null(home.SelectedMap); Assert.Empty(home.Packages);
        Assert.NotNull(home.SelectedDrive);
    }

    [AvaloniaFact]
    public async Task SwitchingProfile_StartsTheChoicesAfresh_KeepingTheDrive()
    {
        using var env = new AppEnv(); var home = env.Main.Home; await PickEverything(env);
        env.Dialogs.PromptAnswer = "Other"; await env.Main.AddProfileCommand.ExecuteAsync(null);
        Assert.Null(home.SelectedRegion); Assert.Null(home.SelectedRelease); Assert.Empty(home.Packages); Assert.NotNull(home.SelectedDrive);
    }
}
