using Avalonia.Headless.XUnit;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// The release, map and package lists are filled after waiting for the backend. When a second load started while the first was still waiting, both added their rows: every package was
/// listed twice (or, with several overlaps, 108 rows for 18 packages) until the app was restarted.
/// </summary>
public class HomeLoadRaceTests
{
    private sealed class Hold
    {
        private readonly TaskCompletionSource _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Held;
        public void Install(AppEnv env, string query) => env.Api.Gate = async q => { if (q.Contains(query)) { Interlocked.Increment(ref Held); await _open.Task; } };
        public void Release() => _open.TrySetResult();
    }

    private static async Task<(AppEnv env, Cyanlabs.Syn3Updater.App.ViewModels.HomeViewModel home)> Ready()
    {
        var env = new AppEnv(); var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        return (env, home);
    }

    [AvaloniaFact]
    public async Task Choosing_the_same_maps_again_while_packages_are_loading_does_not_list_every_package_twice()
    {
        var (env, home) = await Ready(); using var _ = env;
        var hold = new Hold(); hold.Install(env, "releases(limit");
        var map = home.MapVersions[0];
        home.SelectedMap = map; await AppEnv.Until(() => hold.Held == 1);                  // first load, waiting for the server
        home.SelectedMap = null; home.SelectedMap = map; await AppEnv.Until(() => hold.Held == 2);          // the selection fires again
        hold.Release();
        await AppEnv.Until(() => home.Packages.Count > 0); await Task.Delay(300);
        Assert.Equal(2, home.Packages.Count);
        Assert.Equal(home.Packages.Select(p => p.Ivsu.Name).Distinct().Count(), home.Packages.Count);
        Assert.False(home.Loading);
    }

    [AvaloniaFact]
    public async Task Five_overlapping_package_loads_still_give_one_copy_of_each_package()
    {
        var (env, home) = await Ready(); using var _ = env;
        var hold = new Hold(); hold.Install(env, "releases(limit");
        var map = home.MapVersions[0];
        for (var i = 1; i <= 5; i++) { home.SelectedMap = null; home.SelectedMap = map; await AppEnv.Until(() => hold.Held == i); }
        hold.Release();
        await AppEnv.Until(() => home.Packages.Count > 0); await Task.Delay(300);
        Assert.Equal(new[] { "APP1", "VOICE1" }, home.Packages.Select(p => p.Ivsu.Name).OrderBy(n => n));
    }

    [AvaloniaFact]
    public async Task Packages_that_arrive_after_the_choices_were_cleared_are_not_added()
    {
        var (env, home) = await Ready(); using var _ = env;
        var hold = new Hold(); hold.Install(env, "releases(limit");
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => hold.Held == 1);
        home.ClearSelections();                                                               // Settings applied / profile switched while the packages were still coming
        hold.Release(); await Task.Delay(500);
        Assert.Empty(home.Packages); Assert.False(home.StartEnabled);
    }

    [AvaloniaFact]
    public async Task Two_overlapping_release_loads_list_each_release_once()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        var hold = new Hold(); hold.Install(env, "releases(sort");
        home.ReloadSummary(); await AppEnv.Until(() => hold.Held == 1);
        home.ClearSelections(); await AppEnv.Until(() => hold.Held == 2);
        hold.Release();
        await AppEnv.Until(() => home.Releases.Count > 0); await Task.Delay(300);
        Assert.Single(home.Releases); Assert.False(home.Loading);
    }

    [AvaloniaFact]
    public async Task The_busy_indicator_stays_on_until_the_last_overlapping_load_has_finished()
    {
        var (env, home) = await Ready(); using var _ = env;
        var first = new Hold(); first.Install(env, "releases(limit");
        var map = home.MapVersions[0];
        home.SelectedMap = map; await AppEnv.Until(() => first.Held == 1);
        Assert.True(home.Loading);
        first.Release(); await AppEnv.Until(() => home.Packages.Count > 0); await AppEnv.Until(() => !home.Loading);
        Assert.False(home.Loading);
    }
}
