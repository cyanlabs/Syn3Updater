using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>A drive whose flush hook lets a test change the files on the "stick" between the copy and the final read-back.</summary>
internal sealed class HookedDrive(Action<DriveInfoModel> onFlush) : IDriveService
{
    public string OsDescription => "test";
    public Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DriveInfoModel>>(new List<DriveInfoModel>());
    public Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.FromResult(drive.MountPath);
    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) { onFlush(drive); return Task.CompletedTask; }
    public Task<EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default) => Task.FromResult(new EjectResult(true, ""));
}

public class PostCopyVerificationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("syn3verify").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private (UpdateRequest req, AppLog log, string usb) Setup(Action<DriveInfoModel> onFlush, out UpdateRunner runner)
    {
        var data = RandomNumberGenerator.GetBytes(400_000);
        var dl = Path.Combine(_root, "dl"); Directory.CreateDirectory(dl);
        File.WriteAllBytes(Path.Combine(dl, "app.tar.gz"), data);          // already downloaded and valid: no network needed
        var rwData = RandomNumberGenerator.GetBytes(50_000);
        File.WriteAllBytes(Path.Combine(dl, "rw.tar.gz"), rwData);
        var item = new Ivsu { Type = "APPS", Name = "app.tar.gz", FileName = "app.tar.gz", Url = "http://127.0.0.1:1/app.tar.gz", FileSize = data.Length, Md5 = Convert.ToHexString(MD5.HashData(data)) };
        var rw = new Ivsu { Type = "TOOL", Name = "rw.tar.gz", FileName = "rw.tar.gz", Url = "http://127.0.0.1:1/rw.tar.gz", FileSize = rwData.Length, Md5 = Convert.ToHexString(MD5.HashData(rwData)) };
        var usb = Path.Combine(_root, "usb"); Directory.CreateDirectory(usb);
        var log = new AppLog();
        runner = new UpdateRunner(new HookedDrive(onFlush), new FileService(new HttpClient()), log);
        return (new UpdateRequest
        {
            Queue = new() { rw, item }, Tools = new SpecialTools { RwDataCleaner = rw }, Release = "Sync 3.4", Region = "NA", SkipFormat = true, DownloadDir = dl, AppVersion = "3.0.0",
            Drive = new DriveInfoModel { DeviceId = "/dev/test", MountPath = usb, Name = "stick" },        // a real drive (not IsFolder): the verify pass applies
        }, log, usb);
    }

    [Fact]
    public async Task GoodDrive_IsReadBackAfterTheFlushAndReportedVerified()
    {
        var (req, log, _) = Setup(_ => { }, out var runner);
        Assert.True(await runner.RunAsync(req, new Progress<RunProgress>(), default), runner.LastError);
        Assert.Contains(log.Lines, l => l.Contains("Verified: app.tar.gz"));
        Assert.Contains(log.Lines, l => l.Contains("All files on the drive verified"));
        var i = log.Lines.ToList();
        Assert.True(i.FindIndex(l => l.Contains("Verifying the files")) > i.FindIndex(l => l.Contains("Copied: app.tar.gz")));
    }

    [Fact]
    public async Task FileDamagedAfterTheFlush_FailsTheRunInsteadOfLookingFine()
    {
        var (req, log, usb) = Setup(d => File.WriteAllBytes(Path.Combine(d.MountPath, "SyncMyRide", "app.tar.gz"), new byte[400_000]), out var runner);
        Assert.False(await runner.RunAsync(req, new Progress<RunProgress>(), default));
        Assert.Contains("does not match", runner.LastError);
        Assert.Contains(log.Lines, l => l.Contains("Unable to verify app.tar.gz"));
    }

    [Fact]
    public async Task FolderTargets_SkipTheDriveVerifyPass()
    {
        var (req, log, usb) = Setup(_ => throw new Exception("not flushed for folders"), out var runner);
        var folder = new UpdateRequest
        {
            Queue = req.Queue, Tools = req.Tools, Release = req.Release, Region = req.Region, SkipFormat = true, DownloadDir = req.DownloadDir, AppVersion = "3.0.0",
            Drive = new DriveInfoModel { IsFolder = true, MountPath = usb, Name = "folder" },
        };
        Assert.True(await runner.RunAsync(folder, new Progress<RunProgress>(), default), runner.LastError);
        Assert.DoesNotContain(log.Lines, l => l.Contains("Verifying the files"));
    }
}

public class UpdateCheckerTests
{
    private static string Releases(params (string tag, bool pre, bool draft)[] r) =>
        JsonSerializer.Serialize(r.Select(x => new { tag_name = x.tag, prerelease = x.pre, draft = x.draft, html_url = "https://github.com/CyanLabs/Syn3Updater/releases/tag/" + x.tag }));

    [Theory]
    [InlineData("3.0.0", "3.0.1", true)]
    [InlineData("3.0.0", "v3.1.0", true)]
    [InlineData("3.0.0", "3.0.0", false)]
    [InlineData("3.1.0", "3.0.9", false)]
    [InlineData("3.0.0-test6", "3.0.0", true)]       // the release is newer than its own pre-release
    [InlineData("3.0.0", "3.0.0-test6", false)]
    [InlineData("3.0.0-test9", "3.0.0-test10", true)]  // numeric, not alphabetical
    public void Pick_ComparesVersions(string current, string tag, bool newer)
    {
        var pre = tag.Contains('-');
        var pick = UpdateChecker.Pick(Releases((tag, pre, false)), current);
        Assert.Equal(newer, pick != null);
    }

    [Fact]
    public void Pick_IgnoresDraftsAndHidesPrereleasesFromStableUsers()
    {
        var json = Releases(("4.0.0-rc1", true, false), ("3.5.0", false, true), ("3.2.0", false, false), ("3.1.0", false, false));
        Assert.Equal("3.2.0", UpdateChecker.Pick(json, "3.0.0")!.Version);
        Assert.Equal("4.0.0-rc1", UpdateChecker.Pick(json, "3.0.0-test1")!.Version);    // a pre-release tester is offered the newest
    }

    [Fact]
    public void Pick_ReturnsNullForGarbageAndUnknownVersions()
    {
        Assert.Null(UpdateChecker.Pick("[]", "3.0.0"));
        Assert.Null(UpdateChecker.Pick("{}", "3.0.0"));
        Assert.Null(UpdateChecker.Pick(Releases(("3.1.0", false, false)), "not-a-version"));
        Assert.Equal("https://github.com/CyanLabs/Syn3Updater/releases/tag/3.1.0", UpdateChecker.Pick(Releases(("3.1.0", false, false)), "3.0.0")!.Url);
    }

    [Theory]
    [InlineData("3.0.0", UpdateChannel.Stable)]
    [InlineData("v3.0.0", UpdateChannel.Stable)]
    [InlineData("3.0.0-test7", UpdateChannel.Beta)]
    [InlineData("3.1.0-beta.2", UpdateChannel.Beta)]
    [InlineData("3.1.0-rc.1", UpdateChannel.Beta)]
    [InlineData("3.1.0-nightly.20261108.5", UpdateChannel.Nightly)]
    [InlineData("3.1.0-Nightly.1", UpdateChannel.Nightly)]
    [InlineData("rubbish", UpdateChannel.Stable)]
    public void ABuildKnowsItsOwnChannel_FromItsVersion(string version, UpdateChannel expected) => Assert.Equal(expected, Channels.Of(version));

    [Fact]
    public void TheSavedChannel_FallsBackToTheRunningBuild_WhenMissingOrUnknown()
    {
        Assert.Equal(UpdateChannel.Beta, Channels.Parse(null, "3.0.0-test7"));
        Assert.Equal(UpdateChannel.Stable, Channels.Parse("", "3.0.0"));
        Assert.Equal(UpdateChannel.Stable, Channels.Parse("Gamma", "3.0.0"));
        Assert.Equal(UpdateChannel.Stable, Channels.Parse("42", "3.0.0"));          // a number is not a channel
        Assert.Equal(UpdateChannel.Nightly, Channels.Parse("nightly", "3.0.0"));
    }

    [Fact]
    public void AnOldSettingsFile_StillLoads_AfterTheOptOutWasRemoved()
    {
        var m = JsonSerializer.Deserialize<Cyanlabs.Syn3Updater.Core.Models.MainSettings>("{\"CheckForUpdates\": false, \"UpdateChannel\": \"Beta\"}")!;
        Assert.Equal("Beta", m.UpdateChannel);               // the unknown old key is ignored, the rest is read
    }

    [Fact]
    public void APrereleaseUnderABareNumericTag_IsTheBuildNamedInItsTitle_NotAHigherVersion()
    {
        // The v2 Launcher needs a bare tag like 3.0.0.0, so test builds are published as 3.0.0.0 flagged "pre-release" with the real name in the title.
        var json = Releases2(("3.0.0.0", "Syn3 Updater 3.0.0-test8", true));
        Assert.Null(UpdateChecker.Pick(json, "3.0.0-test8"));                                          // already running exactly that build: no banner
        Assert.Equal("3.0.0-test8", UpdateChecker.Pick(json, "3.0.0-test7")!.Version);                 // an older test build is offered test8, not "3.0.0.0"
        Assert.Null(UpdateChecker.Pick(json, "3.0.0-test9"));                                          // a newer test build is not told to go back
        // the same tag as a real (not pre-release) release IS the final 3.0.0 and beats every test build
        var final = Releases2(("3.0.0.0", "Syn3 Updater 3.0.0", false));
        Assert.Equal("3.0.0.0", UpdateChecker.Pick(final, "3.0.0-test8")!.Version);
        // a title that does not name the same version changes nothing
        Assert.Equal("3.0.0.0", UpdateChecker.Pick(Releases2(("3.0.0.0", "Big v3 release", true)), "3.0.0-test8")!.Version);
        Assert.Equal("3.0.0.0", UpdateChecker.Pick(Releases2(("3.0.0.0", "Syn3 Updater 3.1.0-test1", true)), "3.0.0-test8")!.Version);
        // the real situation: test8 published as 3.0.0.0 and test9 as v3.0.0-test9. The newest TEST build is offered (test9), never the bare 3.0.0.0 label.
        var both = Releases2(("v3.0.0-test9", "Syn3 Updater 3.0.0-test9", true), ("3.0.0.0", "Syn3 Updater 3.0.0-test8", true), ("2.14.2.1", "2.14.2.1", false));
        Assert.Equal("3.0.0-test9", UpdateChecker.Pick(both, "3.0.0-test8")!.Version);
        Assert.Null(UpdateChecker.Pick(both, "3.0.0-test9"));
        // Stable never sees a pre-release, whatever it is called
        Assert.Null(UpdateChecker.Pick(json, "3.0.0", UpdateChannel.Stable));
    }

    private static string Releases2(params (string tag, string? name, bool pre)[] r) =>
        JsonSerializer.Serialize(r.Select(x => new { tag_name = x.tag, name = x.name, prerelease = x.pre, draft = false, html_url = "https://example/" + x.tag }));

    [Fact]
    public void Stable_NeverOffersBetasOrNightlies_EvenToAStableUserWhoAsksForStable()
    {
        var json = Releases2(("3.2.0-beta.1", "Syn3 Updater 3.2.0-beta.1", true), ("3.1.0", "Syn3 Updater 3.1.0", false), ("nightly", "Syn3 Updater 3.3.0-nightly.20261108.1", true));
        Assert.Equal("3.1.0", UpdateChecker.Pick(json, "3.0.0", UpdateChannel.Stable)!.Version);
    }

    [Fact]
    public void Beta_OffersTheNewestReleaseOrPrerelease_ButNotNightlies()
    {
        var json = Releases2(("nightly", "Syn3 Updater 3.5.0-nightly.20261108.1", true), ("3.2.0-beta.1", null, true), ("3.1.0", null, false));
        Assert.Equal("3.2.0-beta.1", UpdateChecker.Pick(json, "3.0.0", UpdateChannel.Beta)!.Version);
        Assert.Equal("3.2.0-beta.1", UpdateChecker.Pick(json, "3.1.0-beta.1", UpdateChannel.Beta)!.Version);
        var stableNewest = Releases2(("3.2.0", null, false), ("3.2.0-beta.1", null, true));
        Assert.Equal("3.2.0", UpdateChecker.Pick(stableNewest, "3.2.0-beta.1", UpdateChannel.Beta)!.Version);            // the final release beats its own betas
    }

    [Fact]
    public void Nightly_ReadsItsVersionFromTheReleaseTitle_BecauseTheTagIsAlwaysJustNightly()
    {
        var json = Releases2(("nightly", "Syn3 Updater 3.1.0-nightly.20261108.5", true));
        var pick = UpdateChecker.Pick(json, "3.1.0-nightly.20261107.4", UpdateChannel.Nightly)!;
        Assert.Equal("3.1.0-nightly.20261108.5", pick.Version); Assert.False(pick.Switch); Assert.Equal("https://example/nightly", pick.Url);
        Assert.Null(UpdateChecker.Pick(json, "3.1.0-nightly.20261108.5", UpdateChannel.Nightly));                         // already on it
        Assert.Null(UpdateChecker.Pick(json, "3.1.0-nightly.20261109.1", UpdateChannel.Nightly));                         // newer than the published one
        Assert.Equal("3.1.0-nightly.20261108.10", UpdateChecker.Pick(Releases2(("nightly", "Syn3 Updater 3.1.0-nightly.20261108.10", true)), "3.1.0-nightly.20261108.9", UpdateChannel.Nightly)!.Version);   // run 10 > run 9
    }

    [Fact]
    public void ChoosingAnotherChannel_OffersItsNewestBuild_EvenWhenOlder_UntilYouAreOnIt()
    {
        var stable = Releases2(("3.0.5", null, false));
        // a beta tester picks Stable: the stable build is older than the beta, but it is what they asked for
        var back = UpdateChecker.Pick(stable, "3.1.0-beta.2", UpdateChannel.Stable)!;
        Assert.Equal("3.0.5", back.Version); Assert.True(back.Switch);
        Assert.Null(UpdateChecker.Pick(stable, "3.0.5", UpdateChannel.Stable));                                        // now on it: nothing more to offer
        // a stable user picks Nightly whose number is lower than their release
        var night = Releases2(("nightly", "Syn3 Updater 3.0.0-nightly.20261108.1", true));
        var sw = UpdateChecker.Pick(night, "3.0.0", UpdateChannel.Nightly)!;
        Assert.True(sw.Switch); Assert.Equal("3.0.0-nightly.20261108.1", sw.Version);
        Assert.Null(UpdateChecker.Pick(night, "3.0.0-nightly.20261108.1", UpdateChannel.Nightly));
        // nothing published on the chosen channel: nothing to offer
        Assert.Null(UpdateChecker.Pick(stable, "3.0.5", UpdateChannel.Nightly));
    }

    [Fact]
    public async Task Check_AsksTheDevRepoForNightlies_AndTheProductionRepoForTheRest()
    {
        var urls = new List<string>();
        var http = new HttpClient(new RecordingStub(r => { urls.Add(r.RequestUri!.ToString()); return new(HttpStatusCode.OK) { Content = new StringContent("[]") }; }));
        var checker = new UpdateChecker(http);
        await checker.CheckAsync("3.0.0", UpdateChannel.Stable); await checker.CheckAsync("3.0.0", UpdateChannel.Beta); await checker.CheckAsync("3.0.0", UpdateChannel.Nightly);
        Assert.Contains("/repos/CyanLabs/Syn3Updater/releases", urls[0]); Assert.Contains("/repos/CyanLabs/Syn3Updater/releases", urls[1]);
        Assert.Contains("/repos/CyanLabs/Syn3UpdaterDev/releases", urls[2]);        // never the production repo: the v2 Launcher reads its newest pre-release as the Beta update
    }

    private sealed class RecordingStub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));
    }

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    [Fact]
    public async Task Check_NeverThrows_WhenGitHubIsUnreachableOrRateLimited()
    {
        Assert.Null(await new UpdateChecker(new HttpClient(new Stub(() => new(HttpStatusCode.Forbidden))), "http://x/").CheckAsync("3.0.0"));
        Assert.Null(await new UpdateChecker(new HttpClient(new Stub(() => throw new HttpRequestException("offline"))), "http://x/").CheckAsync("3.0.0"));
        Assert.Null(await new UpdateChecker(new HttpClient(new Stub(() => new(HttpStatusCode.OK) { Content = new StringContent("<html>") })), "http://x/").CheckAsync("3.0.0"));
        var ok = await new UpdateChecker(new HttpClient(new Stub(() => new(HttpStatusCode.OK) { Content = new StringContent(Releases(("9.0.0", false, false))) })), "http://x/").CheckAsync("3.0.0");
        Assert.Equal("9.0.0", ok!.Version);
    }
}

public class SystemInfoAndLogTests
{
    [Fact]
    public void Redact_HidesTheHomeFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine("~", "Downloads"), SystemInfo.Redact(Path.Combine(home, "Downloads")).Replace('\\', Path.DirectorySeparatorChar).Replace("~/", "~" + Path.DirectorySeparatorChar));
        Assert.DoesNotContain(home, SystemInfo.Redact(Path.Combine(home, "x")));
        Assert.Equal("/opt/app", SystemInfo.Redact("/opt/app"));
        Assert.Equal("", SystemInfo.Redact(null));
    }

    [Fact]
    public void Collect_ReportsTheEssentialsAndWhichToolsAreMissing()
    {
        var facts = SystemInfo.Collect("3.0.0", "Stable", Path.GetTempPath(), Path.GetTempPath(), exists: t => t != SystemInfo.ToolsFor()[0]);
        var d = facts.ToDictionary(f => f.Key, f => f.Value);
        Assert.Equal("3.0.0 (Stable)", d["App version"]);
        Assert.Contains("Operating system", d.Keys); Assert.Contains(".NET runtime", d.Keys); Assert.Contains("Architecture", d.Keys);
        Assert.Contains(SystemInfo.ToolsFor()[0] + "=NO", d["Tools"]);
        foreach (var other in SystemInfo.ToolsFor().Skip(1)) Assert.Contains(other + "=yes", d["Tools"]);   // macOS has just one tool
        Assert.DoesNotContain(Environment.UserName + "/", string.Join("\n", facts.Select(f => f.Value)));
    }

    [Fact]
    public void LogReport_IncludesTheSystemSectionAndMoreDriveDetail()
    {
        var drive = new DriveInfoModel { DeviceId = "/dev/sdx", Name = "Stick", SizeBytes = 16_000_000_000, FreeBytes = 15_000_000_000, FileSystem = "exFAT", PartitionType = "MBR", VolumeName = "CYANLABS", Encrypted = false };
        var text = LogReport.Build("3.0", "Stable", "Arch", new ProfileSettings(), "autoinstall", false, drive, null, "RUNLOG",
            new[] { new KeyValuePair<string, string>("Desktop", "KDE") });
        Assert.Contains("SYSTEM\r\nDesktop: KDE", text);
        Assert.Contains("Device: /dev/sdx", text); Assert.Contains("Volume Label: CYANLABS", text); Assert.Contains("Encrypted: False", text);
        Assert.DoesNotContain("SYSTEM", LogReport.Build("3.0", "Stable", "Arch", new ProfileSettings(), "m", false, null, null, "x"));
    }
}

public class DiagnosticsServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3diag").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond());
    }

    private DiagnosticsService Make(Func<string, bool> exists, Func<HttpResponseMessage>? net = null, bool token = true, string? downloads = null, CommandRunner? run = null) =>
        new(new InertDrives(), new HttpClient(new Stub(net ?? (() => new(HttpStatusCode.OK)))), Path.Combine(_dir, "data"), () => downloads ?? Path.Combine(_dir, "dl"), () => Path.Combine(_dir, "logs"),
            token, run ?? ((f, a, s, c, t) => Task.FromResult(new ProcessResult(0, "", ""))), exists, () => false);

    [Fact]
    public async Task EverythingPresent_PassesAndDoesNotChangeAnythingBeyondTheProbeFile()
    {
        var results = await Make(_ => true).RunAsync();
        Assert.DoesNotContain(results, r => r.Status == DiagStatus.Fail && r.Id is not ("Admin"));       // Admin is a hard requirement only on Windows
        Assert.Contains(results, r => r is { Id: "Api", Status: DiagStatus.Pass });
        Assert.Contains(results, r => r is { Id: "DataFolder", Status: DiagStatus.Pass });
        Assert.Empty(Directory.GetFiles(Path.Combine(_dir, "data"), ".syn3updater-write-test-*"));        // the probe file is removed
    }

    [Fact]
    public async Task MissingTools_AreReportedByNameWithAFailForRequiredOnes()
    {
        var required = SystemInfo.ToolsFor();
        var results = await Make(_ => false).RunAsync();
        var failed = results.Where(r => r.Status == DiagStatus.Fail).Select(r => r.Id).ToList();
        if (OperatingSystem.IsLinux()) { Assert.Contains("MkfsExfat", failed); Assert.Contains("Udisksctl", failed); Assert.Contains("Pkexec", failed); Assert.Contains("Sfdisk", failed); }
        else if (OperatingSystem.IsWindows()) { Assert.Contains("Diskpart", failed); Assert.Contains("PowerShell", failed); }
        else if (OperatingSystem.IsMacOS()) Assert.Contains("Diskutil", failed);
        Assert.NotEmpty(required);
    }

    [Fact]
    public async Task NoApiToken_NetworkDown_AndClockSkew_AreEachReported()
    {
        var noToken = await Make(_ => true, token: false).RunAsync();
        Assert.Contains(noToken, r => r is { Id: "ApiToken", Status: DiagStatus.Fail });

        var down = await Make(_ => true, net: () => throw new HttpRequestException("no route")).RunAsync();
        Assert.Contains(down, r => r is { Id: "Api", Status: DiagStatus.Fail } && r.Detail.Contains("no route"));
        Assert.Contains(down, r => r is { Id: "Ford", Status: DiagStatus.Fail });

        var skewed = await Make(_ => true, net: () => { var m = new HttpResponseMessage(HttpStatusCode.OK); m.Headers.Date = DateTimeOffset.UtcNow.AddHours(-3); return m; }).RunAsync();
        Assert.Contains(skewed, r => r is { Id: "Clock", Status: DiagStatus.Warn });
    }

    [Fact]
    public async Task UnwritableDownloadFolder_FailsWithTheFolderInTheDetail()
    {
        var file = Path.Combine(_dir, "afile"); File.WriteAllText(file, "x");
        var results = await Make(_ => true, downloads: Path.Combine(file, "sub")).RunAsync();       // a folder cannot be created underneath a file
        Assert.Contains(results, r => r is { Id: "DownloadFolder", Status: DiagStatus.Fail });
    }
}

/// <summary>Keeps every language file complete, in step with English, and free of keys nothing uses.</summary>
public class LocalizationTests
{
    private static string SrcDir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Syn3Updater.Core", "Languages"))) d = d.Parent;
            return Path.Combine(d?.FullName ?? throw new DirectoryNotFoundException("repo root"), "src");
        }
    }

    private static Dictionary<string, string> Load(string code) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(SrcDir, "Syn3Updater.Core", "Languages", code + ".json")))!;

    public static IEnumerable<object[]> Codes() =>
        Directory.GetFiles(Path.Combine(SrcDir, "Syn3Updater.Core", "Languages"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Where(c => c != "en-US").Select(c => new object[] { c });

    [Theory, MemberData(nameof(Codes))]
    public void EveryLanguage_HasExactlyTheEnglishKeys(string code)
    {
        var en = Load("en-US"); var other = Load(code);
        Assert.Empty(en.Keys.Except(other.Keys));       // missing: would fall back to English in the UI
        Assert.Empty(other.Keys.Except(en.Keys));       // stale: nothing reads them
    }

    [Theory, MemberData(nameof(Codes))]
    public void EveryLanguage_KeepsThePlaceholders(string code)
    {
        var en = Load("en-US"); var other = Load(code);
        static string P(string s) => string.Join(",", Regex.Matches(s, @"\{\d+\}").Select(m => m.Value).Distinct().Order());
        var bad = en.Where(kv => other.TryGetValue(kv.Key, out var v) && P(v) != P(kv.Value)).Select(kv => kv.Key).ToList();
        Assert.True(bad.Count == 0, $"{code}: placeholders differ for {string.Join(", ", bad)}");
    }

    [Fact]
    public void EveryKeyUsedInTheCode_ExistsInEnglish_AndEveryEnglishKeyIsUsed()
    {
        var en = Load("en-US");
        var src = string.Join("\n", Directory.EnumerateFiles(SrcDir, "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
        var diag = File.ReadAllText(Path.Combine(SrcDir, "Syn3Updater.Core", "Services", "DiagnosticsService.cs"));
        var literals = Regex.Matches(src, "\"([A-Za-z]+\\.[A-Za-z0-9_.]+)\"").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(src, @"\{svc:T ([A-Za-z0-9_.]+)\}").Select(m => m.Groups[1].Value)).ToHashSet();

        var prefixes = new[] { "Main.", "App.", "Home.", "Settings.", "Utility.", "Download.", "Logs.", "String.", "MessageBox.", "Profiles.", "News.", "About.", "Diag." };
        var missing = literals.Where(k => prefixes.Any(k.StartsWith) && !en.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "used in code but not in en-US.json: " + string.Join(", ", missing));

        // Diagnostics titles/hints are looked up as "Diag.<Id>" and "Diag.<Id>.Fix" from the ids the service returns
        bool Used(string k) => literals.Contains(k) || (k.StartsWith("Diag.") && diag.Contains($"\"{k.Split('.')[1]}\""));
        var unused = en.Keys.Where(k => !Used(k)).ToList();
        Assert.True(unused.Count == 0, "in en-US.json but not used anywhere: " + string.Join(", ", unused));
    }

    [Fact]
    public void XamlHasNoHardcodedUserText_ExceptBrandNamesAndPlaceholders()
    {
        var allowed = new HashSet<string> { "Syn3 Updater", "CyanLabs", "GitHub", "VIN", "Sync_XXXXXXXX_VIN.xml" };
        var found = new List<string>();
        foreach (var f in Directory.EnumerateFiles(Path.Combine(SrcDir, "Syn3Updater.App", "Views"), "*.axaml"))
            foreach (Match m in Regex.Matches(File.ReadAllText(f), "\\b(Text|Content|Header|Watermark|ToolTip\\.Tip)=\"([^\"{][^\"]*)\""))
                if (Regex.IsMatch(m.Groups[2].Value, "[A-Za-z]{2,}") && !allowed.Contains(m.Groups[2].Value)) found.Add($"{Path.GetFileName(f)}: {m.Groups[2].Value}");
        Assert.True(found.Count == 0, string.Join("\n", found));
    }
}
