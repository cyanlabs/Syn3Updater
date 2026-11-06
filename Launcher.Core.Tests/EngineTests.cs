using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Cyanlabs.Launcher.Core.Tests
{
    public class EngineTests
    {
        private sealed class World : IDisposable
        {
            public TempDir Dir = new TempDir();
            public FakeSource Source = new FakeSource();
            public string Install => Dir.Sub("Program Files", "Syn3Updater");
            public List<string> Log = new List<string>();
            public List<string> Messages = new List<string>();
            public int CloseCalls;
            public World() { Directory.CreateDirectory(Install); }

            /// <summary>An install made by the v2 launcher: exe, launcher, dlls, languages (no manifest).</summary>
            public void InstallV2(string tag = "2.14.2.0")
            {
                foreach (var f in new[] { "Syn3Updater.exe", "Launcher.exe", "Newtonsoft.Json.dll", "FluentWPF.dll", "Languages/en-US.json" })
                { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Install, f))); File.WriteAllText(Path.Combine(Install, f), "v2-" + f); }
            }

            public UpdateRequest Request(ReleaseChannel channel = ReleaseChannel.Stable, string installed = "2.14.2.0", ReleaseChannel? installedChannel = ReleaseChannel.Stable, int osBuild = 19045) => new UpdateRequest
            {
                InstallDir = Install, Channel = channel, InstalledTag = installed, InstalledChannel = installedChannel, OsBuild = osBuild, Arch = "x64",
                DataDirs = new[] { Dir.Sub("local") }, BackupRoot = Dir.Sub("backups"), Retry = new RetryPolicy(2, 0),
                CloseApp = () => { CloseCalls++; return Task.CompletedTask; }, Log = Log.Add
            };

            public Task<UpdateOutcome> Run(UpdateRequest r) => new UpdateEngine(Source).RunAsync(r, new Progress<UpdateProgress>(p => Messages.Add(p.Message)), CancellationToken.None);
            public string Read(string rel) => File.ReadAllText(Path.Combine(Install, rel));
            public void Dispose() => Dir.Dispose();

            public static byte[] V3Zip(string version = "3.0.0") => Zips.Make(($"Syn3Updater-{version}-win-x64/Syn3Updater.exe", "v3-exe"), ($"Syn3Updater-{version}-win-x64/Launcher.exe", "v3-launcher"), ($"Syn3Updater-{version}-win-x64/LICENSE", "gpl"));
            public static byte[] V2Zip(string v) => Zips.Make(("Syn3Updater.exe", "v" + v + "-exe"), ("Launcher.exe", "v" + v + "-launcher"), ("Languages/en-US.json", "{}"));
        }

        [Fact]
        public async Task V2_To_V3_Bridge_ReplacesEverything_IncludingTheLauncher_AndBacksUpSettings()
        {
            using var w = new World(); w.InstallV2();
            Directory.CreateDirectory(w.Dir.Sub("local")); File.WriteAllText(w.Dir.Sub("local", "settings.json"), "my-v2-settings");
            w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0"));
            w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip", sums: true);

            var outcome = await w.Run(w.Request());

            Assert.Equal(OutcomeKind.Installed, outcome.Kind); Assert.Equal("3.0.0.0", outcome.Version.NumericString);
            Assert.Equal("v3-exe", w.Read("Syn3Updater.exe")); Assert.Equal("v3-launcher", w.Read("Launcher.exe")); Assert.True(File.Exists(w.Dir.Sub("Program Files", "Syn3Updater", "LICENSE")));
            Assert.False(File.Exists(Path.Combine(w.Install, "Newtonsoft.Json.dll"))); Assert.False(Directory.Exists(Path.Combine(w.Install, "Languages")));     // v2 leftovers gone
            Assert.Equal(1, w.CloseCalls);
            Assert.NotNull(outcome.DataBackupPath); Assert.Equal("my-v2-settings", File.ReadAllText(Path.Combine(outcome.DataBackupPath, "data1", "settings.json")));
            Assert.Equal(new[] { "LICENSE", "Launcher.exe", "Syn3Updater.exe" }.OrderBy(x => x), InstallManifest.Load(w.Install).Files.OrderBy(x => x));
            Assert.False(Directory.Exists(Path.Combine(w.Install, InstallTransaction.StagingDirName))); Assert.False(Directory.Exists(Path.Combine(w.Install, InstallTransaction.BackupDirName)));
        }

        [Fact]
        public async Task ExistingV2Packaging_StillWorks_ForAnOrdinaryV2Update()
        {
            using var w = new World(); w.InstallV2("2.14.1.0");
            w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0"));
            var outcome = await w.Run(w.Request(installed: "2.14.1.0"));
            Assert.Equal(OutcomeKind.Installed, outcome.Kind); Assert.Equal("v2.14.2.0-exe", w.Read("Syn3Updater.exe")); Assert.Equal("v2.14.2.0-launcher", w.Read("Launcher.exe"));
            Assert.Null(outcome.DataBackupPath);                      // same major version: no settings backup needed
        }

        [Fact]
        public async Task StableUsers_DoNotGetAPrerelease_BetaUsersDo()
        {
            using var w = new World(); w.InstallV2();
            w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0"));
            w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip", prerelease: true);
            Assert.Equal(OutcomeKind.UpToDate, (await w.Run(w.Request())).Kind); Assert.Equal("v2-Syn3Updater.exe", w.Read("Syn3Updater.exe"));
            Assert.Equal(OutcomeKind.Installed, (await w.Run(w.Request(ReleaseChannel.Beta, installedChannel: ReleaseChannel.Beta))).Kind); Assert.Equal("v3-exe", w.Read("Syn3Updater.exe"));
        }

        [Fact]
        public async Task AnUpToDateInstall_DownloadsNothing()
        {
            using var w = new World(); w.InstallV2(); w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0"));
            var outcome = await w.Run(w.Request());
            Assert.Equal(OutcomeKind.UpToDate, outcome.Kind); Assert.Equal(0, w.Source.Downloads); Assert.Equal(0, w.CloseCalls);
        }

        [Fact]
        public async Task OldWindows_IsHeldOnTheLatestV2_WithAnExplanation()
        {
            using var w = new World(); w.InstallV2("2.14.1.0");
            w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0")); w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip");
            var outcome = await w.Run(w.Request(installed: "2.14.1.0", osBuild: 7601));       // Windows 7
            Assert.Equal(OutcomeKind.Installed, outcome.Kind); Assert.Equal("2.14.2.0", outcome.Version.NumericString); Assert.Equal("v2.14.2.0-exe", w.Read("Syn3Updater.exe"));

            var again = await w.Run(w.Request(installed: "2.14.2.0", osBuild: 7601));
            Assert.Equal(OutcomeKind.UpToDate, again.Kind); Assert.Contains("Windows 10", again.Message); Assert.Contains("3.0.0.0", again.Message);
        }

        [Fact]
        public async Task ACorruptDownload_IsRejected_AndTheInstallIsUntouched()
        {
            using var w = new World(); w.InstallV2();
            var rel = w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip", sums: true);
            w.Source.Files[rel.Assets[0].DownloadUrl] = w.Source.Files[rel.Assets[0].DownloadUrl].Concat(new byte[] { 1, 2, 3 }).ToArray();   // tampered after the checksum was made
            var outcome = await w.Run(w.Request());
            Assert.Equal(OutcomeKind.Failed, outcome.Kind); Assert.Contains("corrupt", outcome.Message);
            Assert.Equal("v2-Syn3Updater.exe", w.Read("Syn3Updater.exe")); Assert.Equal("v2-Launcher.exe", w.Read("Launcher.exe")); Assert.Equal(0, w.CloseCalls);     // never even closed the app
        }

        [Fact]
        public async Task GitHubsOwnDigest_IsUsedWhenPresent()
        {
            using var w = new World(); w.InstallV2();
            var zip = World.V3Zip(); var rel = w.Source.Publish("3.0.0.0", zip, "Syn3Updater-3.0.0.0-win-x64.zip");
            rel.Assets[0].Digest = "sha256:" + new string('0', 64);                      // wrong on purpose
            Assert.Equal(OutcomeKind.Failed, (await w.Run(w.Request())).Kind);
            rel.Assets[0].Digest = "sha256:" + FakeSource.Hash(zip);
            Assert.Equal(OutcomeKind.Installed, (await w.Run(w.Request())).Kind);
        }

        [Fact]
        public async Task AFailureWhileSwappingFiles_RestoresTheOldVersion()
        {
            using var w = new World(); w.InstallV2();
            w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip");
            var req = w.Request(); req.FileSystem = new FaultyFileSystem { FailMove = (s, d) => s.Contains(InstallTransaction.StagingDirName) && s.EndsWith("Launcher.exe") };
            var outcome = await w.Run(req);
            Assert.Equal(OutcomeKind.Failed, outcome.Kind); Assert.Contains("restored", outcome.Message);
            Assert.Equal("v2-Syn3Updater.exe", w.Read("Syn3Updater.exe")); Assert.Equal("v2-Launcher.exe", w.Read("Launcher.exe")); Assert.Equal("v2-Newtonsoft.Json.dll", w.Read("Newtonsoft.Json.dll"));
        }

        [Fact]
        public async Task Offline_StartsTheInstalledApp_ButFailsOnAFreshMachine()
        {
            using var w = new World(); w.Source.ListError = new NetworkException("down", new Exception());
            Assert.Equal(OutcomeKind.Failed, (await w.Run(w.Request(installed: null, installedChannel: null))).Kind);     // nothing installed, nothing to start
            w.InstallV2(); Assert.Equal(OutcomeKind.Skipped, (await w.Run(w.Request())).Kind);
        }

        [Fact]
        public async Task RateLimited_SkipsTheCheck()
        {
            using var w = new World(); w.InstallV2(); w.Source.ListError = new RateLimitedException();
            var outcome = await w.Run(w.Request()); Assert.Equal(OutcomeKind.Skipped, outcome.Kind); Assert.Contains("limit", outcome.Message);
        }

        [Fact]
        public async Task ADownloadThatDropsMidway_LeavesTheInstallAlone()
        {
            using var w = new World(); w.InstallV2(); w.Source.Publish("3.0.0.0", World.V3Zip(), "Syn3Updater-3.0.0.0-win-x64.zip"); w.Source.DownloadError = new NetworkException("reset", new Exception());
            Assert.Equal(OutcomeKind.Skipped, (await w.Run(w.Request())).Kind); Assert.Equal("v2-Syn3Updater.exe", w.Read("Syn3Updater.exe")); Assert.Equal(0, w.CloseCalls);
        }

        [Fact]
        public async Task AReleaseWithNoWindowsDownload_IsSkipped_NotCrashed()
        {
            using var w = new World(); w.InstallV2();
            w.Source.Publish("3.0.0.0", new byte[] { 1 }, "Syn3Updater-3.0.0.0-linux-x64.tar.gz");
            var outcome = await w.Run(w.Request()); Assert.Equal(OutcomeKind.Skipped, outcome.Kind); Assert.Contains("no download", outcome.Message);
        }

        [Fact]
        public async Task AMissingApp_IsRepaired_EvenWhenTheVersionMatches()
        {
            using var w = new World(); w.Source.Publish("2.14.2.0", World.V2Zip("2.14.2.0"));
            var outcome = await w.Run(w.Request());           // prefs say 2.14.2.0 but nothing is installed
            Assert.Equal(OutcomeKind.Installed, outcome.Kind); Assert.Equal("v2.14.2.0-exe", w.Read("Syn3Updater.exe"));
        }

        [Fact]
        public async Task SecondUpdate_UsesTheManifest_ToRemoveFilesTheNewVersionDropped()
        {
            using var w = new World(); w.InstallV2();
            w.Source.Publish("3.0.0.0", Zips.Make(("Syn3Updater.exe", "a"), ("helper.dll", "h"), ("Launcher.exe", "l")), "Syn3Updater-3.0.0.0-win-x64.zip");
            await w.Run(w.Request()); Assert.True(File.Exists(Path.Combine(w.Install, "helper.dll")));
            File.WriteAllText(Path.Combine(w.Install, "user-note.txt"), "mine");
            w.Source.Publish("3.1.0.0", Zips.Make(("Syn3Updater.exe", "b"), ("Launcher.exe", "l2")), "Syn3Updater-3.1.0.0-win-x64.zip");
            var outcome = await w.Run(w.Request(installed: "3.0.0.0"));
            Assert.Equal(OutcomeKind.Installed, outcome.Kind); Assert.False(File.Exists(Path.Combine(w.Install, "helper.dll"))); Assert.Equal("mine", w.Read("user-note.txt")); Assert.Null(outcome.DataBackupPath);
        }
    }

    public class GitHubSourceTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Respond = _ => new HttpResponseMessage(HttpStatusCode.OK);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Respond(request));
        }

        private const string Json = @"[{""tag_name"":""v3.0.0"",""prerelease"":true,""draft"":false,""assets"":[{""name"":""a-win-x64.zip"",""content_type"":""application/zip"",""browser_download_url"":""https://x/a.zip"",""size"":5,""digest"":""sha256:" + "ab" + @"""}]},{""tag_name"":""2.14.2.0"",""assets"":[]}]";

        [Fact]
        public void ParsesTheApiResponse()
        {
            var r = GitHubReleaseSource.Parse(Json);
            Assert.Equal(2, r.Count); Assert.True(r[0].Prerelease); Assert.Equal("a-win-x64.zip", r[0].Assets[0].Name); Assert.Equal("sha256:ab", r[0].Assets[0].Digest); Assert.False(r[1].Prerelease);
        }

        [Fact]
        public async Task SendsAUserAgent_AndReadsReleases()
        {
            HttpRequestMessage seen = null;
            using var src = new GitHubReleaseSource("cyanlabs", "Syn3Updater", new Handler { Respond = req => { seen = req; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) }; } });
            Assert.Equal(2, (await src.GetReleasesAsync(CancellationToken.None)).Count);
            Assert.Contains("CyanLabs-Launcher", seen.Headers.UserAgent.ToString()); Assert.Contains("/repos/cyanlabs/Syn3Updater/releases", seen.RequestUri.ToString());
        }

        [Fact]
        public async Task A429_OrAnExhaustedForbidden_IsRateLimited()
        {
            using var a = new GitHubReleaseSource("o", "r", new Handler { Respond = _ => new HttpResponseMessage((HttpStatusCode)429) });
            await Assert.ThrowsAsync<RateLimitedException>(() => a.GetReleasesAsync(CancellationToken.None));
            var forbidden = new HttpResponseMessage(HttpStatusCode.Forbidden); forbidden.Headers.Add("X-RateLimit-Remaining", "0");
            using var b = new GitHubReleaseSource("o", "r", new Handler { Respond = _ => forbidden });
            await Assert.ThrowsAsync<RateLimitedException>(() => b.GetReleasesAsync(CancellationToken.None));
        }

        [Fact]
        public async Task ServerErrors_AndConnectionFailures_AreNetworkExceptions()
        {
            using var a = new GitHubReleaseSource("o", "r", new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.BadGateway) });
            await Assert.ThrowsAsync<NetworkException>(() => a.GetReleasesAsync(CancellationToken.None));
            using var b = new GitHubReleaseSource("o", "r", new Handler { Respond = _ => throw new HttpRequestException("no route") });
            await Assert.ThrowsAsync<NetworkException>(() => b.GetReleasesAsync(CancellationToken.None));
        }

        [Fact]
        public async Task DownloadsToAFile_ReportingProgress()
        {
            using var t = new TempDir(); var seen = new List<long>();
            using var src = new GitHubReleaseSource("o", "r", new Handler { Respond = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[200_000]) } });
            await src.DownloadFileAsync("https://x/f", t.Sub("f.bin"), new SyncProgress<KeyValuePair<long, long>>(p => seen.Add(p.Key)), CancellationToken.None);
            Assert.Equal(200_000, new FileInfo(t.Sub("f.bin")).Length); Assert.Equal(200_000, seen.Last());
        }

        private sealed class SyncProgress<T> : IProgress<T> { private readonly Action<T> _a; public SyncProgress(Action<T> a) { _a = a; } public void Report(T v) => _a(v); }
    }
}
