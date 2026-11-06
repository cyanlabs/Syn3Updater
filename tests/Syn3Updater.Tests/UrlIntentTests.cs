using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class UrlIntentParserTests
{
    [Theory]
    [InlineData("syn3updater://Region=EU;Release=3.4.19274;Maps=Navigation_Map_2025", "EU", "3.4.19274", "Navigation Map 2025")]       // the v2 form
    [InlineData("syn3updater://Region=NA;Release=3.4.19274;Maps=Navigation_Map_2025/", "NA", "3.4.19274", "Navigation Map 2025")]     // browsers add a trailing slash
    [InlineData("SYN3UPDATER:Region=na;release=3.3.19052", "NA", "3.3.19052", null)]                                                  // scheme, keys and region are case-insensitive
    [InlineData("syn3updater:///?Region=ANZ&Release=3.4.1", "ANZ", "3.4.1", null)]                                                    // ? and & style
    [InlineData("syn3updater://Region=EU;Maps=Map%20Name%202025", "EU", null, "Map Name 2025")]                                       // percent-encoding
    [InlineData("\"syn3updater://Region=CN\"", "CN", null, null)]                                                                     // Windows can hand the argument over quoted
    [InlineData("syn3updater://Region=EU;Bogus=1;Release=3.4.1;Another", "EU", "3.4.1", null)]                                       // unknown and malformed parts are skipped
    public void Parse_AcceptsEveryShapeTheWebsiteAndV2Produced(string raw, string? region, string? release, string? maps)
    {
        var i = UrlIntentParser.Parse(raw);
        Assert.NotNull(i); Assert.Equal(region, i!.Region); Assert.Equal(release, i.Release); Assert.Equal(maps, i.Maps);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    [InlineData("https://cyanlabs.net/?Region=EU")]                        // some other scheme
    [InlineData("syn3updater://")] [InlineData("syn3updater://nothing-useful")] [InlineData("syn3updater://Bogus=1")]
    [InlineData("syn3updater://Region=")]                                  // empty value
    [InlineData("syn3updater://Region=Europe-Not-A-Code")]                 // not a region code
    [InlineData("syn3updater://Region=E%00U")]                             // control character
    [InlineData("syn3updater://Region=%ZZ")]                               // broken escape
    public void Parse_RejectsEverythingThatIsNotUsable(string? raw) => Assert.Null(UrlIntentParser.Parse(raw));

    [Fact]
    public void Parse_DropsValuesThatCouldBeAbused_ButKeepsTheRest()
    {
        var evil = "syn3updater://Region=EU;Release=" + Uri.EscapeDataString("3.4.1; rm -rf /") + ";Maps=" + new string('A', 500);
        var i = UrlIntentParser.Parse(evil);
        Assert.NotNull(i); Assert.Equal("EU", i!.Region); Assert.Null(i.Release); Assert.Null(i.Maps);     // shape check on Release, length cap on Maps
        Assert.Null(UrlIntentParser.Parse("syn3updater://Region=EU;" + new string('x', 3000)));               // whole link too long
        Assert.Null(UrlIntentParser.Parse("syn3updater://Release=" + "..\\..\\x"));                          // path-like values are not release names
    }

    [Fact]
    public void Find_PicksTheLinkOutOfTheArguments() =>
        Assert.Equal("syn3updater://Region=EU", UrlIntentParser.Find(new[] { "/launcher", "--x", "syn3updater://Region=EU", "other" }));

    [Fact]
    public void Find_ReturnsNull_WithoutALink() => Assert.Null(UrlIntentParser.Find(new[] { "--debug", "https://x" }));

    [Fact]
    public void ToString_RoundTripsThroughTheParser()
    {
        var i = new UrlIntent("EU", "3.4.19274", "Map 2025");
        Assert.Equal(new UrlIntent("EU", "3.4.19274", "Map 2025"), UrlIntentParser.Parse("syn3updater://" + i.ToString().Replace(' ', '_')));
    }
}

public class SingleInstanceTests
{
    private static string Name() => "syn3test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void AnotherProcessHoldingTheLock_MeansNotPrimary_EvenWithoutAPipeInThisProcess()
    {
        // Regression: on Linux a pipe name alone does not stop a second process becoming "primary" (it re-binds the socket file),
        // so ownership is a file lock. Holding the lock the way another process would must be enough to be refused.
        var name = Name(); var path = Path.Combine(Path.GetTempPath(), name + ".lock");
        using (new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Null(SingleInstance.TryBecomePrimary(name));
        using var after = SingleInstance.TryBecomePrimary(name);                   // released again: the next start can take over
        Assert.NotNull(after);
    }

    [Fact]
    public async Task ASecondLaunch_HandsItsLinkToTheFirst_AndKnowsItIsNotPrimary()
    {
        var name = Name();
        using var first = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(first);
        Assert.Null(SingleInstance.TryBecomePrimary(name));                       // the name is taken

        var got = new TaskCompletionSource<string>();
        first!.Listen(m => got.TrySetResult(m));
        Assert.True(await SingleInstance.TrySendAsync(name, "syn3updater://Region=EU;Release=3.4.1"));
        Assert.Equal("syn3updater://Region=EU;Release=3.4.1", await got.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task SeveralMessagesInARow_AreAllDelivered()
    {
        // Links can arrive back to back (several clicks, or a browser retrying). Each one must get through: on Windows the server end has to be
        // disconnected after every message or the next sender waits out its timeout, so a dozen in a row is what shows it up reliably.
        var name = Name(); using var first = SingleInstance.TryBecomePrimary(name)!;
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>(); first.Listen(received.Enqueue);
        var sent = Enumerable.Range(0, 12).Select(i => i == 0 ? "activate" : "syn3updater://Region=" + (i % 2 == 0 ? "EU" : "NA") + ";Release=3.4." + i).ToArray();
        foreach (var m in sent) Assert.True(await SingleInstance.TrySendAsync(name, m, timeoutMs: 3000), "could not deliver: " + m);
        await AppEnv.Until(() => received.Count == sent.Length, 10000);
        Assert.Equal(sent, received.ToArray());
    }

    [Fact]
    public async Task SendingToNobody_FailsQuickly_SoTheAppStartsNormally() =>
        Assert.False(await SingleInstance.TrySendAsync(Name(), "x", timeoutMs: 300));

    [Fact]
    public void TheNameFollowsTheDataFolder_SoDevAndTestCopiesDoNotCollideWithARealSession()
    {
        Assert.Equal(SingleInstance.NameFor("/a"), SingleInstance.NameFor("/a"));
        Assert.NotEqual(SingleInstance.NameFor("/a"), SingleInstance.NameFor("/b"));
    }

    [Fact]
    public async Task AfterTheFirstExits_ANewLaunchBecomesPrimary()
    {
        var name = Name();
        var first = SingleInstance.TryBecomePrimary(name)!; first.Listen(_ => { }); first.Dispose();
        await Task.Delay(100);
        using var next = SingleInstance.TryBecomePrimary(name);
        Assert.NotNull(next);
    }
}

public class UrlSchemeRegistrationTests
{
    [Fact]
    public void TheWindowsEntries_RunTheExeWithTheLinkAsItsArgument()
    {
        var e = UrlSchemeRegistration.WindowsEntries(@"C:\Apps\Syn3 Updater\Syn3Updater.exe");
        Assert.Contains(e, x => x.SubKey == @"Software\Classes\syn3updater" && x.Name == "URL Protocol" && x.Value == "");
        Assert.Contains(e, x => x.SubKey == @"Software\Classes\syn3updater\shell\open\command" && x.Name == null && x.Value == "\"C:\\Apps\\Syn3 Updater\\Syn3Updater.exe\" \"%1\"");
        Assert.All(e, x => Assert.StartsWith(@"Software\Classes\syn3updater", x.SubKey));         // never anywhere else in the registry
    }

    [Theory]
    [InlineData(@"C:\x\Syn3Updater.exe", true)] [InlineData(@"c:\x\syn3updater.EXE", true)]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe", false)]                                  // `dotnet Syn3Updater.dll` in development
    [InlineData("/opt/syn3updater/Syn3Updater", false)] [InlineData("", false)] [InlineData(null, false)]
    public void OnlyTheRealProgramIsRegistered(string? path, bool expected) => Assert.Equal(expected, UrlSchemeRegistration.IsRegistrable(path));
}

public class UrlIntentHomeTests
{
    [AvaloniaFact]
    public async Task ALink_PreselectsRegionReleaseAndMaps_AndNeverStartsAnything()
    {
        using var env = new AppEnv(nav: true);
        await UrlIntents.HandleAsync("syn3updater://Region=EU;Release=3.4.26057;Maps=Map_2025");
        var home = env.Main.Home;
        await AppEnv.Until(() => home.SelectedMap != null && home.Packages.Count > 0);
        Assert.Equal("EU", home.SelectedRegion!.Code);
        Assert.Equal("Sync 3.4.26057", home.SelectedRelease!.Display);
        Assert.Equal("Map 2025", home.SelectedMap!.Release!.Name);
        Assert.Same(env.Main.Home, env.Main.Current);                      // the app took you to Home
        Assert.False(home.StartEnabled);                                   // no drive chosen: a link can never be the thing that starts a run
        Assert.False(Workflow.Busy);
        Assert.False(Directory.Exists(env.DownloadDir) && Directory.EnumerateFileSystemEntries(env.DownloadDir).Any());
        Assert.Null(UrlIntents.Pending);
    }

    [AvaloniaFact]
    public async Task OptionsTheAppDoesNotOffer_AreSkipped_WithoutBreakingTheOnesThatMatch()
    {
        using var env = new AppEnv(nav: true); var home = env.Main.Home;
        await UrlIntents.HandleAsync("syn3updater://Region=EU;Release=9.9.99999;Maps=Whatever");
        await AppEnv.Until(() => home.Releases.Count > 0);
        Assert.Equal("EU", home.SelectedRegion!.Code); Assert.Null(home.SelectedRelease); Assert.Null(home.SelectedMap);

        await UrlIntents.HandleAsync("syn3updater://Region=XX;Release=3.4.26057");           // not a region the app has
        Assert.Equal("EU", home.SelectedRegion!.Code); Assert.Null(home.SelectedRelease);

        await UrlIntents.HandleAsync("syn3updater://Region=NA;Release=3.4.26057;Maps=Does_Not_Exist");
        await AppEnv.Until(() => home.MapVersions.Count > 0);
        Assert.Equal("NA", home.SelectedRegion!.Code); Assert.NotNull(home.SelectedRelease); Assert.Null(home.SelectedMap);
    }

    [AvaloniaFact]
    public async Task MapsBlockedForMy20_CannotBePreselected()
    {
        using var env = new AppEnv(nav: true); var home = env.Main.Home; Ctx.Profile.My20v2 = true;
        await UrlIntents.HandleAsync("syn3updater://Region=NA;Release=3.4.26057;Maps=Map_ESN");
        await AppEnv.Until(() => home.MapVersions.Count > 0);
        Assert.NotNull(home.SelectedRelease); Assert.Null(home.SelectedMap);                  // ESN-locked maps stay unselectable on a MY20 unit
    }

    [AvaloniaFact]
    public async Task WithoutARegion_LikeV2_NothingIsPreselected()
    {
        using var env = new AppEnv(nav: true); var home = env.Main.Home; env.Main.ShowHome(); home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.ClearSelections(includeRegion: true);
        await UrlIntents.HandleAsync("syn3updater://Release=3.4.26057;Maps=Map_2025");
        Assert.Null(home.SelectedRegion); Assert.Null(home.SelectedRelease);
    }

    [AvaloniaFact]
    public async Task ALinkThatArrivesBeforeTheCarIsConfigured_WaitsAndIsAppliedOnceHomeIsAvailable()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        Ctx.Profile.CurrentRegion = null; Ctx.Profile.CurrentVersion = 0; env.Main.ShowSettings();
        await UrlIntents.HandleAsync("syn3updater://Region=EU;Release=3.4.26057");
        Assert.NotNull(UrlIntents.Pending); Assert.Null(home.SelectedRegion);                  // Settings is shown instead, the link is kept

        Ctx.Profile.CurrentRegion = "NA"; Ctx.Profile.CurrentVersion = 3420282;
        home.OnShown();
        await AppEnv.Until(() => home.SelectedRelease != null);
        Assert.Equal("EU", home.SelectedRegion!.Code); Assert.Null(UrlIntents.Pending);
    }

    [AvaloniaFact]
    public async Task WhileARunIsInProgress_ALinkIsRefusedWithTheExistingMessage()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        Workflow.Busy = true;
        try
        {
            await UrlIntents.HandleAsync("syn3updater://Region=EU");
            Assert.Null(home.SelectedRegion); Assert.Null(UrlIntents.Pending);
            Assert.Contains(env.Dialogs.Messages, m => m.Contains("Another operation is already running"));
        }
        finally { Workflow.Busy = false; }
    }
}
