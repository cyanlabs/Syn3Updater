using System.Net;
using System.Security.Cryptography;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class EndToEndTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("syn3e2e").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private static (HttpListener, string, Dictionary<string, byte[]>) Serve(bool ignoreRange = false)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["/rw.tar.gz"] = RandomNumberGenerator.GetBytes(300_000),
            ["/app.tar.gz"] = RandomNumberGenerator.GetBytes(2_000_000),
        };
        var port = new Random().Next(20000, 40000);
        var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Start();
        _ = Task.Run(async () =>
        {
            while (l.IsListening)
            {
                HttpListenerContext c;
                try { c = await l.GetContextAsync(); } catch { return; }
                if (!files.TryGetValue(c.Request.Url!.AbsolutePath, out var data)) { c.Response.StatusCode = 404; c.Response.Close(); continue; }
                var range = c.Request.Headers["Range"];
                int start = 0, end = data.Length - 1;
                if (range != null && !ignoreRange) { var p = range.Replace("bytes=", "").Split('-'); start = int.Parse(p[0]); end = int.Parse(p[1]); c.Response.StatusCode = 206; }
                try
                {
                    c.Response.ContentLength64 = end - start + 1;
                    if (c.Request.HttpMethod != "HEAD") await c.Response.OutputStream.WriteAsync(data.AsMemory(start, end - start + 1));
                    c.Response.Close();
                }
                catch (Exception) { /* client dropped the connection (e.g. cancelled part): keep serving */ }
            }
        });
        return (l, $"http://127.0.0.1:{port}", files);
    }

    private static Ivsu Item(string baseUrl, string file, string type, byte[] data) => new()
    {
        Type = type, Name = file, FileName = file, Url = baseUrl + "/" + file, FileSize = data.Length,
        Md5 = Convert.ToHexString(MD5.HashData(data)),
    };

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public async Task FullRun_DownloadsValidatesCopiesAndWritesLists(int connections)
    {
        var (l, url, files) = Serve();
        try
        {
            var rw = Item(url, "rw.tar.gz", "TOOL", files["/rw.tar.gz"]);
            var app = Item(url, "app.tar.gz", "APPS", files["/app.tar.gz"]);
            var usb = Path.Combine(_root, "usb"); Directory.CreateDirectory(usb);
            var req = new UpdateRequest
            {
                Queue = new() { rw, app }, Tools = new SpecialTools { RwDataCleaner = rw }, Release = "Sync 3.4", Region = "NA",
                Drive = new DriveInfoModel { IsFolder = true, MountPath = usb, Name = "folder" }, SkipFormat = true,
                DownloadDir = Path.Combine(_root, "dl"), Connections = connections, AppVersion = "3.0.0",
            };
            var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog());
            Assert.True(await runner.RunAsync(req, new Progress<RunProgress>(), default), runner.LastError);

            Assert.Equal(files["/app.tar.gz"], await File.ReadAllBytesAsync(Path.Combine(usb, "SyncMyRide", "app.tar.gz")));
            var lst = await File.ReadAllTextAsync(Path.Combine(usb, "autoinstall.lst"));
            Assert.Contains("Item2 = APPS - app.tar.gz", lst);
            Assert.True(File.Exists(Path.Combine(usb, "DONTINDX.MSA")));
            Assert.Empty(Directory.GetFiles(req.DownloadDir, "*.part"));

            // second run reuses validated downloads (no server needed)
            l.Stop();
            Assert.True(await runner.RunAsync(req, new Progress<RunProgress>(), default), runner.LastError);
        }
        finally { l.Close(); }
    }

    [Fact]
    public async Task ParallelDownload_FallsBackToSingleStream_WhenServerIgnoresRange()
    {
        var (l, url, files) = Serve(ignoreRange: true);
        try
        {
            var item = Item(url, "app.tar.gz", "APPS", files["/app.tar.gz"]);
            var dest = Path.Combine(_root, "dl", "app.tar.gz");
            var ok = await new FileService(new HttpClient()).DownloadAsync(item.Url, dest, 4, new Progress<int>(), default);
            Assert.True(ok);
            Assert.Equal(files["/app.tar.gz"], await File.ReadAllBytesAsync(dest));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(dest)!, "*.part"));
        }
        finally { l.Close(); }
    }

    [Fact]
    public async Task CorruptDownload_FailsAfterThreeAttempts()
    {
        var (l, url, files) = Serve();
        try
        {
            var rw = Item(url, "rw.tar.gz", "TOOL", files["/rw.tar.gz"]); rw.Md5 = "00000000000000000000000000000000";
            var req = new UpdateRequest
            {
                Queue = new() { rw }, Tools = new SpecialTools { RwDataCleaner = rw }, DownloadOnly = true,
                DownloadDir = Path.Combine(_root, "dl"),
            };
            var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog());
            Assert.False(await runner.RunAsync(req, new Progress<RunProgress>(), default));
            Assert.Contains("3 attempts", runner.LastError);
        }
        finally { l.Close(); }
    }

    [SkippableLiveFact]
    public async Task LiveApi_ReturnsReleasesAndSpecialPackages()
    {
        var api = new ApiClient(new HttpClient(), ApiClient.ResolveToken, () => "");
        var rel = await api.GetReleases("NA");
        Assert.NotEmpty(rel.Releases!);
        var first = rel.Releases![0];
        var ivsus = await api.GetReleaseIvsus(first.Name, "nonnav");
        Assert.NotEmpty(ivsus.Releases![0].Ivsus!);
        var rw = await api.GetSpecialPackage(SpecialPackages.RWDataCleaner);
        Assert.False(string.IsNullOrEmpty(rw.Url));
        Assert.NotEmpty(rw.FileName);
    }
}

public sealed class SkippableLiveFactAttribute : FactAttribute
{
    public SkippableLiveFactAttribute()
    {
        if (!ApiClient.HasToken) Skip = "no API token compiled in (secrets/api-token.txt) or set in SYN3_API_TOKEN";
    }
}
