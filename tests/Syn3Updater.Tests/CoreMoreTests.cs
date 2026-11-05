using System.Net;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class MarkdownTests
{
    [Fact] public void Empty_GivesNoBlocks() { Assert.Empty(Markdown.Parse(null)); Assert.Empty(Markdown.Parse("  \n ")); }

    [Fact]
    public void Inline_BoldItalicCode()
    {
        var r = Markdown.Inline("a **b** *c* `d` e");
        Assert.Contains(r, x => x is MdText { Text: "b", Bold: true });
        Assert.Contains(r, x => x is MdText { Text: "c", Italic: true });
        Assert.Contains(r, x => x is MdText { Text: "d", Code: true });
    }

    [Fact]
    public void Inline_LinkAndBareUrl_TrimTrailingPunctuation()
    {
        var r = Markdown.Inline("see [the forum](https://x.test/a) or https://y.test/b.");
        Assert.Contains(r, x => x is MdLink { Text: "the forum", Url: "https://x.test/a" });
        Assert.Contains(r, x => x is MdLink { Url: "https://y.test/b" });
        Assert.Contains(r, x => x is MdText { Text: "." });
    }

    [Theory]
    [InlineData("[x](javascript:alert(1))")]
    [InlineData("[x](file:///etc/passwd)")]
    [InlineData("[x](steam://run/1)")]
    public void UnsafeSchemes_AreNeverLinks(string md) => Assert.DoesNotContain(Markdown.Inline(md), x => x is MdLink);

    [Fact] public void SafeUrl_Rules() { Assert.True(Markdown.IsSafeUrl("https://a.b")); Assert.True(Markdown.IsSafeUrl("mailto:a@b.c")); Assert.False(Markdown.IsSafeUrl("ftp://a")); Assert.False(Markdown.IsSafeUrl("not a url")); }

    [Fact] public void SnakeCase_IsNotItalic() => Assert.DoesNotContain(Markdown.Inline("my_var_name"), x => x is MdText { Italic: true });
    [Fact] public void Escapes_Work() => Assert.Equal("*not bold*", string.Concat(Markdown.Inline(@"\*not bold\*").OfType<MdText>().Select(t => t.Text)));
    [Fact] public void BoldWithNestedItalic() => Assert.Contains(Markdown.Inline("**very *nested* text**"), x => x is MdText { Bold: true });

    [Fact]
    public void Blocks_AreRecognised()
    {
        var b = Markdown.Parse("# Title\n- one\n* two\n1. three\n> quoted\n---\nplain\n```\ncode line\n```\n");
        Assert.Equal(new[] { MdBlockKind.Heading, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Quote, MdBlockKind.Rule, MdBlockKind.Paragraph, MdBlockKind.Code }, b.Select(x => x.Kind));
        Assert.Equal(1, b[0].Level);
        Assert.Equal("code line", ((MdText)b[^1].Inlines[0]).Text);
    }

    [Fact]
    public void BlankLines_AreKeptAsSpacing_ButNotAtTheStart()
    {
        var b = Markdown.Parse("\n\nfirst\nsame para\n\nsecond\n- item\n\n- later");
        Assert.Equal(new[] { false, false, true, false, true }, b.Select(x => x.AfterBlank));
    }

    [Fact] public void EachLine_IsItsOwnBlock() => Assert.Equal(3, Markdown.Parse("Added: a\nFixed: b\r\nNotice: c").Count);
    [Fact] public void UnclosedFence_StillEmitsCode() => Assert.Equal(MdBlockKind.Code, Markdown.Parse("```\nabc")[0].Kind);
}

public class LogParserTests
{
    [Theory]
    [InlineData("[t] ERROR: boom", LogLevel.Error)]
    [InlineData("unable to validate x after 3 tries, ABORTING PROCESS!", LogLevel.Error)]
    [InlineData("Formatting failed: x", LogLevel.Error)]
    [InlineData("[t] WARN: slow", LogLevel.Warning)]
    [InlineData("Downloading (Attempt #2): x", LogLevel.Warning)]
    [InlineData("Process cancelled by user", LogLevel.Warning)]
    [InlineData("ALL FILES DOWNLOADED AND COPIED TO THE USB DRIVE SUCCESSFULLY!", LogLevel.Success)]
    [InlineData("Validated: x (Skipping Download)", LogLevel.Success)]
    [InlineData("[t] DEBUG: hello", LogLevel.Debug)]
    [InlineData("just text", LogLevel.Info)]
    [InlineData("", LogLevel.Info)]
    public void Classify(string line, LogLevel expected) => Assert.Equal(expected, LogParser.Classify(line));

    [Fact]
    public void Describe_FileNames()
    {
        Assert.Equal(new LogFileInfo(LogKind.UpdateRun, "Update run - 5 Nov, 18:20"), LogParser.Describe("log-2026-11-05-18-20-11.txt"));
        Assert.Equal(LogKind.Interrogator, LogParser.Describe("Sync_1_VIN.xml").Kind);
        Assert.Equal(new LogFileInfo(LogKind.Other, "notes.txt"), LogParser.Describe("notes.txt"));
    }

    [Fact] public void FileSize_Formats() { Assert.Equal("12 B", LogParser.FileSize(12)); Assert.Equal("1.5 KB", LogParser.FileSize(1536)); Assert.Equal("2 MB", LogParser.FileSize(2 * 1024 * 1024)); }
}

public class SettingsAndPathsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3set").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void Defaults_ThenSaveAndReload()
    {
        var paths = new AppPaths(_dir); var store = new SettingsStore(paths);
        store.Load();
        Assert.Equal("default", store.Main.Profile);
        Assert.EndsWith("Syn3Updater", store.Main.DownloadPath);
        Assert.True(Directory.Exists(store.Main.LogPath));
        Assert.Equal(8, store.Profile.DownloadConnections);

        store.Main.Lang = "de"; store.Profile.CurrentRegion = "EU"; store.Profile.CurrentVersion = 3420282; store.Save();
        var again = new SettingsStore(paths); again.Load();
        Assert.Equal("de", again.Main.Lang); Assert.Equal("EU", again.Profile.CurrentRegion); Assert.Equal("3.4.20282", again.Profile.VersionString);
    }

    [Fact]
    public void CorruptFiles_AreReplacedWithDefaults()
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.MainConfigFile, "{ not json");
        File.WriteAllText(paths.ProfileFile("default"), "###");
        var store = new SettingsStore(paths); store.Load();
        Assert.Equal("default", store.Main.Profile);
        Assert.Equal("autodetect", store.Profile.InstallMode);
    }

    [Fact]
    public void Profiles_SwitchListDeleteReset()
    {
        var store = new SettingsStore(new AppPaths(_dir)); store.Load();
        store.Profile.CurrentRegion = "NA"; store.Save();
        store.SwitchProfile("Wife's car/2");
        Assert.Equal("Wife's car_2", store.Main.Profile);
        Assert.Null(store.Profile.CurrentRegion);
        Assert.Equal(new[] { "Wife's car_2", "default" }.Order(), store.ListProfiles());
        store.DeleteProfile("default");
        Assert.Single(store.ListProfiles());
        store.Reset();
        Assert.Equal("default", store.Main.Profile);
    }

    [Fact]
    public void FileSafe_And_DownloadDir()
    {
        Assert.Equal("a_b", AppPaths.FileSafe("a/b")); Assert.Equal("default", AppPaths.FileSafe("  "));
        Assert.EndsWith("Syn3Updater", AppPaths.DefaultDownloadDir());
        Assert.Equal("0.0.00000", ProfileSettings.FormatVersion(null)); Assert.Equal("0.0.00000", new ProfileSettings { CurrentVersion = 123 }.VersionString);
    }

    [Fact]
    public void Log_CollectsAndSaves()
    {
        var log = new AppLog(); string? last = null; log.LineAdded += l => last = l;
        log.Info("a"); log.Debug("b"); log.Error("c");
        Assert.Equal(3, log.Lines.Count); Assert.Contains("ERROR: c", last); Assert.Contains("INFO: a", log.Text);
        var f = Path.Combine(_dir, "l.txt"); log.Save(f); Assert.Contains("DEBUG: b", File.ReadAllText(f));
    }
}

public class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(string Url, string Body)> Calls { get; } = new();
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        Calls.Add((r.RequestUri!.ToString(), r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct)));
        return respond(r);
    }
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class ApiClientTests
{
    private static (ApiClient, FakeHandler) Make(string json, HttpStatusCode code = HttpStatusCode.OK, string license = "")
    {
        var h = new FakeHandler(_ => FakeHandler.Json(json, code));
        return (new ApiClient(new HttpClient(h), () => "tok", () => license), h);
    }

    [Fact]
    public async Task Notices_UserAgents_My20_Version_Special()
    {
        var (api, _) = Make("""{"data":{"notices":[{"id":"1","title":"T","notice":"N","important":true}],"useragents":[{"useragent":"UA [PLACEHOLDER]","min":"1","max":"3"}],"my20models":[{"model":"JB5T"}],"ivsu":[{"id":"5","name":"X","type":"TOOL","url":"https://h/p/file.tar.gz?dl=1","md5":"AB","filesize":"3274","version":"3.4.1"}]}}""");
        Assert.Equal("T", (await api.GetNotices())[0].Title);
        Assert.Contains("UA ", (await api.GetUserAgents())[0].Value);
        Assert.Equal("JB5T", (await api.GetMy20Models())[0].Model);
        Assert.Equal("3.4.1", await api.LookupVersion("X"));
        var sp = await api.GetSpecialPackage("reformat");
        Assert.Equal("file.tar.gz", sp.FileName); Assert.Equal(3274, sp.FileSize); Assert.Equal("TOOL", sp.Type);
    }

    [Fact]
    public async Task RandomUserAgent_SubstitutesPlaceholder_AndFallsBack()
    {
        var (api, _) = Make("""{"data":{"useragents":[{"useragent":"Agent/[PLACEHOLDER]","min":"10","max":"20"}]}}""");
        var ua = await api.RandomUserAgent(); Assert.StartsWith("Agent/", ua); Assert.DoesNotContain("PLACEHOLDER", ua);
        var (bad, _) = Make("{}", HttpStatusCode.InternalServerError);
        Assert.Contains("Mozilla", await bad.RandomUserAgent());
    }

    [Fact]
    public async Task Errors_AreFriendly()
    {
        var (unauth, _) = Make("{}", HttpStatusCode.Unauthorized);
        Assert.Contains("token", (await Assert.ThrowsAsync<ApiException>(() => unauth.GetNotices())).Message);
        var (gql, _) = Make("""{"errors":[{"message":"nope"}]}""");
        Assert.Contains("nope", (await Assert.ThrowsAsync<ApiException>(() => gql.GetNotices())).Message);
        var (garbage, _) = Make("<html>");
        await Assert.ThrowsAsync<ApiException>(() => garbage.GetNotices());
        var empty = new ApiClient(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("offline"))), () => null, () => "");
        Assert.Contains("offline", (await Assert.ThrowsAsync<ApiException>(() => empty.GetNotices())).Message);
        var (none, _) = Make("""{"data":{"ivsu":[]}}""");
        await Assert.ThrowsAsync<ApiException>(() => none.GetSpecialPackage("x"));
    }

    [Fact]
    public async Task MapReleases_IncludeLicenseKey_OnlyWhenLongEnough()
    {
        var (api, h) = Make("""{"data":{"map_releases":[]}}""", license: "ABCDEFGHIJKLMNOP");
        await api.GetMapReleases("EU", "3.4");
        Assert.Contains("ABCDEFGHIJKLMNOP", h.Calls[0].Body);
        var (short1, h2) = Make("""{"data":{"map_releases":[]}}""", license: "short");
        await short1.GetMapReleases("EU", "3.4");
        Assert.DoesNotContain("short", h2.Calls[0].Body);
    }

    [Fact]
    public async Task Changelog_Upload_AsBuilt()
    {
        var h = new FakeHandler(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.Contains("githubchangelog") => FakeHandler.Json("""{"data":[{"version":"2.14.2.0","date":"30-08-2025","changelog":"Fixed"}]}"""),
            var p when p.Contains("logs") => FakeHandler.Json("""{"uuid":"abc","status":"ok"}"""),
            _ => FakeHandler.Json("""{"filename":"f.xml","status":"ok"}"""),
        });
        var api = new ApiClient(new HttpClient(h), () => "tok", () => "");
        Assert.Equal("2.14.2.0", (await api.GetChangelog())[0].Version);
        Assert.EndsWith("uuid=abc", await api.UploadLog("pc", "contents"));
        Assert.EndsWith("filename=f.xml", await api.UploadAsBuilt("<x/>", "JB5T", true, 32, "VIN"));
        Assert.Contains("computername=pc", h.Calls.First(c => c.Url.Contains("logs")).Body);
    }

    [Fact]
    public void Token_ComesFromEnvironmentOverride()
    {
        var old = Environment.GetEnvironmentVariable("SYN3_API_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("SYN3_API_TOKEN", "from-env");
            Assert.Equal("from-env", ApiClient.ResolveToken()); Assert.True(ApiClient.HasToken);
            Environment.SetEnvironmentVariable("SYN3_API_TOKEN", null);
            Assert.Equal(ApiClient.ResolveToken() != null, ApiClient.HasToken);   // falls back to the compiled-in token
        }
        finally { Environment.SetEnvironmentVariable("SYN3_API_TOKEN", old); }
    }
}

public class FileServiceMoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3fs").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public async Task ExtractMultiPackage_UnpacksMapAndLicense_AndIgnoresOtherSources()
    {
        var bundle = Path.Combine(_dir, "maps.tar.gz");
        await using (var fs = File.Create(bundle))
        await using (var gz = new GZipOutputStream(fs))
        await using (var tar = new TarOutputStream(gz, Encoding.ASCII))
        {
            foreach (var (name, data) in new[] { ("sub/4U5T-14G421-CAC.tar.gz", "map"), ("sub/4U5T-14G424-AA.tar.gz", "lic") })
            {
                var e = TarEntry.CreateTarEntry(name); e.Size = data.Length; tar.PutNextEntry(e);
                await tar.WriteAsync(Encoding.ASCII.GetBytes(data)); tar.CloseEntry();
            }
        }
        var svc = new FileService(new HttpClient());
        var items = await svc.ExtractMultiPackageAsync(new Ivsu { Source = "naviextras", FileName = "maps.tar.gz" }, _dir, default);
        Assert.Equal(new[] { "MAP", "MAP_LICENSE" }.Order(), items.Select(i => i.Type).Order());
        Assert.All(items, i => Assert.True(File.Exists(Path.Combine(_dir, i.FileName)) && i.Md5!.Length == 32));
        Assert.Empty(await svc.ExtractMultiPackageAsync(new Ivsu { Source = "other", FileName = "x" }, _dir, default));
    }

    [Fact]
    public async Task Validate_ByRemoteSize_AndCancelled()
    {
        var f = Path.Combine(_dir, "a.bin"); await File.WriteAllBytesAsync(f, new byte[10]);
        var h = new FakeHandler(_ => { var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) }; return r; });
        var svc = new FileService(new HttpClient(h));
        Assert.True((await svc.ValidateAsync("http://x/a", f, null, false, null, default)).Ok);                // equal size => ok
        Assert.False((await svc.ValidateAsync("http://x/a", Path.Combine(_dir, "missing"), "x", false, null, default)).Ok);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => svc.ValidateAsync("", f, "x", false, null, cts.Token));
        Assert.False(await FileService.CopyAsync(f, Path.Combine(_dir, "c.bin"), new Progress<int>(), cts.Token));
        Assert.False(File.Exists(Path.Combine(_dir, "c.bin")));
    }

    [Fact] public void ValidFileName() => Assert.DoesNotContain('/', FileService.MakeValidFileName("a/b"));

    [Fact]
    public async Task Download_ReportsHttpErrors()
    {
        var svc = new FileService(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.DownloadAsync("http://x/y", Path.Combine(_dir, "y"), 1, new Progress<int>(), default));
        Assert.Empty(Directory.GetFiles(_dir, "*.part"));
    }
}

public class DriveModelAndServiceTests
{
    [Fact]
    public void DriveInfo_Text_And_SkipFormat()
    {
        var d = new DriveInfoModel { Name = "SanDisk", VolumeName = "CYANLABS", SizeBytes = 32L << 30, FreeBytes = 30L << 30, FileSystem = "exFAT", PartitionType = "MBR", MountPath = "/m" };
        Assert.Contains("SanDisk (CYANLABS)", d.Title); Assert.Contains("File System: exFAT (MBR)", d.Detail); Assert.Contains("32GB", d.Display); Assert.True(d.CanSkipFormat);
        d.VolumeName = "OTHER"; Assert.False(d.CanSkipFormat);
        var folder = new DriveInfoModel { IsFolder = true, Name = "Folder: /x" };
        Assert.Equal("Folder: /x", folder.Title); Assert.Equal("", folder.Detail); Assert.Equal("Folder: /x", folder.Display);
    }

    [Fact]
    public async Task ProcessRunner_RunsAndReportsMissingPrograms()
    {
        if (!OperatingSystem.IsLinux()) return;
        var ok = await ProcessRunner.RunAsync("sh", new[] { "-c", "echo hi; echo err >&2; exit 3" });
        Assert.Equal(3, ok.ExitCode); Assert.Equal("hi", ok.StdOut.Trim()); Assert.Equal("err", ok.StdErr.Trim()); Assert.False(ok.Ok);
        var cat = await ProcessRunner.RunAsync("cat", Array.Empty<string>(), stdin: "piped");
        Assert.Equal("piped", cat.StdOut);
        Assert.Equal(127, (await ProcessRunner.RunAsync("definitely-not-a-program", Array.Empty<string>())).ExitCode);
        Assert.True(ProcessRunner.Exists("sh")); Assert.False(ProcessRunner.Exists("definitely-not-a-program"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync("sleep", new[] { "5" }, timeoutMs: 200));
    }

    [Fact] public void Factory_PicksPlatformService() => Assert.IsType<LinuxDriveService>(OperatingSystem.IsLinux() ? DriveServiceFactory.Create() : new LinuxDriveService());

    [Fact]
    public async Task LinuxFormat_RefusesDevicesThatAreNotRemovable()
    {
        if (!OperatingSystem.IsLinux() || !ProcessRunner.Exists("lsblk")) return;
        var ex = await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => new LinuxDriveService().FormatAsync(new DriveInfoModel { DeviceId = "/dev/definitely-not-there" }, new Progress<string>()));
        Assert.Contains("refusing", ex.Message);
    }

    [Fact]
    public async Task EnsureMounted_ReturnsExistingMountPath()
    {
        Assert.Equal("/m", await new LinuxDriveService().EnsureMountedAsync(new DriveInfoModel { MountPath = "/m" }));
        Assert.Equal(@"E:\", await new WindowsDriveService().EnsureMountedAsync(new DriveInfoModel { MountPath = @"E:\" }));
        await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => new WindowsDriveService().EnsureMountedAsync(new DriveInfoModel()));
        await new LinuxDriveService().FlushAsync(new DriveInfoModel());
    }

    [Fact]
    public async Task FormatScript_IsValidShell_AndHasTheSafeguards()
    {
        Assert.Contains("--wipe-partitions always", LinuxDriveService.FormatScript);      // stale ntfs signature
        Assert.Contains("umount", LinuxDriveService.FormatScript);                         // automounted partition
        Assert.Contains("for i in 1 2 3 4 5 6", LinuxDriveService.FormatScript);           // retry while the automounter lets go
        Assert.Contains("count=16", LinuxDriveService.FormatScript);                       // old boot sectors zeroed
        if (!OperatingSystem.IsLinux()) return;
        var f = Path.GetTempFileName();
        try
        {
            File.WriteAllText(f, LinuxDriveService.FormatScript);
            var r = await ProcessRunner.RunAsync("sh", new[] { "-n", f });
            Assert.True(r.Ok, r.StdErr);
        }
        finally { File.Delete(f); }
    }

    [Fact]
    public void Linux_ParsesEncryptedAndPlainDisks()
    {
        const string json = """{"blockdevices":[{"name":"sdc","path":"/dev/sdc","type":"disk","size":8000000000,"tran":"usb","hotplug":true,"rm":true,"model":"X","pttype":"gpt","children":[{"name":"sdc1","path":"/dev/sdc1","type":"part","size":1,"fstype":"crypto_LUKS"}]},{"name":"sdd","path":"/dev/sdd","type":"disk","size":8000000000,"tran":"usb","hotplug":true,"rm":true,"model":"","fstype":"vfat","label":"STICK"}]}""";
        var l = LinuxDriveService.ParseLsblk(json);
        Assert.True(l[0].Encrypted); Assert.Equal("GPT", l[0].PartitionType);
        Assert.Equal("FAT32", l[1].FileSystem); Assert.Equal("sdd", l[1].Name);
    }
}

public class RunnerAndReportTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("syn3run").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task FolderTarget_IsCleared_WhenNotSkipped_AndReformatListsAreWritten()
    {
        var dl = Path.Combine(_root, "dl"); Directory.CreateDirectory(dl);
        byte[] Data(byte b) => Enumerable.Repeat(b, 1000).ToArray();
        string Md5(byte[] d) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(d));
        var files = new Dictionary<string, byte[]> { ["app.tar.gz"] = Data(1), ["rw.tar.gz"] = Data(2), ["re.tar.gz"] = Data(3) };
        foreach (var (f, d) in files) await File.WriteAllBytesAsync(Path.Combine(dl, f), d);   // already downloaded => no server needed
        var data = Data(0);
        var usb = Path.Combine(_root, "usb"); Directory.CreateDirectory(usb); File.WriteAllText(Path.Combine(usb, "old.txt"), "x"); Directory.CreateDirectory(Path.Combine(usb, "olddir"));
        Ivsu I(string t, string f) => new() { Type = t, FileName = f, Url = "http://unused/" + f, Md5 = Md5(files[f]), FileSize = 1000 };
        var rw = I("TOOL", "rw.tar.gz"); var re = I("TOOL", "re.tar.gz");
        var tools = new SpecialTools { RwDataCleaner = rw, ReformatTool = re };
        var req = new UpdateRequest
        {
            Queue = new() { re, rw, I("APPS", "app.tar.gz") }, Tools = tools, InstallMode = InstallModes.Reformat, Release = "Sync 3.4", Region = "NA",
            Drive = new DriveInfoModel { IsFolder = true, MountPath = usb }, SkipFormat = false, DownloadDir = dl, AppVersion = "3",
        };
        var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient(new FakeHandler(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) }))), new AppLog());
        Assert.True(await runner.RunAsync(req, new Progress<RunProgress>(), default), runner.LastError);
        Assert.False(File.Exists(Path.Combine(usb, "old.txt"))); Assert.False(Directory.Exists(Path.Combine(usb, "olddir")));
        Assert.Equal("APPS=app.tar.gz", File.ReadAllText(Path.Combine(usb, "reformat.lst")).Trim());
        Assert.Contains("REFORMAT TOOL", File.ReadAllText(Path.Combine(usb, "autoinstall.lst")));
    }

    [Fact]
    public async Task Cancelled_Run_ReturnsFalse_WithoutError()
    {
        var req = new UpdateRequest { Queue = new() { new Ivsu { FileName = "a", Url = "http://unused/a" } }, Tools = new SpecialTools(), DownloadOnly = true, DownloadDir = _root };
        using var cts = new CancellationTokenSource(); cts.Cancel();
        var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog());
        Assert.False(await runner.RunAsync(req, new Progress<RunProgress>(), cts.Token)); Assert.Null(runner.LastError);
    }

    [Fact]
    public async Task UnclearableFolder_IsReportedNotThrown()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root") return;
        var usb = Path.Combine(_root, "ro"); Directory.CreateDirectory(usb); File.WriteAllText(Path.Combine(usb, "x"), "1");
        File.SetUnixFileMode(usb, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var req = new UpdateRequest { Queue = new(), Tools = new SpecialTools(), Drive = new DriveInfoModel { IsFolder = true, MountPath = usb }, DownloadDir = _root };
            var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog());
            Assert.False(await runner.RunAsync(req, new Progress<RunProgress>(), default)); Assert.Contains("clear", runner.LastError);
        }
        finally { File.SetUnixFileMode(usb, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public void LogReport_ContainsConfigDestinationFilesAndLog()
    {
        var usb = Path.Combine(_root, "u"); Directory.CreateDirectory(Path.Combine(usb, "SyncMyRide")); File.WriteAllText(Path.Combine(usb, "SyncMyRide", "a.tar.gz"), "abc");
        File.WriteAllText(Path.Combine(usb, "autoinstall.lst"), "AUTO"); File.WriteAllText(Path.Combine(usb, "reformat.lst"), "REF");
        var drive = new DriveInfoModel { Name = "SanDisk", SizeBytes = 1 << 30, FileSystem = "exFAT", PartitionType = "MBR" };
        var text = LogReport.Build("3.0", "Stable", "Arch", new ProfileSettings { CurrentRegion = "EU", CurrentVersion = 3420282, My20v2 = true }, "autoinstall", false, drive, usb, "RUNLOG");
        foreach (var s in new[] { "V3.0", "Arch", "Region: EU", "3.4.20282", "Mode: Drive", "SanDisk", "AUTOINSTALL.LST", "REFORMAT.LST", "FILES (1)", "a.tar.gz", "RUNLOG", "My20 Protection Enabled: True" }) Assert.Contains(s, text);
        Assert.Contains("Mode: Directory", LogReport.Build("3", "S", "o", new ProfileSettings(), "m", true, new DriveInfoModel { IsFolder = true }, usb, ""));
    }

    [Fact]
    public void Interrogator_DescribeIncludesEverything()
    {
        var r = new InterrogatorResult { Vin = "V", ApimModel = "M", ApimSizeGb = 16, Nav = false, ApimFree = "1G", Packages = { "P1" }, SoftwareName = "S", Generated = DateTimeOffset.Parse("2024-01-02T03:04:05Z") };
        r.Partitions.Add(new PartitionHealth { Type = "/fs/images/", Total = "16G", Available = "1G" }); r.AsBuilt.Add(new DidEntry { Value = "DE00", Response = "ab" });
        var (d, p, a) = Interrogator.Describe(r, "3.4.1");
        Assert.Contains("Non-Navigation", d); Assert.Contains("3.4.1 (S)", d); Assert.Contains("/fs/images/ = 1G / 16G", p); Assert.Contains("P1", p); Assert.Contains("DE00: AB", a);
    }

    [Fact]
    public void Interrogator_MissingModel_IsAnErrorWhenRequired()
    {
        const string xml = """<p:OTAModuleSnapShot xmlns:p="u" xmlns:d="d"><p:VIN>V</p:VIN><d:DID didType="ECU Delivery Assembly Number" didValue="F111"><d:Response></d:Response></d:DID></p:OTAModuleSnapShot>""";
        Assert.Equal("Unknown", Interrogator.Parse(xml).ApimModel);
        Assert.Equal("MODEL_NOT_FOUND", Assert.Throws<InterrogatorException>(() => Interrogator.Parse(xml, requireModel: true)).Message);
    }

    [Fact]
    public void AutoInstall_Rejects_MissingTools_And_HandlesMapsWithoutLicense()
    {
        Assert.Throws<InvalidOperationException>(() => AutoInstallBuilder.CreateAutoInstall(Array.Empty<Ivsu>(), new SpecialTools(), UpdateAction.Main, "r", "EU", false, "1", "S"));
        Assert.Throws<InvalidOperationException>(() => AutoInstallBuilder.CreateReformatAutoInstall(InstallModes.Downgrade, new SpecialTools { RwDataCleaner = new Ivsu(), ReformatTool = new Ivsu() }, "r", "EU", false, "1", "S"));
        var rw = new Ivsu { Type = "TOOL", FileName = "rw", FileSize = 1 };
        var s = AutoInstallBuilder.CreateAutoInstall(new[] { rw, new Ivsu { Type = "MAP", FileName = "m1", FileSize = 5000 } }, new SpecialTools { RwDataCleaner = rw }, UpdateAction.Main, "r", "EU", true, "1", "S");
        Assert.Contains("FORCED", s); Assert.Contains("m1", s);
        var down = AutoInstallBuilder.CreateReformatAutoInstall(InstallModes.Downgrade, new SpecialTools { RwDataCleaner = rw, ReformatTool = new Ivsu { FileName = "re" }, DowngradeApp = new Ivsu { FileName = "da" }, DowngradeTool = new Ivsu { FileName = "dt" } }, "Sync 3.3.19052", "EU", false, "1", "S");
        Assert.Contains("APP - da", down); Assert.Contains("REFORMAT TOOL - re", down);
    }

    [Fact]
    public void Localizer_FormatAndNativeNames()
    {
        var l = new Localizer(); l.SetLanguage("de");
        Assert.Equal("de", l.Code.Split('-')[0]);
        Assert.Contains("A", new Localizer().Format("MessageBox.FailedToValidate3", "A"));
        Assert.NotNull(Localizer.NativeName("de")); Assert.False(string.IsNullOrEmpty(Localizer.NativeName("zz-ZZ-bad")));
        var changed = 0; l.LanguageChanged += () => changed++; l.SetLanguage("en-US"); Assert.Equal(1, changed);
    }
}
