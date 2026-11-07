using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.Views;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// Real Interrogator logs, anonymised (tools/sanitize-interrogator-log.py): My20.xml is the log of an MY20 unit (model 5U5T-14G371-BKC), NotMy20.xml of an older one (3U5T-14G371-ADD), Unknown.xml of an older one whose log carries no model at all.
/// They are what the Utility tab reads, so these tests keep both the parser and the sanitising honest.
/// </summary>
public class InterrogatorFixtureTests
{
    internal static string Fixture(string name)
    {
        var relative = Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Interrogator", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
    private static InterrogatorResult Read(string name, bool requireModel = false) => Interrogator.Parse(File.ReadAllText(Fixture(name)), requireModel);

    [Fact]
    public void My20Log_IsReadAsAnMy20NavigationUnit()
    {
        var r = Read("My20.xml", requireModel: true);
        Assert.Equal("5U5T-14G371-BKC", r.ApimModel);                 // the model the MY20 check compares with the API's list
        Assert.Equal("NU5T-14G381-AD", r.SoftwareName);
        Assert.True(r.Nav); Assert.Equal(64, r.ApimSizeGb); Assert.Equal("13G", r.ApimFree);
        Assert.Equal("XXXXXXXXXXXXXXXXX", r.Vin);
        Assert.Equal(new DateTimeOffset(2024, 10, 7, 10, 35, 0, TimeSpan.Zero), r.Generated);
        Assert.Equal(23, r.Partitions.Count); Assert.Equal(7, r.AsBuilt.Count);
    }

    [Fact]
    public void NotMy20Log_IsReadAsAnOlderNavigationUnit()
    {
        var r = Read("NotMy20.xml", requireModel: true);
        Assert.Equal("3U5T-14G371-ADD", r.ApimModel); Assert.True(r.ModelKnown);
        Assert.Equal("5U5T-14G381-ET", r.SoftwareName); Assert.Equal("", r.Vin);                      // this log has no VIN at all
        Assert.True(r.Nav); Assert.Equal(64, r.ApimSizeGb); Assert.Equal("11G", r.ApimFree);
        Assert.Equal(21, r.Partitions.Count);
        Assert.All(r.AsBuilt, d => Assert.Equal("Blank", d.Response));                                // a unit that was never configured reports no as-built data
    }

    [Fact]
    public void UnknownLog_HasNoModel_SoItIsReadableButItsModelIsUnknown()
    {
        var ex = Assert.Throws<InterrogatorException>(() => Read("Unknown.xml", requireModel: true));
        Assert.Equal("MODEL_NOT_FOUND", ex.Message);                  // the strict reading refuses it...
        var r = Read("Unknown.xml");                                  // ...the Utility tab's reading shows what is there
        Assert.Equal("Unknown", r.ApimModel); Assert.False(r.ModelKnown);
        Assert.Equal("5U5T-14G381-ET", r.SoftwareName); Assert.True(r.Nav); Assert.Equal(64, r.ApimSizeGb); Assert.Equal(21, r.Partitions.Count);
    }

    [Fact]
    public void PackagesAreListedWithoutTheEmptyEntriesOfThePadding()
    {
        var my20 = Read("My20.xml"); var old = Read("NotMy20.xml");
        Assert.Equal(16, my20.Packages.Count);                        // what the hex padding leaves behind includes empty entries...
        var list = Interrogator.PackageList(my20);
        Assert.Equal(13, list.Count); Assert.All(list, p => Assert.False(string.IsNullOrWhiteSpace(p)));
        Assert.Equal("1U5T-14G421-BAG", list[0]);
        Assert.Equal(16, Interrogator.PackageList(old).Count); Assert.Equal("1U5T-14G421-BAH", Interrogator.PackageList(old)[0]);
    }

    [Fact]
    public void PartitionUsage_ShowsHowFullEachPartitionIs()
    {
        var usage = Interrogator.Usage(Read("My20.xml"));
        Assert.Equal(23, usage.Count);
        var images = Assert.Single(usage, u => u.Path == "/fs/images/");
        Assert.Equal("13G", images.Free); Assert.Equal("54G", images.Total); Assert.Equal(1 - 13d / 54, images.Used, 6);
        var quip = usage.Where(u => u.Path == "/fs/rwdata/quip/").ToList();
        Assert.Equal(2, quip.Count); Assert.Equal(0, quip[0].Used);   // a partition that is entirely free: 35M of 35M
        Assert.All(usage, u => Assert.InRange(u.Used, 0, 1));
        Assert.Equal(usage.Select(u => u.Path), Read("My20.xml").Partitions.Select(p => p.Type));   // the log's order is kept
    }

    [Theory]
    [InlineData("396K", 396d * 1024)]
    [InlineData("5.7M", 5.7 * 1024 * 1024)]
    [InlineData("1003M", 1003d * 1024 * 1024)]
    [InlineData("1.2G", 1.2 * 1024 * 1024 * 1024)]
    [InlineData("54G", 54d * 1024 * 1024 * 1024)]
    [InlineData(" 11G ", 11d * 1024 * 1024 * 1024)]
    [InlineData("512B", 512d)]
    public void SizeText_ReadsTheUnitsTheLogUses(string text, double bytes) => Assert.Equal(bytes, SizeText.ToBytes(text)!.Value, 3);

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("G")] [InlineData("12")] [InlineData("abcG")] [InlineData("-5M")] [InlineData("12X")]
    public void SizeText_NotASize_IsNull(string? text) => Assert.Null(SizeText.ToBytes(text));

    [Fact]
    public void AnUnreadableSize_CountsAsZeroUsed_NotACrash()
    {
        var r = new InterrogatorResult(); r.Partitions.Add(new PartitionHealth { Type = "/x/", Total = "?", Available = "5M" });
        Assert.Equal(0, Interrogator.Usage(r).Single().Used);
    }

    [Fact]
    public void TheAsBuiltExport_NeverCarriesTheVin()
    {
        var export = Read("My20.xml").AsBuiltXml!;                    // the log above has a VIN; what can be uploaded for the as-built viewer must not
        Assert.Equal("", export.Root!.Element("VEHICLE")!.Attribute("VIN")!.Value);
        Assert.DoesNotContain("XXXXXXXXXXXXXXXXX", export.ToString());
    }

    // ---- the fixtures themselves: nothing that identifies a car or a unit may be committed ----

    public static IEnumerable<object[]> Files() { yield return new object[] { "My20.xml" }; yield return new object[] { "NotMy20.xml" }; yield return new object[] { "Unknown.xml" }; }

    [Theory, MemberData(nameof(Files))]
    public void Fixtures_AreAnonymised(string name)
    {
        var text = File.ReadAllText(Fixture(name));
        Assert.All(Regex.Matches(text, @"\b[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}\b").Select(m => m.Value),                 // MAC addresses
            mac => Assert.Matches(@"^00:00:00:00:00:[0-9a-f]{2}$", mac));
        var vin = Regex.Match(text, "<p:VIN>(.*?)</p:VIN>").Groups[1].Value;
        Assert.True(vin.Length == 0 || vin.All(c => c == 'X'), $"the VIN must be empty or all X, not '{vin}'");
        Assert.DoesNotMatch(@"\b(?![X0]{17}\b)[A-HJ-NPR-Z0-9]{17}\b", text);                                              // no other VIN-shaped text
        Assert.Matches(@"<d2p1:SyncData>U0FOSVRJWkVE</d2p1:SyncData>", text);                                               // the certificate blob is a placeholder
        Assert.Matches(@"didValue=""F141""[^>]*>\s*<d2p1:Response>00000000</d2p1:Response>", text);                       // the unit's serial number
    }

    // ---- the Utility tab ----

    [AvaloniaFact]
    public async Task OpeningTheMy20Log_FillsTheResultCard_AndSetsMy20()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        env.Dialogs.FileToPick = Fixture("My20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(u.HasLog); Assert.True(Ctx.Profile.My20v2);
        Assert.Equal(Ctx.T("Utility.ResultMy20"), u.ResVerdict);
        Assert.Equal("5U5T-14G371-BKC", u.Facts.Single(f => f.Label == Ctx.T("App.Model")).Value);
        Assert.Equal("XXXXXXXXXXXXXXXXX", u.Facts.Single(f => f.Label == "VIN").Value);
        Assert.Equal("64 GB", u.Facts.Single(f => f.Label == Ctx.T("Utility.APIMSize")).Value); Assert.Equal("13G", u.Facts.Single(f => f.Label == Ctx.T("Utility.APIMFree")).Value);
        Assert.Equal(23, u.PartitionRows.Count); Assert.Equal(13, u.PackageRows.Count); Assert.Equal(7, u.AsBuiltRows.Count);
        Assert.Equal(Ctx.T("Utility.FreeOf", "13G", "54G"), u.PartitionRows.Single(p => p.Path == "/fs/images/").Summary);
        Assert.Equal("DE00", u.AsBuiltRows[0].Id); Assert.False(u.AsBuiltRows[0].Blank); Assert.Equal(1, u.AsBuiltRows[0].Opacity);
    }

    [AvaloniaFact]
    public async Task OpeningTheOlderLog_ShowsNotMy20_WithDashesForWhatItDoesNotHave_AndReplacesTheEarlierResult()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Dialogs.FileToPick = Fixture("My20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        env.Dialogs.FileToPick = Fixture("NotMy20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.False(Ctx.Profile.My20v2);                                                    // the log read last decides: protection that the first log switched on is switched off again
        Assert.Equal(Ctx.T("Utility.ResultNotMy20"), u.ResVerdict);                          // the card is about the log in front of you...
        Assert.Equal(Ctx.T("MessageBox.My20NotFound"), env.Dialogs.Messages[^1]);            // ...and so is the popup: it must not say "MY20 found, AutoInstall enforced" for an older unit
        Assert.Single(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20Found"));        // that was said once, for the first log
        Assert.Equal("—", u.Facts.Single(f => f.Label == "VIN").Value);                    // no VIN in the log: a dash, not a blank
        Assert.Equal("3U5T-14G371-ADD", u.Facts.Single(f => f.Label == Ctx.T("App.Model")).Value);
        Assert.Equal(21, u.PartitionRows.Count); Assert.Equal(16, u.PackageRows.Count);   // the first result is gone, not added to
        Assert.All(u.AsBuiltRows, r => { Assert.True(r.Blank); Assert.True(r.Opacity < 1); });
    }

    [AvaloniaTheory]
    [InlineData(true)]      // protection that was switched on (by hand, or by an earlier log) is switched off by a log that says the unit is older
    [InlineData(false)]
    [InlineData(null)]
    public async Task OpeningTheOlderLog_SwitchesProtectionOff_WhateverItWas(bool? before)
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = before;
        env.Dialogs.FileToPick = Fixture("NotMy20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.False(Ctx.Profile.My20v2);
    }

    [AvaloniaFact]
    public async Task OpeningAnMy20Log_AfterAnOlderOne_SwitchesProtectionBackOn()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Dialogs.FileToPick = Fixture("NotMy20.xml"); await u.ParseLogCommand.ExecuteAsync(null); Assert.False(Ctx.Profile.My20v2);
        env.Dialogs.FileToPick = Fixture("My20.xml"); await u.ParseLogCommand.ExecuteAsync(null); Assert.True(Ctx.Profile.My20v2);
    }

    [AvaloniaFact]
    public async Task OpeningTheOlderLog_OnItsOwn_SetsProtectionOff()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Dialogs.FileToPick = Fixture("NotMy20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.False(Ctx.Profile.My20v2); Assert.Equal(Ctx.T("Utility.ResultNotMy20"), u.ResVerdict);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20NotFound"));
    }

    [AvaloniaTheory]
    [InlineData(false, false)]       // MY20 protection explicitly off: a log with no model switches it on (even though this unit is an older one)
    [InlineData(null, false)]        // Auto-Detect
    [InlineData(true, false)]        // already on: stays on
    [InlineData(false, true)]        // the server cannot be reached: the answer does not depend on it
    public async Task ALogWithoutAModel_IsTreatedAsMy20_NeverAsNotMy20(bool? before, bool offline)
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        Ctx.Profile.My20v2 = before; env.Api.Offline = offline;
        env.Dialogs.FileToPick = Fixture("Unknown.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(u.HasLog); Assert.True(Ctx.Profile.My20v2);
        Assert.Equal(Ctx.T("Utility.ResultModelUnknown"), u.ResVerdict);
        Assert.Equal("Unknown", u.Facts.Single(f => f.Label == Ctx.T("App.Model")).Value);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.LogUtilityNoModelFound") + "\n\n" + Ctx.T("Utility.ModelUnknownProtected"));   // says why, and what to do
        Assert.DoesNotContain(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20NotFound"));
    }

    [Fact]
    public void TheVerdictTexts_SayWhenTheSync3WasManufactured()
    {
        var en = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(Fixture("My20.xml"))!, "..", "..", "..", "..", "src", "Syn3Updater.Core", "Languages", "en-US.json")))!;
        Assert.Equal("SYNC 3 manufactured in 2020 or newer", en["Utility.ResultMy20"]);
        Assert.Equal("SYNC 3 manufactured before 2020", en["Utility.ResultNotMy20"]);
        Assert.Equal("No model in the log: treating this SYNC 3 as manufactured in 2020 or newer, to be safe", en["Utility.ResultModelUnknown"]);
        Assert.Equal("Could not check the manufacture year (the CyanLabs servers did not answer)", en["Utility.ResultMy20Unknown"]);
        Assert.Equal("Syn3 Updater was unable to reach the CyanLabs servers, so it could not check the manufacture year of this SYNC 3.\n\nCheck your internet connection.\n\nThen check status.cyanlabs.net. If CyanLabs has a known problem, try again later.\n\nIf your connection works and the status page shows everything is online, please report this on our forum (community.cyanlabs.net) or Discord.\n\nOpen the log again once the servers are reachable.", en["Utility.My20CheckFailed"]);
        Assert.Equal("Syn3 Updater was unable to reach the CyanLabs servers.\n\nCheck your internet connection.\n\nThen check status.cyanlabs.net. If CyanLabs has a known problem, try again later.\n\nIf your connection works and the status page shows everything is online, please report this on our forum (community.cyanlabs.net) or Discord and include the details below.\n\nDetails: {0}", en["Home.BackendRequired"]);
    }

    [AvaloniaTheory]
    [InlineData("My20.xml", "#ef6b62")]         // MY20: red
    [InlineData("NotMy20.xml", "#3fb97a")]      // not MY20: green
    [InlineData("Unknown.xml", "#e8a23a")]      // no model in the log: amber
    public async Task TheVerdictBanner_HasTheColourOfItsVerdict(string fixture, string colour)
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Dialogs.FileToPick = Fixture(fixture); await u.ParseLogCommand.ExecuteAsync(null);
        var want = Avalonia.Media.Color.Parse(colour);
        Assert.Equal(want, ((Avalonia.Media.SolidColorBrush)u.ResBrush).Color);
        var back = ((Avalonia.Media.SolidColorBrush)u.ResBackground).Color;                  // the same colour, faint, behind the text
        Assert.Equal((want.R, want.G, want.B), (back.R, back.G, back.B)); Assert.True(back.A < 0x80);
    }

    [AvaloniaFact]
    public async Task OpeningALog_WhenTheServerCannotBeReached_SaysTheVerdictIsUnknown()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Api.Offline = true;
        env.Dialogs.FileToPick = Fixture("My20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(u.HasLog); Assert.Null(Ctx.Profile.My20v2);                              // nothing was decided, nothing was changed
        Assert.Equal(Ctx.T("Utility.ResultMy20Unknown"), u.ResVerdict);
        Assert.DoesNotContain(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20Found") || m == Ctx.T("MessageBox.My20NotFound"));     // the popup does not claim either
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("Utility.My20CheckFailed") && m.Contains("status.cyanlabs.net"));            // it says what to check
        Assert.Contains(env.Dialogs.Shown, d => d.Buttons.SequenceEqual(new[] { Ctx.T("String.OK"), Ctx.T("Home.OpenStatusPage") }));       // with a button for the status page
        Assert.Empty(env.Dialogs.Opened);                                                                                                  // (not pressed)
    }

    [AvaloniaFact]
    public async Task OpeningALog_WhenTheServerCannotBeReached_TheStatusButtonOpensTheStatusPage()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Ctx.Profile.My20v2 = null;
        env.Api.Offline = true; env.Dialogs.Choices.Enqueue(1);                       // the user presses "Open status page"
        env.Dialogs.FileToPick = Fixture("My20.xml"); await u.ParseLogCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "https://status.cyanlabs.net" }, env.Dialogs.Opened);
    }

    [Fact]
    public void TheResultCardMarkup_BindsTheVerdict_TheFacts_AndTheFoldedLists()
    {
        // the headless platform does not build item panels, so the markup is checked: verdict, wrapped fact tiles, a bar per partition, chips per package, as-built rows
        var xaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(Fixture("My20.xml"))!, "..", "..", "..", "..", "src", "Syn3Updater.App", "Views", "UtilityView.axaml"));
        foreach (var binding in new[] { "{Binding ResVerdict}", "{Binding ResBrush}", "{Binding ResBackground}", "ItemsSource=\"{Binding Facts}\"", "ItemsSource=\"{Binding PartitionRows}\"",
                                        "Value=\"{Binding Used}\"", "{Binding Summary}", "ItemsSource=\"{Binding PackageRows}\"", "ItemsSource=\"{Binding AsBuiltRows}\"", "Opacity=\"{Binding Opacity}\"" })
            Assert.Contains(binding, xaml);
        Assert.Contains("<ctl:AdaptiveColumns", xaml); Assert.DoesNotContain("Width=\"256\"", xaml);      // the fact tiles are not a fixed width: they stretch to fill the row
        Assert.DoesNotContain("{Binding LogPartitions}", xaml); Assert.DoesNotContain("{Binding LogAsBuilt}", xaml);   // the plain text boxes are gone (the text is still what Copy AsBuilt copies)
    }

    // ---- the panel that lays the fact tiles out ----

    private static (List<Avalonia.Rect> Rects, Avalonia.Size Size) Layout(int tiles, double width, double minItem = 240, double spacing = 10, double height = 40)
    {
        var panel = new Cyanlabs.Syn3Updater.App.Controls.AdaptiveColumns { MinItemWidth = minItem, Spacing = spacing, UseLayoutRounding = false };      // (rounding to whole pixels would blur the exact widths below)
        for (var i = 0; i < tiles; i++) panel.Children.Add(new Border { Height = height });
        panel.Measure(new Avalonia.Size(width, double.PositiveInfinity)); panel.Arrange(new Avalonia.Rect(0, 0, width, panel.DesiredSize.Height));
        return (panel.Children.Select(c => c.Bounds).ToList(), panel.DesiredSize);
    }

    [AvaloniaTheory]
    [InlineData(6, 1000, 4, 242.5)]      // (1000 + 10) / 250 = 4 columns, each stretched to (1000 - 30) / 4
    [InlineData(6, 500, 2, 245)]
    [InlineData(6, 760, 3, 246.666666)]
    [InlineData(6, 240, 1, 240)]
    [InlineData(6, 200, 1, 200)]         // narrower than a tile: one column at the full width, not a cut-off tile
    [InlineData(2, 1000, 2, 495)]        // fewer tiles than columns: they still fill the whole row
    [InlineData(1, 1000, 1, 1000)]
    public void AdaptiveColumns_FillTheRow_WithAsManyColumnsAsFit(int tiles, double width, int columns, double itemWidth)
    {
        var (rects, size) = Layout(tiles, width);
        Assert.Equal(columns, rects.Select(r => r.X).Distinct().Count());
        Assert.All(rects, r => Assert.Equal(itemWidth, r.Width, 3));
        var firstRow = rects.Where(r => r.Y == 0).ToList();
        Assert.Equal(width, firstRow.Max(r => r.Right), 3);                                  // a full row ends exactly at the right edge
        Assert.Equal(width, size.Width, 3);
    }

    [AvaloniaFact]
    public void AdaptiveColumns_RowsAreAsTallAsTheirTallestTile_AndSpaced()
    {
        var panel = new Cyanlabs.Syn3Updater.App.Controls.AdaptiveColumns { MinItemWidth = 100, Spacing = 10, UseLayoutRounding = false };
        foreach (var h in new double[] { 30, 50, 20 }) panel.Children.Add(new Border { Child = new Border { Height = h } });          // tiles with content of different heights
        panel.Measure(new Avalonia.Size(330, double.PositiveInfinity)); panel.Arrange(new Avalonia.Rect(0, 0, 330, panel.DesiredSize.Height));
        Assert.All(panel.Children, c => Assert.Equal(50, c.Bounds.Height));                  // three columns: one row, as tall as the tallest
        Assert.Equal(50, panel.DesiredSize.Height);
        var (rects, size) = Layout(5, 330, minItem: 100, spacing: 10, height: 20);           // 3 + 2 tiles: two rows
        Assert.Equal(new[] { 0d, 0, 0, 30, 30 }, rects.Select(r => r.Y)); Assert.Equal(50, size.Height);
    }

    [AvaloniaFact]
    public void AdaptiveColumns_SkipsHiddenTiles_AndNoTilesTakeNoSpace()
    {
        var panel = new Cyanlabs.Syn3Updater.App.Controls.AdaptiveColumns { MinItemWidth = 100, Spacing = 10, UseLayoutRounding = false };
        panel.Measure(new Avalonia.Size(300, double.PositiveInfinity)); Assert.Equal(0, panel.DesiredSize.Height);
        panel.Children.Add(new Border { Height = 20, IsVisible = false }); panel.Children.Add(new Border { Height = 20 });
        panel.Measure(new Avalonia.Size(300, double.PositiveInfinity)); panel.Arrange(new Avalonia.Rect(0, 0, 300, 20));
        Assert.Equal(300, panel.Children[1].Bounds.Width);                                   // the one visible tile gets the whole row
    }
}
