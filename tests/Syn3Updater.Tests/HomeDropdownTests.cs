using Avalonia.VisualTree;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.App.Views;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class HomeDropdownTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

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
    public async Task EachPackageHasAnInfoLink_ToItsPageInTheCyanLabsDatabase()
    {
        using var env = new AppEnv(); var home = env.Main.Home; await PickEverything(env);
        foreach (var row in home.Packages)
        {
            Assert.True(row.HasInfo);
            Assert.Equal("https://cyanlabs.net/fhub/sync3/sw-db/?ivsu=" + Uri.EscapeDataString(row.Ivsu.Name), row.InfoUrl);
        }
        await home.Packages[0].OpenInfoCommand.ExecuteAsync(null);
        Assert.Equal(new[] { home.Packages[0].InfoUrl }, env.Dialogs.Opened);
        Assert.True(home.Packages[0].Selected);                                           // opening the page is not a click on the package
    }

    [AvaloniaFact]
    public void TheInfoLink_EscapesTheName_AndIsHiddenWithoutOne()
    {
        Assert.Equal("https://cyanlabs.net/fhub/sync3/sw-db/?ivsu=A%20B%26C%231", new PackageRow(new Cyanlabs.Syn3Updater.Core.Models.Ivsu { Name = "A B&C#1" }).InfoUrl);
        Assert.False(new PackageRow(new Cyanlabs.Syn3Updater.Core.Models.Ivsu { Name = "  " }).HasInfo);
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

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectADirectory_CanBePickedAgainAfterCancelling(bool aFolderWasChosenFirst)
    {
        using var env = new AppEnv(); var home = env.Main.Home; home.ReloadSummary();
        if (aFolderWasChosenFirst) { env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null); }
        else await home.RefreshDrivesAsync();
        var view = new HomeView { DataContext = home }; var window = new Window { Content = view }; window.Show();
        var box = view.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, home.Drives));
        var before = home.SelectedDrive; env.Dialogs.FolderToPick = null;                    // the folder dialog is cancelled every time

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            box.SelectedItem = home.Drives[^1];                                              // the user picks "Select A Directory" in the list
            await AppEnv.Until(() => env.Dialogs.FolderPicks >= attempt + (aFolderWasChosenFirst ? 1 : 0));
            await AppEnv.Until(() => ReferenceEquals(box.SelectedItem, before));              // and the list goes back to what was selected, ready for the next pick
            Assert.Same(before, home.SelectedDrive);
        }
        window.Close();
    }

    [Fact]
    public void TheMapList_DisablesBlockedMaps_AndShowsTheEsnLines()
    {
        // the popup's items are not realised in the headless platform, so the markup is checked instead: a blocked map's list item is disabled, and each row shows the lock and the ESN line
        var xaml = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/HomeView.axaml"));
        var box = xaml[xaml.IndexOf("x:Name=\"MapBox\"", StringComparison.Ordinal)..];
        box = box[..box.IndexOf("</ComboBox>", StringComparison.Ordinal)];
        Assert.Contains("<Setter Property=\"IsEnabled\" Value=\"{Binding IsSelectable}\"", box);
        Assert.Contains("IsVisible=\"{Binding IsLocked}\"", box); Assert.Contains("IsVisible=\"{Binding IsUnlocked}\"", box);
        Assert.Contains("Text=\"{Binding Detail}\"", box);
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
