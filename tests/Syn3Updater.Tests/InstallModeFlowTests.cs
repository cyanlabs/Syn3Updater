using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// The install-mode flow as drawn in docs/install-mode-logic.md (the flowchart "Current Version? / My20 Protection? / Update Maps? / ESN Locked? / Update Apps?"),
/// written out literally and compared with what v3 really does for every combination of inputs.
/// </summary>
public class InstallModeFlowTests
{
    private enum Outcome { NotPossible, Reformat, Autoinstall, Downgrade }

    /// <summary>The flowchart, node by node. "Protected" is MY20 Enabled; Auto-Detect and Disabled both follow the "Not MY20 Protected" arrows (the app asks the user at Start).</summary>
    private static Outcome Flowchart(int version, bool protectedMy20, bool updateMaps, bool keepExistingApps, bool esnLocked)
    {
        if (version < 3200000) return protectedMy20 ? Outcome.NotPossible : Outcome.Reformat;                     // "3.0 or below": MY20 protected -> not possible, else reformat
        if (!updateMaps) return Outcome.Autoinstall;                                                                // keep maps / non-nav -> autoinstall
        if (protectedMy20) return esnLocked ? Outcome.NotPossible : Outcome.Autoinstall;                           // protected: ESN locked -> not possible, unlocked -> autoinstall
        if (keepExistingApps) return esnLocked ? Outcome.NotPossible : Outcome.Autoinstall;                        // not protected, keep existing apps ("Only update maps")
        return version < 3419274 ? Outcome.Reformat : Outcome.Downgrade;                                            // not protected, update the apps: by version
    }

    /// <summary>What v3 does: the maps list greys out blocked ESN maps (EsnBlocked), then the resolver picks the mode (no fixed mode in play).</summary>
    private static Outcome V3(int version, bool? my20, MapChoice kind, bool onlyMaps, bool esn)
    {
        if (version >= ApiEndpoints.ReformatVersion && kind == MapChoice.MapPackage && InstallModeResolver.EsnBlocked(onlyMaps, my20, esn)) return Outcome.NotPossible;
        var r = InstallModeResolver.Resolve(version, my20, false, kind, esn, onlyMaps);
        if (r.Invalid) return Outcome.NotPossible;
        return r.Mode switch { InstallModes.Reformat => Outcome.Reformat, InstallModes.Downgrade => Outcome.Downgrade, InstallModes.Autoinstall => Outcome.Autoinstall, _ => throw new Xunit.Sdk.XunitException("no mode and not invalid") };
    }

    [Fact]
    public void V3_FollowsTheFlowchart_ForEveryCombination()
    {
        var versions = new[] { 0, 3000000, 3100000, 3199999, 3200000, 3200001, 3300000, 3419200, 3419273, 3419274, 3419275, 3426057 };   // below, on and around each threshold
        var maps = new[] { (Choice: MapChoice.MapPackage, Update: true), (MapChoice.NoMaps, false), (MapChoice.NonNavApim, false), (MapChoice.KeepExisting, false) };
        var n = 0;
        foreach (var v in versions) foreach (bool? my20 in new bool?[] { null, true, false }) foreach (var (kind, update) in maps)
        foreach (var onlyMaps in new[] { false, true }) foreach (var esn in new[] { false, true })
        {
            var want = Flowchart(v, my20 == true, update, onlyMaps, esn);
            var got = V3(v, my20, kind, onlyMaps, esn);
            Assert.True(want == got, $"version {v}, MY20 {(my20 == null ? "auto-detect" : my20 == true ? "protected" : "not protected")}, maps {kind}, only-maps release {onlyMaps}, ESN {esn}: the flowchart says {want} but v3 does {got}");
            n++;
        }
        Assert.Equal(12 * 3 * 4 * 2 * 2, n);
    }

    [Theory]
    [InlineData(3419273, "Reformat")] [InlineData(3419274, "Downgrade")]
    public void TheDowngradeBoundary_IsExactlyWhereTheFlowchartSaysItIs(int version, string expected)
    {
        // the chart labels the ranges "3.2 to 3.4.19200" and "3.4.19274+"; the code and v2 split at 3.4.19274 with nothing in between
        Assert.Equal(expected, V3(version, false, MapChoice.MapPackage, false, false).ToString());
        Assert.Equal(expected, Flowchart(version, false, true, false, false).ToString());
    }

    [Fact]
    public void AnEsnLockedMap_CanNeverBeChosen_WhenMy20IsProtectedOrTheReleaseIsOnlyMaps()
    {
        Assert.True(InstallModeResolver.EsnBlocked(false, true, true));
        Assert.True(InstallModeResolver.EsnBlocked(true, false, true)); Assert.True(InstallModeResolver.EsnBlocked(true, null, true));
        Assert.False(InstallModeResolver.EsnBlocked(false, false, true)); Assert.False(InstallModeResolver.EsnBlocked(false, null, true));
        Assert.False(InstallModeResolver.EsnBlocked(true, true, false));                                           // a map that is not ESN-locked is never blocked
    }
}
