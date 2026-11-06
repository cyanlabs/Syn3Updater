using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>A file server that behaves like a real one for resuming: Range, If-Range, ETag, and misbehaviour on demand.</summary>
public sealed class ResumeServer : IDisposable
{
    private HttpListener _l = new();
    private readonly object _gate = new();
    public string Url { get; } = "";
    public byte[] Data { get; set; }
    public string? ETag { get; set; } = "\"v1\"";
    public bool SupportRange { get; set; } = true;
    public int AbortNext;            // cut the next N ranged GETs off half way through the body
    public int FailNext;             // answer the next N ranged GETs with FailStatus
    public int FailStatus = 503;
    public int StallNext;            // hold the next N ranged GETs without sending anything
    public int GetStatusAlways;      // when set, every GET answers with this status (HEAD stays fine)
    public List<(long Start, long End)> Ranges { get; } = new();
    public int FullGets { get; private set; }

    public ResumeServer(int size = 1 << 20)
    {
        Data = new byte[size]; new Random(7).NextBytes(Data);
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(40000, 60000);
            _l = new HttpListener();                                  // a failed Start() disposes the listener, so every attempt needs a new one
            try { _l.Prefixes.Add($"http://127.0.0.1:{port}/"); _l.Start(); Url = $"http://127.0.0.1:{port}/file.tar.gz"; break; }
            catch (HttpListenerException) when (attempt < 20) { _l.Close(); }
        }
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                _ = Task.Run(() => Handle(c));
            }
        });
    }

    public void Change(byte[] data, string? etag) { lock (_gate) { Data = data; ETag = etag; } }
    public (long Start, long End)[] RangesSnapshot() { lock (_gate) return Ranges.ToArray(); }
    public void ClearRanges() { lock (_gate) Ranges.Clear(); }

    private async Task Handle(HttpListenerContext c)
    {
        try
        {
            byte[] data; string? etag; lock (_gate) { data = Data; etag = ETag; }
            if (etag != null) c.Response.Headers["ETag"] = etag;
            if (c.Request.HttpMethod == "HEAD") { c.Response.ContentLength64 = data.Length; c.Response.Close(); return; }
            if (GetStatusAlways != 0) { c.Response.StatusCode = GetStatusAlways; c.Response.Close(); return; }

            var range = c.Request.Headers["Range"]; var ifRange = c.Request.Headers["If-Range"];
            if (!SupportRange || range == null || (ifRange != null && ifRange != etag))      // RFC: a changed file is sent whole
            {
                lock (_gate) FullGets++;
                c.Response.ContentLength64 = data.Length; await c.Response.OutputStream.WriteAsync(data); c.Response.Close(); return;
            }
            var p = range.Replace("bytes=", "").Split('-'); long start = long.Parse(p[0]), end = Math.Min(long.Parse(p[1]), data.Length - 1);
            lock (_gate) Ranges.Add((start, end));
            if (Interlocked.Decrement(ref FailNext) >= 0) { c.Response.StatusCode = FailStatus; c.Response.Close(); return; } else Interlocked.Increment(ref FailNext);
            if (Interlocked.Decrement(ref StallNext) >= 0) { await Task.Delay(8000); c.Response.Abort(); return; } else Interlocked.Increment(ref StallNext);

            c.Response.StatusCode = 206; c.Response.Headers["Content-Range"] = $"bytes {start}-{end}/{data.Length}";
            c.Response.ContentLength64 = end - start + 1;
            if (Interlocked.Decrement(ref AbortNext) >= 0)
            {
                await c.Response.OutputStream.WriteAsync(data.AsMemory((int)start, (int)((end - start + 1) / 2)));
                await c.Response.OutputStream.FlushAsync(); c.Response.Abort(); return;
            }
            Interlocked.Increment(ref AbortNext);
            await c.Response.OutputStream.WriteAsync(data.AsMemory((int)start, (int)(end - start + 1))); c.Response.Close();
        }
        catch { /* client went away */ }
    }

    public void Dispose() { try { _l.Close(); } catch { } }
}

internal sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T v) => a(v); }

public class ResumeDownloadTests : IDisposable
{
    private const int Chunk = 65536;
    private readonly string _dir = Directory.CreateTempSubdirectory("resume").FullName;
    private string Dest => Path.Combine(_dir, "file.tar.gz");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static FileService Svc(long chunk = Chunk, int stallMs = 30_000) => new(new HttpClient()) { ChunkSize = chunk, RetryDelayMs = 0, StallTimeout = TimeSpan.FromMilliseconds(stallMs) };
    private static int DoneChunks(string statePath) => JsonDocument.Parse(File.ReadAllText(statePath)).RootElement.GetProperty("Done").GetArrayLength();

    /// <summary>Starts a download and cancels it once <paramref name="percent"/> has arrived.</summary>
    private async Task<bool> Interrupt(FileService svc, ResumeServer server, int percent, int connections = 1)
    {
        using var cts = new CancellationTokenSource();
        return await svc.DownloadAsync(server.Url, Dest, connections, new SyncProgress<int>(p => { if (p >= percent) cts.Cancel(); }), cts.Token);
    }

    [Fact]
    public async Task AnInterruptedDownload_ContinuesWhereItStopped_FetchingOnlyTheMissingChunks()
    {
        using var server = new ResumeServer(); var svc = Svc();
        Assert.False(await Interrupt(svc, server, 50));                                         // "paused"
        Assert.True(File.Exists(Dest + ".part")); Assert.True(File.Exists(Dest + ".part.json")); Assert.False(File.Exists(Dest));
        var had = DoneChunks(Dest + ".part.json"); Assert.InRange(had, 4, 15);

        server.ClearRanges(); long resumedBytes = -1;
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default, null, r => resumedBytes = r));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(16 - had, server.RangesSnapshot().Length);                                  // only what was missing
        Assert.Equal(had * (long)Chunk, resumedBytes);
        Assert.Empty(Directory.GetFiles(_dir, "*.part*"));                                       // no leftovers
    }

    [Fact]
    public async Task Resume_ReportsTheEarlierProgress_ButCountsOnlyNewBytesAsTransferred()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 60);
        var percents = new List<int>(); long lastBytes = 0;
        await svc.DownloadAsync(server.Url, Dest, 1, new SyncProgress<int>(percents.Add), default, new SyncProgress<long>(b => lastBytes = b));
        Assert.True(percents[0] >= 50);                                                          // the bar starts where it was, not at 0
        Assert.True(percents.SequenceEqual(percents.OrderBy(x => x))); Assert.Equal(100, percents[^1]);
        Assert.InRange(lastBytes, 1, server.Data.Length / 2);                                    // speed/ETA see this run's bytes only
    }

    [Fact]
    public async Task Resuming_WorksWithADifferentNumberOfConnections()
    {
        using var server = new ResumeServer(); var svc = Svc();
        Assert.False(await Interrupt(svc, server, 40, connections: 1));
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 6, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task ParallelConnections_AlsoResume()
    {
        using var server = new ResumeServer(); var svc = Svc();
        Assert.False(await Interrupt(svc, server, 45, connections: 4)); var had = DoneChunks(Dest + ".part.json"); Assert.InRange(had, 1, 15);
        server.ClearRanges(); Assert.True(await svc.DownloadAsync(server.Url, Dest, 4, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest)); Assert.True(server.RangesSnapshot().Length <= 16 - had + 0); Assert.True(server.RangesSnapshot().Length >= 1);
    }

    [Fact]
    public async Task IfTheRemoteFileChanged_TheOldPartIsThrownAway_AndTheNewFileIsDownloaded()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        var fresh = new byte[server.Data.Length]; new Random(99).NextBytes(fresh); server.Change(fresh, "\"v2\"");
        long resumedBytes = -1;
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 2, new Progress<int>(), default, null, r => resumedBytes = r));
        Assert.Equal(fresh, await File.ReadAllBytesAsync(Dest)); Assert.Equal(-1, resumedBytes);   // nothing was reused
    }

    [Fact]
    public async Task WithoutAnETag_ADifferentSize_AlsoMeansStartOver()
    {
        using var server = new ResumeServer { ETag = null }; var svc = Svc();
        await Interrupt(svc, server, 50);
        var bigger = new byte[server.Data.Length + 12345]; new Random(5).NextBytes(bigger); server.Change(bigger, null);
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 2, new Progress<int>(), default));
        Assert.Equal(bigger, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task ETagThatChangesWhileTheServerKeepsTheSameSize_IsCaughtToo()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        var other = (byte[])server.Data.Clone(); other[0] ^= 0xFF; other[^1] ^= 0xFF; server.Change(other, "\"v2\"");
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default));
        Assert.Equal(other, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task AServerThatStopsHonouringRanges_MidwayThroughResuming_StillEndsWithTheFile()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        server.SupportRange = false;
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 3, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest)); Assert.Empty(Directory.GetFiles(_dir, "*.part*"));
    }

    [Fact]
    public async Task AServerWithoutRanges_GetsAPlainDownload_AndNoLeftovers()
    {
        using var server = new ResumeServer { SupportRange = false }; var svc = Svc();
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 4, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest)); Assert.Empty(Directory.GetFiles(_dir, "*.part*"));
    }

    [Theory]
    [InlineData("garbage, not json")] [InlineData("{\"Version\":1,\"Url\":\"x\"}")] [InlineData("")]
    public async Task ACorruptOrForeignStateFile_IsIgnored_NotTrusted(string state)
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        await File.WriteAllTextAsync(Dest + ".part.json", state);
        server.ClearRanges(); long resumedBytes = -1;
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default, null, r => resumedBytes = r));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest)); Assert.Equal(-1, resumedBytes); Assert.Equal(16, server.RangesSnapshot().Length);
    }

    [Fact]
    public async Task ATruncatedPartFile_MeansStartOver()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        await using (var fs = new FileStream(Dest + ".part", FileMode.Open)) fs.SetLength(1000);
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 2, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task AStateFileWithoutItsPartFile_MeansStartOver()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50); File.Delete(Dest + ".part");
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 2, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task DroppedConnections_AreRetried_WithoutCountingBytesTwice()
    {
        using var server = new ResumeServer { AbortNext = 3 }; var svc = Svc(); long last = 0; int maxPct = 0;
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 2, new SyncProgress<int>(p => maxPct = Math.Max(maxPct, p)), default, new SyncProgress<long>(b => last = b)));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
        Assert.Equal(server.Data.Length, last); Assert.Equal(100, maxPct);                       // a retried chunk is not double-counted
        Assert.True(server.RangesSnapshot().Length >= 16 + 3);                                   // the failed ones were asked for again
    }

    [Fact]
    public async Task TemporaryServerErrors_AreRetried()
    {
        using var server = new ResumeServer { FailNext = 2, FailStatus = 503 }; var svc = Svc();
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public async Task AServerThatKeepsFailing_ThrowsButKeepsWhatItHas_ForNextTime()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50); var had = DoneChunks(Dest + ".part.json");
        server.GetStatusAlways = 503;
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default));
        Assert.True(File.Exists(Dest + ".part.json")); Assert.True(DoneChunks(Dest + ".part.json") >= had);       // still resumable

        server.GetStatusAlways = 0; server.ClearRanges();
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default)); Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
        Assert.True(server.RangesSnapshot().Length <= 16 - had);
    }

    [Fact]
    public async Task AFileThatIsGone_ThrowsAndLeavesNothingBehind()
    {
        using var server = new ResumeServer(); var svc = Svc();
        await Interrupt(svc, server, 50);
        server.GetStatusAlways = 404;
        await Assert.ThrowsAsync<HttpRequestException>(() => svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default));
        Assert.Empty(Directory.GetFiles(_dir, "*.part*"));
    }

    [Fact]
    public async Task AStalledConnection_IsGivenUpOnAndRetried()
    {
        using var server = new ResumeServer { StallNext = 1 }; var svc = Svc(stallMs: 400);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 1, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest)); Assert.True(watch.Elapsed < TimeSpan.FromSeconds(6));
    }

    [Fact]
    public async Task TheStateIsSavedWithEveryChunkOnFailure_SoNothingThatArrivedIsLost()
    {
        using var server = new ResumeServer { FailStatus = 503 }; var svc = Svc();
        server.FailNext = 0; await Interrupt(svc, server, 90);
        Assert.InRange(DoneChunks(Dest + ".part.json"), 12, 16);                                  // the last 1-second save window did not eat them
    }

    [Fact]
    public async Task ACompleteDownload_LeavesOnlyTheFinalFile()
    {
        using var server = new ResumeServer(); var svc = Svc();
        Assert.True(await svc.DownloadAsync(server.Url, Dest, 4, new Progress<int>(), default));
        Assert.Equal(new[] { "file.tar.gz" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task ASmallFile_NeedingOneChunk_StillWorks()
    {
        using var server = new ResumeServer(300); var svc = Svc(chunk: 0);
        svc.ChunkSize = null; Assert.True(await svc.DownloadAsync(server.Url, Dest, 8, new Progress<int>(), default));
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Dest));
    }

    [Fact]
    public void StalePartials_AreCleanedUp_ButRecentOnesAndOtherFilesAreNot()
    {
        File.WriteAllText(Path.Combine(_dir, "old.tar.gz.part"), "x"); File.WriteAllText(Path.Combine(_dir, "old.tar.gz.part.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "new.tar.gz.part"), "x"); File.WriteAllText(Path.Combine(_dir, "new.tar.gz.part.json"), "{}");
        File.WriteAllText(Path.Combine(_dir, "keep.tar.gz"), "x");
        foreach (var f in new[] { "old.tar.gz.part", "old.tar.gz.part.json" }) File.SetLastWriteTimeUtc(Path.Combine(_dir, f), DateTime.UtcNow.AddDays(-20));
        Assert.Equal(1, FileService.CleanStalePartials(_dir, TimeSpan.FromDays(14)));
        Assert.False(File.Exists(Path.Combine(_dir, "old.tar.gz.part"))); Assert.False(File.Exists(Path.Combine(_dir, "old.tar.gz.part.json")));
        Assert.True(File.Exists(Path.Combine(_dir, "new.tar.gz.part"))); Assert.True(File.Exists(Path.Combine(_dir, "keep.tar.gz")));
        Assert.Equal(0, FileService.CleanStalePartials(Path.Combine(_dir, "missing"), TimeSpan.Zero));
    }
}

public class DownloadStateTests
{
    private static DownloadState S(Action<DownloadState>? edit = null)
    {
        var s = new DownloadState { Url = "http://h/f", Length = 1000, ChunkSize = 100, ETag = "\"a\"", Done = { 0, 3 } }; edit?.Invoke(s); return s;
    }
    private static readonly RemoteInfo Same = new(1000, "\"a\"", null);

    [Fact] public void MatchesTheSameFile() => Assert.True(S().Matches("http://h/f", Same));
    [Fact] public void IgnoresTheQueryString_SignedLinksChange() => Assert.True(S().Matches("http://h/f?token=2", Same));
    [Fact] public void ADifferentPath_DoesNotMatch() => Assert.False(S().Matches("http://h/other", Same));
    [Fact] public void ADifferentLength_DoesNotMatch() => Assert.False(S().Matches("http://h/f", Same with { Length = 1001 }));
    [Fact] public void ADifferentETag_DoesNotMatch() => Assert.False(S().Matches("http://h/f", Same with { ETag = "\"b\"" }));
    [Fact] public void ETagAppearingOrVanishing_DoesNotMatch() { Assert.False(S().Matches("http://h/f", Same with { ETag = null })); Assert.False(S(s => s.ETag = null).Matches("http://h/f", Same)); }
    [Fact] public void WithoutETags_TheLastModifiedDecides() { Assert.True(S(s => { s.ETag = null; s.LastModified = "L"; }).Matches("http://h/f", new(1000, null, "L"))); Assert.False(S(s => { s.ETag = null; s.LastModified = "L"; }).Matches("http://h/f", new(1000, null, "M"))); }
    [Fact] public void ChunksOutOfRange_OrRepeated_AreRejected() { Assert.False(S(s => s.Done.Add(10)).Matches("http://h/f", Same)); Assert.False(S(s => s.Done.Add(-1)).Matches("http://h/f", Same)); Assert.False(S(s => s.Done.Add(3)).Matches("http://h/f", Same)); }
    [Fact] public void AZeroChunkSize_IsRejected() => Assert.False(S(s => s.ChunkSize = 0).Matches("http://h/f", Same));
    [Fact] public void ChunkMath_HandlesTheShortLastChunk() { var s = S(s => { s.Length = 950; }); Assert.Equal(10, s.ChunkCount); Assert.Equal(50, s.ChunkLength(9)); Assert.Equal(900, s.ChunkStart(9)); }

    [Fact]
    public void SaveAndLoad_RoundTrip_AndLeaveNoTempFile()
    {
        var dir = Directory.CreateTempSubdirectory("state").FullName; var path = Path.Combine(dir, "f.part.json");
        try { S().Save(path); var back = DownloadState.Load(path)!; Assert.Equal(new[] { 0, 3 }, back.Done); Assert.Equal("\"a\"", back.ETag); Assert.Single(Directory.GetFiles(dir)); Assert.Null(DownloadState.Load(Path.Combine(dir, "nope"))); }
        finally { Directory.Delete(dir, true); }
    }
}

public class ResumeRunnerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("resumerun").FullName;
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public async Task TheRunner_ResumesAnInterruptedFile_ReportsIt_AndTheRemainingFigureShrinks()
    {
        using var server = new ResumeServer(); var dl = Path.Combine(_root, "dl"); Directory.CreateDirectory(dl);
        var item = new Ivsu { Type = "APPS", Name = "file", FileName = "file.tar.gz", Url = server.Url, FileSize = server.Data.Length, Md5 = Convert.ToHexString(MD5.HashData(server.Data)) };
        var req = new UpdateRequest { Queue = new() { item }, Tools = new SpecialTools(), Release = "r", Region = "NA", DownloadOnly = true, DownloadDir = dl, Connections = 1, AppVersion = "3.0.0" };
        var svc = new FileService(new HttpClient()) { ChunkSize = 65536 };

        using (var cts = new CancellationTokenSource())                                    // an earlier run that was cut short
            await svc.DownloadAsync(server.Url, Path.Combine(dl, "file.tar.gz"), 1, new SyncProgress<int>(p => { if (p >= 50) cts.Cancel(); }), cts.Token);

        var had = JsonDocument.Parse(File.ReadAllText(Path.Combine(dl, "file.tar.gz.part.json"))).RootElement.GetProperty("Done").GetArrayLength();
        var reports = new List<RunProgress>();
        var runner = new UpdateRunner(DriveServiceFactory.Create(), svc, new AppLog());
        Assert.True(await runner.RunAsync(req, new SyncProgress<RunProgress>(reports.Add), default), runner.LastError);
        Assert.Equal(server.Data, await File.ReadAllBytesAsync(Path.Combine(dl, "file.tar.gz")));

        var resuming = reports.First(r => r.Status.StartsWith("Resuming:"));
        Assert.Contains("already downloaded", resuming.Status);
        Assert.Equal(server.Data.Length - had * 65536L, resuming.RemainingBytes);            // exactly what was already there leaves the "still to come" total
        Assert.Equal(0, resuming.TransferredBytes);                                         // and is not counted as speed
        Assert.Empty(Directory.GetFiles(dl, "*.part*"));
    }

    [Fact]
    public async Task TheRunner_ClearsAbandonedPartialsInTheDownloadFolder()
    {
        using var server = new ResumeServer(2000); var dl = Path.Combine(_root, "dl"); Directory.CreateDirectory(dl);
        var stale = Path.Combine(dl, "abandoned.tar.gz.part"); File.WriteAllText(stale, "x"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-30));
        var item = new Ivsu { Type = "APPS", Name = "file", FileName = "file.tar.gz", Url = server.Url, FileSize = server.Data.Length, Md5 = Convert.ToHexString(MD5.HashData(server.Data)) };
        var req = new UpdateRequest { Queue = new() { item }, Tools = new SpecialTools(), Release = "r", Region = "NA", DownloadOnly = true, DownloadDir = dl, Connections = 1, AppVersion = "3.0.0" };
        Assert.True(await new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog()).RunAsync(req, new Progress<RunProgress>(), default));
        Assert.False(File.Exists(stale));
    }
}
