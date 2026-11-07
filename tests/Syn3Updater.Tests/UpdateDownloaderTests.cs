using System.Net;
using System.Net.Http.Headers;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

public class UpdateDownloaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syn3-dl-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static readonly byte[] Data = Enumerable.Range(0, 5000).Select(i => (byte)(i * 7 % 251)).ToArray();

    /// <summary>A server that honours Range requests.</summary>
    private static FuncHandler Server(byte[] data, bool honourRange = true) => new(req =>
    {
        if (honourRange && req.Headers.Range?.Ranges.FirstOrDefault()?.From is long from)
        {
            if (from >= data.Length) return new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable);
            var r = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[(int)from..]) };
            r.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, data.Length - 1, data.Length);
            return r;
        }
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };
    });

    private static UpdateDownloader Dl(FuncHandler h, Func<string, long>? free = null) => new(new HttpClient(h), free ?? (_ => long.MaxValue));

    [Fact]
    public async Task A_good_download_is_kept_and_reports_progress()
    {
        var progress = new List<double>();
        var path = await Dl(Server(Data)).DownloadAsync(Asset(Data), _dir, new Progress<double>(progress.Add));
        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz"), path);
        Assert.False(File.Exists(path + ".part"));
        await Task.Delay(50);                                          // Progress<T> posts asynchronously
        Assert.Equal(1.0, progress.Max());
    }

    [Fact]
    public async Task A_wrong_checksum_is_discarded_and_nothing_is_left_behind()
    {
        var bad = (byte[])Data.Clone(); bad[10] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl(Server(bad)).DownloadAsync(Asset(Data), _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_short_download_is_discarded()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl(Server(Data[..4000])).DownloadAsync(Asset(Data), _dir));
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task A_server_sending_more_than_the_manifest_says_is_stopped()
    {
        var longer = Data.Concat(new byte[500]).ToArray();
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl(Server(longer)).DownloadAsync(Asset(Data), _dir));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => !f.EndsWith(".part"));        // never promoted to a finished file
    }

    [Fact]
    public async Task A_partial_file_is_continued_with_a_range_request()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part"), Data[..2000]);
        var h = Server(Data);
        var path = await Dl(h).DownloadAsync(Asset(Data), _dir);
        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
        Assert.Equal(2000, h.Requests.Single().Headers.Range!.Ranges.Single().From);
    }

    [Fact]
    public async Task A_server_that_ignores_ranges_still_gives_a_correct_file()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part"), Data[..2000]);
        var path = await Dl(Server(Data, honourRange: false)).DownloadAsync(Asset(Data), _dir);
        Assert.Equal(Data, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task A_corrupt_partial_file_fails_the_checksum_instead_of_being_trusted()
    {
        Directory.CreateDirectory(_dir);
        var junk = (byte[])Data[..2000].Clone(); junk[5] ^= 1;
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part"), junk);
        await Assert.ThrowsAsync<InvalidDataException>(() => Dl(Server(Data)).DownloadAsync(Asset(Data), _dir));
        Assert.Empty(Directory.GetFiles(_dir));          // so the next try starts clean
    }

    [Fact]
    public async Task A_partial_file_longer_than_expected_is_restarted()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part"), new byte[Data.Length + 100]);
        Assert.Equal(Data, await File.ReadAllBytesAsync(await Dl(Server(Data)).DownloadAsync(Asset(Data), _dir)));
    }

    [Fact]
    public async Task A_range_the_server_refuses_restarts_from_nothing()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part"), Data[..2000]);
        var calls = 0;
        var h = new FuncHandler(req => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Data) });
        Assert.Equal(Data, await File.ReadAllBytesAsync(await Dl(h).DownloadAsync(Asset(Data), _dir)));
    }

    [Fact]
    public async Task A_file_already_downloaded_and_correct_is_not_fetched_again()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz"), Data);
        var h = Server(Data);
        await Dl(h).DownloadAsync(Asset(Data), _dir);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task A_stale_finished_file_with_the_wrong_content_is_replaced()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz"), new byte[Data.Length]);
        Assert.Equal(Data, await File.ReadAllBytesAsync(await Dl(Server(Data)).DownloadAsync(Asset(Data), _dir)));
    }

    [Fact]
    public async Task Too_little_disk_space_stops_before_downloading()
    {
        var h = Server(Data);
        var ex = await Assert.ThrowsAsync<IOException>(() => Dl(h, _ => 1024).DownloadAsync(Asset(Data), _dir));
        Assert.Contains("free disk space", ex.Message);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Unknown_free_space_does_not_block() =>
        Assert.Equal(Data, await File.ReadAllBytesAsync(await Dl(Server(Data), _ => -1).DownloadAsync(Asset(Data), _dir)));

    [Fact]
    public async Task A_server_error_is_reported()
    {
        var h = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => Dl(h).DownloadAsync(Asset(Data), _dir));
    }

    [Fact]
    public async Task Cancelling_stops_the_download_and_keeps_the_partial_file_for_next_time()
    {
        using var cts = new CancellationTokenSource();
        var h = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SlowStream(Data, cts)) });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dl(h).DownloadAsync(Asset(Data), _dir, null, cts.Token));
        Assert.False(File.Exists(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz")));
    }

    [Fact]
    public async Task A_connection_that_goes_silent_is_given_up_on_and_the_partial_file_is_kept_for_the_next_try()
    {
        var h = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(Data[..1500])) });
        var dl = new UpdateDownloader(new HttpClient(h), _ => long.MaxValue, TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<IOException>(() => dl.DownloadAsync(Asset(Data), _dir));
        Assert.Contains("stalled", ex.Message); Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        Assert.Equal(1500, new FileInfo(Path.Combine(_dir, "Syn3Updater-3.1.0-linux-x64.tar.gz.part")).Length);       // what arrived is kept, so the next try continues from there
        // and the next try, with a working connection, continues from it
        Assert.Equal(Data, await File.ReadAllBytesAsync(await Dl(Server(Data)).DownloadAsync(Asset(Data), _dir)));
    }

    [Fact]
    public async Task A_slow_but_steady_connection_is_not_mistaken_for_a_stalled_one()
    {
        var h = new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new TricklingStream(Data)) });
        var dl = new UpdateDownloader(new HttpClient(h), _ => long.MaxValue, TimeSpan.FromMilliseconds(400));            // total time exceeds the idle limit, but data keeps arriving
        Assert.Equal(Data, await File.ReadAllBytesAsync(await dl.DownloadAsync(Asset(Data), _dir)));
    }

    private sealed class StallingStream : Stream
    {
        private readonly byte[] _d; private int _p;
        public StallingStream(byte[] d) => _d = d;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_p >= _d.Length) { await Task.Delay(Timeout.Infinite, ct); return 0; }              // the connection dies silently: no data, no close
            var n = Math.Min(buffer.Length, _d.Length - _p); _d.AsMemory(_p, n).CopyTo(buffer); _p += n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class TricklingStream : Stream
    {
        private readonly byte[] _d; private int _p;
        public TricklingStream(byte[] d) => _d = d;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_p >= _d.Length) return 0;
            await Task.Delay(150, ct);
            var n = Math.Min(Math.Min(buffer.Length, 500), _d.Length - _p); _d.AsMemory(_p, n).CopyTo(buffer); _p += n; return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class SlowStream : Stream
    {
        private readonly byte[] _d; private readonly CancellationTokenSource _c; private int _p;
        public SlowStream(byte[] d, CancellationTokenSource c) { _d = d; _c = c; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_p >= 1000) { _c.Cancel(); ct.ThrowIfCancellationRequested(); }
            var n = Math.Min(500, Math.Min(buffer.Length, _d.Length - _p));
            _d.AsMemory(_p, n).CopyTo(buffer); _p += n; await Task.Yield(); return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
