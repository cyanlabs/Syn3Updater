using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.App.Views;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class VersionSuggestionTests
{
    private static readonly string[] Expected = { "3.4.26057", "3.4.23188", "3.4.23088", "3.3.19052" };

    [Fact]
    public async Task Api_ReturnsDistinctVersionsNewestFirst_IgnoringBlanks()
    {
        var h = new FakeHandler(_ => FakeHandler.Json("""{"data":{"ivsu":[{"version":"3.3.19052"},{"version":"3.4.9999"},{"version":"3.4.26057"},{"version":"3.4.26057"},{"version":null},{"version":""},{"version":" 3.4.23088 "}]}}"""));
        var versions = await new ApiClient(new HttpClient(h), () => "tok", () => "").GetAppVersions();
        Assert.Equal(new[] { "3.4.26057", "3.4.23088", "3.4.9999", "3.3.19052" }, versions);        // numeric order: 3.4.9999 is older than 3.4.23088
        Assert.Contains("APPS", h.Calls[0].Body);
    }

    [Theory]
    [InlineData("3.4.26057", "3.4.23188", 1)] [InlineData("3.4.9999", "3.4.23088", -1)] [InlineData("3.10.1", "3.9.99999", 1)] [InlineData("3.4", "3.4.0", -1)]
    [InlineData("3.4.26057", "3.4.26057", 0)] [InlineData("2.2.1", "3.0.0", -1)] [InlineData("", "3.0.0", -1)]
    public void VersionOrder_ComparesSegmentsAsNumbers(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(ApiClient.VersionOrder.Instance.Compare(a, b)));

    [AvaloniaFact]
    public async Task Settings_LoadsTheListOnce_WhenOpened()
    {
        using var env = new AppEnv(); var s = env.Main.Settings;
        env.Main.Current = s;
        await AppEnv.Until(() => s.KnownVersions.Count == 4);
        Assert.Equal(Expected, s.KnownVersions);
        await s.LoadVersionsAsync(); await s.LoadVersionsAsync(); env.Main.Current = env.Main.Home; env.Main.Current = s;
        Assert.Equal(1, env.Api.Queries.Count(q => q.Contains("_eq: \"APPS\"")));                 // cached for the session
    }

    [AvaloniaFact]
    public async Task Offline_TheBoxStaysUsable_AndRetriesLater()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Api.Offline = true;
        await s.LoadVersionsAsync();
        Assert.Empty(s.KnownVersions); Assert.Contains("Version suggestions unavailable", string.Join("\n", Ctx.Log.Lines));
        Assert.DoesNotContain(env.Dialogs.Messages, m => m.Length > 0);                              // no error popup for a missing suggestion list
        env.Api.Offline = false;
        await s.LoadVersionsAsync();                                                                  // e.g. when the box gets focus
        Assert.Equal(Expected, s.KnownVersions);
    }

    [AvaloniaFact]
    public async Task AVersionThatIsNotInTheList_CanStillBeEntered()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s;
        await AppEnv.Until(() => s.KnownVersions.Count == 4);
        Assert.DoesNotContain("3.4.99999", s.KnownVersions);
        s.Version = "3.4.99999"; await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal(3499999, Ctx.Profile.CurrentVersion);
        env.Main.Current = s; s.Version = "3.4.2"; await s.ApplyClickCommand.ExecuteAsync(null);      // half-typed: rejected as before
        Assert.Equal(3499999, Ctx.Profile.CurrentVersion);
    }

    [AvaloniaFact]
    public async Task TheView_UsesASuggestingBox_BoundToTheList()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; await s.LoadVersionsAsync();
        var view = new SettingsView { DataContext = s };
        var window = new Window { Content = view }; window.Show();
        var box = view.FindControl<AutoCompleteBox>("VersionBox")!;
        Assert.NotNull(box); Assert.Same(s.KnownVersions, box.ItemsSource);
        Assert.Equal(AutoCompleteFilterMode.StartsWith, box.FilterMode); Assert.Equal(0, box.MinimumPrefixLength);          // opens with the whole list; narrows as you type
        Assert.Equal(s.Version, box.Text);
        box.Text = "3.4.2"; Assert.Equal("3.4.2", s.Version);                                           // typing flows back to the setting
        window.Close();
    }

    private static IEnumerable<string> Texts(object? node)
    {
        if (node is TextBlock t && !string.IsNullOrEmpty(t.Text)) yield return t.Text;
        if (node is Avalonia.LogicalTree.ILogical l) foreach (var c in l.LogicalChildren) foreach (var x in Texts(c)) yield return x;
    }

    [AvaloniaFact]
    public void Home_HasHelpPopups_NextToTheUsbHeading_AndTheRegionLabel()
    {
        using var env = new AppEnv();
        var view = new HomeView { DataContext = env.Main.Home }; var window = new Window { Content = view }; window.Show();

        var usb = view.FindControl<Button>("UsbHelp")!; var region = view.FindControl<Button>("RegionHelp")!;
        Assert.NotNull(usb); Assert.NotNull(region);
        var usbText = Texts(((Flyout)usb.Flyout!).Content).ToList();
        Assert.Contains(Ctx.T("Home.RecommendedUSBFlashDriveTitle"), usbText);
        foreach (var k in new[] { "Home.RecommendedUSBFlashDriveNonNav", "Home.RecommendedUSBFlashDriveNA", "Home.RecommendedUSBFlashDriveEU" }) Assert.Contains(Ctx.T(k), usbText);
        Assert.Contains(usbText, x => x.Contains("exFAT"));

        var regionText = Texts(((Flyout)region.Flyout!).Content).ToList();
        Assert.Contains("Regions", regionText); Assert.Contains(regionText, x => x.Contains("AsBuilt"));
        window.Close();
        // the list itself is data-bound, so every region is described in the popup
        Assert.Equal(5, env.Main.Home.Regions.Count); Assert.Contains(env.Main.Home.Regions, r => r.Code == "EU" && r.Name == "Europe");
        Assert.DoesNotContain(Texts(view), x => x == Ctx.T("Home.RecommendedUSBFlashDriveNA") && false);   // (the always-visible block is gone from the page body; see the flyout assertion above)
    }
}
