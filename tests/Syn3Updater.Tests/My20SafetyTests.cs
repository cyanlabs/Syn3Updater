using System.Text.Json;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>MY20 protection behaves as it did in v2: a fixed install mode only exists while protection is off, and the mode choice follows v2's decision table.</summary>
public class My20SafetyTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3my20").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Theory]
    [InlineData(null, "reformat", false)] [InlineData(true, "reformat", false)] [InlineData(false, "reformat", true)]
    [InlineData(null, "downgrade", false)] [InlineData(true, "downgrade", false)] [InlineData(false, "downgrade", true)]
    [InlineData(false, "autoinstall", true)] [InlineData(null, "autoinstall", false)]
    [InlineData(false, "autodetect", false)] [InlineData(true, "autodetect", false)]
    public void AFixedMode_OnlyCountsWhileMy20ProtectionIsExplicitlyOff(bool? my20, string mode, bool forced)
    {
        var p = new ProfileSettings { My20v2 = false, InstallMode = mode };    // the mode is chosen while protection is off...
        p.My20v2 = my20;                                                        // ...then protection changes
        Assert.Equal(forced, p.ModeForced);
        Assert.Equal(forced ? mode : InstallModes.AutoDetect, p.InstallMode);   // and a fixed mode that no longer applies is dropped, not just ignored
    }

    [Fact]
    public void FindingAnMy20Unit_DropsAFixedReformatOrDowngrade()
    {
        foreach (var mode in new[] { "reformat", "downgrade" })
        {
            var p = new ProfileSettings { My20v2 = false, InstallMode = mode }; Assert.True(p.ModeForced);
            p.My20v2 = true;                                                    // what the Interrogator-log detection does
            Assert.False(p.ModeForced); Assert.Equal("autodetect", p.InstallMode);
            p.My20v2 = false; Assert.False(p.ModeForced);                       // switching protection off again does not bring the old mode back
        }
    }

    [Theory]
    [InlineData("""{ "My20v2": false, "InstallMode": "reformat" }""", "reformat")]
    [InlineData("""{ "InstallMode": "reformat", "My20v2": false }""", "reformat")]     // field order must not matter
    [InlineData("""{ "My20v2": null, "InstallMode": "reformat" }""", "autodetect")]
    [InlineData("""{ "InstallMode": "reformat", "My20v2": null }""", "autodetect")]
    [InlineData("""{ "InstallMode": "downgrade", "My20v2": true }""", "autodetect")]
    [InlineData("""{ "InstallMode": "downgrade" }""", "autodetect")]                     // no MY20 answer at all
    public void AProfileOnDisk_NeverKeepsAFixedModeWithoutMy20BeingOff(string json, string expected)
    {
        var paths = new AppPaths(_dir); File.WriteAllText(paths.ProfileFile("default"), json);
        var s = new SettingsStore(paths); s.Load();
        Assert.Equal(expected, s.Profile.InstallMode); Assert.Equal(expected != "autodetect", s.Profile.ModeForced);
    }

    [Fact]
    public void ModeForced_IsNotWrittenToTheProfileFile()
    {
        var paths = new AppPaths(_dir); var s = new SettingsStore(paths); s.Load(); s.Profile.My20v2 = false; s.Profile.InstallMode = "reformat"; s.Save();
        Assert.False(JsonDocument.Parse(File.ReadAllText(paths.ProfileFile("default"))).RootElement.TryGetProperty("ModeForced", out _));
    }

    [AvaloniaFact]
    public void TheSettingsPage_OnlyLetsYouFixAModeWhileMy20IsOff_AndResetsItOtherwise()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s; s.OnShown();
        Assert.False(s.ModeEnabled);                                           // MY20 on auto-detect: the dropdown is disabled
        s.My20 = s.My20Options.Single(o => o.Value == "off"); Assert.True(s.ModeEnabled);
        s.Mode = s.ModeOptions.Single(o => o.Value == "reformat"); Assert.Equal("reformat", s.Mode!.Value);
        s.My20 = s.My20Options.Single(o => o.Value == "on"); Assert.False(s.ModeEnabled); Assert.Equal("autodetect", s.Mode!.Value);          // protection on: back to auto-detect
        s.My20 = s.My20Options.Single(o => o.Value == "off"); s.Mode = s.ModeOptions.Single(o => o.Value == "downgrade");
        s.My20 = s.My20Options.Single(o => o.Value == "auto"); Assert.Equal("autodetect", s.Mode!.Value);                                       // auto-detect: same
        s.My20 = s.My20Options.Single(o => o.Value == "off"); s.Advanced = true; s.Mode = s.ModeOptions.Single(o => o.Value == "reformat");
        s.Advanced = false; Assert.Equal("autodetect", s.Mode!.Value);                                                                          // closing Advanced: same
    }

    [AvaloniaFact]
    public async Task Saving_NeverStoresAFixedModeUnlessMy20IsOff()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s; s.OnShown();
        s.Mode = s.ModeOptions.Single(o => o.Value == "reformat");             // forced in code while protection is on auto
        await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal("autodetect", Ctx.Profile.InstallMode); Assert.False(Ctx.ModeForced);
        s.My20 = s.My20Options.Single(o => o.Value == "off"); s.Mode = s.ModeOptions.Single(o => o.Value == "reformat");
        await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal("reformat", Ctx.Profile.InstallMode); Assert.True(Ctx.ModeForced); Assert.False(Ctx.Profile.My20v2);
    }

    [AvaloniaFact]
    public async Task TheInterrogatorLog_FindingMy20_LeavesNoFixedReformatBehind()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = "reformat"; Assert.True(Ctx.ModeForced);
        var xml = Path.Combine(env.Dir, "Sync_1_VIN.xml");
        File.WriteAllText(xml, """
            <p:OTAModuleSnapShot xmlns:p="u" xmlns:d="d"><p:VIN>1FA</p:VIN>
             <d:DID didType="Embedded Consumer Operating System Part Number" didValue="D133"><d:Response>GB5T-14G381-AB</d:Response></d:DID>
             <d:DID didType="ECU Delivery Assembly Number" didValue="F111"><d:Response>LB5T-14G371-AF</d:Response></d:DID>
             <d:AdditionalAttributes logGeneratedDateTime="2024-01-02T03:04:05+00:00"><d:PartitionHealth type="/fs/images/" total="28.5G" available="12.0G"/></d:AdditionalAttributes>
            </p:OTAModuleSnapShot>
            """);
        env.Dialogs.FileToPick = xml; await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(Ctx.Profile.My20v2); Assert.False(Ctx.ModeForced); Assert.Equal("autodetect", Ctx.Profile.InstallMode);              // "Syn3 Updater will now enforce AutoInstall mode", as the message promises
    }

    // ---- the install mode chosen from MY20 / version / maps: v2's decision table, ported literally from HomeViewModel.UpdateInstallMode ----
    private static (string? Mode, bool Cancelled) V2(int version, bool? my20, bool forced, bool noMaps, bool esn, bool onlyMaps)
    {
        string? mode = null; bool cancelled = false;
        if (version < ApiEndpoints.ReformatVersion && my20 == true) { mode = "autoinstall"; cancelled = true; }                                    // Option H
        else if (version < ApiEndpoints.ReformatVersion) { if (!forced) mode = "reformat"; }                                                        // Option A
        else if (version >= ApiEndpoints.ReformatVersion && version < ApiEndpoints.BlacklistedVersion)
        {
            if (noMaps) { if (!forced) mode = "autoinstall"; }                                                                                      // B
            else if (my20 == true && esn) cancelled = true;
            else if (my20 == true || onlyMaps) mode = "autoinstall";                                                                                // C
            else if (!forced) mode = "reformat";                                                                                                    // D
        }
        else
        {
            if (noMaps) { if (!forced) mode = "autoinstall"; }                                                                                      // E
            else if (my20 == true && esn) cancelled = true;
            else if (my20 == true || onlyMaps) mode = "autoinstall";                                                                                // F
            else if (!forced) mode = "downgrade";                                                                                                   // G
        }
        return (mode, cancelled);
    }

    [Fact]
    public void TheInstallModeChoice_MatchesV2_ForEveryCombination()
    {
        var versions = new[] { 0, 3000000, 3199999, 3200000, 3200001, 3300000, 3419273, 3419274, 3419275, 3426057 };   // either side of both thresholds
        var kinds = new[] { MapChoice.MapPackage, MapChoice.NoMaps, MapChoice.NonNavApim, MapChoice.KeepExisting };
        int checkedCases = 0;
        foreach (var v in versions) foreach (bool? my20 in new bool?[] { null, true, false }) foreach (var forced in new[] { false, true })
        foreach (var kind in kinds) foreach (var esn in new[] { false, true }) foreach (var onlyMaps in new[] { false, true })
        {
            var want = V2(v, my20, forced, kind != MapChoice.MapPackage, esn, onlyMaps);
            var got = InstallModeResolver.Resolve(v, my20, forced, kind, esn, onlyMaps);
            Assert.True(want.Mode == got.Mode && want.Cancelled == got.Invalid,
                $"version {v}, my20 {my20}, forced {forced}, maps {kind}, esn {esn}, onlyMaps {onlyMaps}: v2 gives ({want.Mode ?? "unchanged"}, cancelled {want.Cancelled}) but v3 gives ({got.Mode ?? "unchanged"}, invalid {got.Invalid})");
            checkedCases++;
        }
        Assert.Equal(10 * 3 * 2 * 4 * 2 * 2, checkedCases);
    }

    [Fact]
    public void TheVersionThresholds_AreV2s()
    {
        Assert.Equal(3200000, ApiEndpoints.ReformatVersion); Assert.Equal(3419274, ApiEndpoints.BlacklistedVersion);
    }
}
