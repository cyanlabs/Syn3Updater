using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Models;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// The package list shows the release's packages A-Z by type, then the map release's packages A-Z by type, then the required tools. The job (and so autoinstall.lst and reformat.lst) still gets the
/// packages in the order the backend lists them: the order on screen is only for reading.
/// </summary>
public class PackageOrderTests
{
    // the backend's order, deliberately jumbled
    private static readonly (string, string)[] Release = { ("Z-VOICE", "VOICE"), ("B-APP", "APPS"), ("GRACE1", "GRACENOTES"), ("DAB1", "ENH_DAB"), ("A-VOICE", "VOICE") };
    private static readonly (string, string)[] Maps = { ("NV1", "VOICE_NAV"), ("LIC1", "MAP_LICENSE"), ("MAP1", "MAP") };

    private static async Task<(AppEnv env, HomeViewModel home)> Setup()
    {
        var env = new AppEnv(version: 3420282, nav: true);
        env.Api.ReleasePackages = Release.ToList(); env.Api.MapPackages = Maps.ToList();
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = InstallModes.Downgrade;            // a downgrade: the four tools are listed too
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == 12);        // 5 release + 3 map + 4 tools
        return (env, home);
    }

    [AvaloniaFact]
    public async Task Release_packages_come_first_A_to_Z_by_type_then_map_packages_A_to_Z_by_type_then_the_tools()
    {
        var (env, home) = await Setup(); using var _ = env;
        Assert.Equal(new[]
        {
            "B-APP", "DAB1", "GRACE1", "A-VOICE", "Z-VOICE",                                      // release: APPS, ENH_DAB, GRACENOTES, VOICE (two of them by name)
            "MAP1", "LIC1", "NV1",                                                                 // map release: MAP, MAP_LICENSE, VOICE_NAV
            "downgradeapp", "gracenotesremoval", "reformat", "rwdatacleaner",                      // required tools last
        }, home.Packages.Select(r => r.Ivsu.Name));
        Assert.Equal(new[] { "APPS", "ENH_DAB", "GRACENOTES", "VOICE", "VOICE", "MAP", "MAP_LICENSE", "VOICE_NAV" }, home.Packages.Take(8).Select(r => r.Ivsu.Type));
    }

    [AvaloniaFact]
    public async Task The_install_lists_still_follow_the_backends_order_not_the_order_on_screen()
    {
        var (env, home) = await Setup(); using var _ = env;
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        await home.StartCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        var lines = File.ReadAllText(Path.Combine(env.UsbDir, "reformat.lst")).TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(new[]
        {
            "VOICE=Z-VOICE.tar.gz", "APPS=B-APP.tar.gz", "GRACENOTES=GRACE1.tar.gz", "ENH_DAB=DAB1.tar.gz", "VOICE=A-VOICE.tar.gz",    // exactly as the backend listed them
            "VOICE_NAV=NV1.tar.gz", "MAP_LICENSE=LIC1.tar.gz", "MAP=MAP1.tar.gz",
        }, lines);
    }

    [AvaloniaFact]
    public async Task Unticking_a_package_does_not_change_the_order_of_the_others_in_the_job()
    {
        var (env, home) = await Setup(); using var _ = env;
        home.Packages.Single(r => r.Ivsu.Name == "GRACE1").ToggleCommand.Execute(null);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        await home.StartCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        var lines = File.ReadAllText(Path.Combine(env.UsbDir, "reformat.lst")).TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(new[] { "VOICE=Z-VOICE.tar.gz", "APPS=B-APP.tar.gz", "ENH_DAB=DAB1.tar.gz", "VOICE=A-VOICE.tar.gz", "VOICE_NAV=NV1.tar.gz", "MAP_LICENSE=LIC1.tar.gz", "MAP=MAP1.tar.gz" }, lines);
    }

    [Fact]
    public void ByType_sorts_by_type_ignoring_case_then_by_name_and_keeps_equal_ones_in_a_steady_order()
    {
        Ivsu P(string n, string t) => new() { Name = n, Type = t };
        var sorted = HomeViewModel.ByType(new[] { P("b", "voice"), P("a", "VOICE"), P("x", "APPS"), P("c", "Map"), P("c", "MAP") }).Select(p => p.Name + ":" + p.Type);
        Assert.Equal(new[] { "x:APPS", "c:Map", "c:MAP", "a:VOICE", "b:voice" }, sorted);          // "c:Map" and "c:MAP" are equal for sorting, so they keep their given order
        Assert.Empty(HomeViewModel.ByType(Array.Empty<Ivsu>()));
    }

    [AvaloniaFact]
    public async Task A_plain_install_with_no_maps_sorts_the_release_packages_and_ends_with_the_cleaner()
    {
        using var env = new AppEnv(); env.Api.ReleasePackages = new() { ("V1", "VOICE"), ("D1", "ENH_DAB"), ("A1", "APPS") };
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 4);
        Assert.Equal(new[] { "A1", "D1", "V1", "rwdatacleaner" }, home.Packages.Select(r => r.Ivsu.Name));
    }
}
