using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Models;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace Cyanlabs.Syn3Updater.Core.Services;

public record ValidationResult(bool Ok, string Message = "");

/// <summary>Thread-safe running byte total for one transfer, reported at most every ~100 ms (so 8 connections do not flood the UI).</summary>
internal sealed class ByteReporter(IProgress<long>? target)
{
    private long _total, _lastTicks;
    public long Total => Interlocked.Read(ref _total);
    public void Add(long n)
    {
        var t = Interlocked.Add(ref _total, n);
        if (target == null) return;
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastTicks);
        if (now - last >= 100 && Interlocked.CompareExchange(ref _lastTicks, now, last) == last) target.Report(t);
    }
    public void Reset() => Interlocked.Exchange(ref _total, 0);
    public void Flush() => target?.Report(Total);
}

public class RangeNotSupportedException : IOException { public RangeNotSupportedException() : base("Server does not support ranged downloads") { } }

/// <summary>Download, checksum and copy operations. Progress is reported as 0-100.</summary>
public class FileService
{
    private readonly HttpClient _http;
    public FileService(HttpClient http) { _http = http; }

    // ---------- download ----------
    /// <summary>Size of the pieces a file is fetched (and remembered) in. Null = chosen from the file size. Tests set it small.</summary>
    public long? ChunkSize { get; set; }
    /// <summary>First wait before a failed chunk is tried again; doubles on every further attempt.</summary>
    public int RetryDelayMs { get; set; } = 500;
    /// <summary>A connection that delivers no data for this long counts as failed (and is retried).</summary>
    public TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(30);
    private const int MaxChunkAttempts = 4;

    /// <summary>
    ///     Downloads a file. It is fetched in chunks over up to <paramref name="connections"/> connections, and what has arrived is remembered
    ///     next to the partial file, so an interrupted download (cancel, lost connection, closed app) continues where it stopped the next time.
    ///     Servers that cannot do ranged requests get a plain single-stream download instead (no resume).
    /// </summary>
    /// <param name="bytes">Optional: cumulative bytes received for this file in this run across all connections (throttled).</param>
    /// <param name="resumed">Optional: called once, before any transfer, with how many bytes were already on disk from an earlier run.</param>
    /// <returns>False when cancelled. The partial file is kept in that case: cancelling is a pause.</returns>
    public async Task<bool> DownloadAsync(string url, string destination, int connections, IProgress<int> progress, CancellationToken ct, IProgress<long>? bytes = null, Action<long>? resumed = null)
    {
        var counter = new ByteReporter(bytes);
        var temp = destination + ".part";
        var statePath = DownloadState.PathFor(temp);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            var remote = await GetInfoAsync(url, ct);

            if (remote.Length > 0)
            {
                for (var startOver = false; ; startOver = true)
                {
                    try
                    {
                        await DownloadChunkedAsync(url, temp, statePath, remote, Math.Clamp(connections, 1, 8), progress, ct, counter, resumed, startOver);
                        break;
                    }
                    catch (ResumeRejectedException) when (!startOver)
                    {
                        Discard(temp); counter.Reset();         // the remote file changed or the server will not continue: begin again
                    }
                    catch (RangeNotSupportedException)
                    {
                        Discard(temp); counter.Reset();         // server ignores Range: one plain connection instead of failing
                        await SingleOrCleanUpAsync(url, temp, remote.Length, progress, ct, counter);
                        break;
                    }
                }
            }
            else
            {
                Discard(temp);
                await SingleOrCleanUpAsync(url, temp, remote.Length, progress, ct, counter);
            }

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
            TryDelete(statePath);
            return true;
        }
        catch (OperationCanceledException) { return false; }                                 // kept: this is how a download is paused
        catch (HttpRequestException e) when (IsDefinitive(e)) { Discard(temp); throw; }      // the file is gone or refused: nothing to continue
    }

    /// <summary>A plain download cannot be continued, so a failed one leaves nothing behind.</summary>
    private async Task SingleOrCleanUpAsync(string url, string file, long length, IProgress<int> progress, CancellationToken ct, ByteReporter counter)
    {
        try { await DownloadSingleAsync(url, file, length, progress, ct, counter); }
        catch { TryDelete(file); throw; }
    }

    private static bool IsDefinitive(HttpRequestException e) =>
        e.StatusCode is { } code && (int)code is >= 400 and < 500 && code != System.Net.HttpStatusCode.RequestTimeout && (int)code != 429;

    /// <summary>Deletes a partial download and its state file.</summary>
    private static void Discard(string partFile) { TryDelete(partFile); TryDelete(DownloadState.PathFor(partFile)); TryDelete(DownloadState.PathFor(partFile) + ".tmp"); }

    /// <summary>Removes partial downloads nobody continued (older than <paramref name="olderThan"/>), so abandoned half-files do not fill the disk.</summary>
    public static int CleanStalePartials(string directory, TimeSpan olderThan)
    {
        var removed = 0;
        if (!Directory.Exists(directory)) return 0;
        foreach (var part in Directory.GetFiles(directory, "*.part"))
        {
            var state = DownloadState.PathFor(part);
            var newest = new[] { part, state }.Where(File.Exists).Max(f => File.GetLastWriteTimeUtc(f));
            if (DateTime.UtcNow - newest < olderThan) continue;
            Discard(part); removed++;
        }
        return removed;
    }

    /// <summary>Waits for the response headers, but not forever: a server that accepts the connection and never answers counts as a failed (retried) attempt.</summary>
    private async Task<HttpResponseMessage> SendWithTimeoutAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        guard.CancelAfter(StallTimeout);
        try { return await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, guard.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("The server did not respond in time."); }
    }

    private async Task<RemoteInfo> GetInfoAsync(string url, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await SendWithTimeoutAsync(head, ct);
        resp.EnsureSuccessStatusCode();
        long length = -1;
        if (resp.Content.Headers.ContentLength is long l) length = l;
        else if (resp.Headers.TryGetValues("X-Dropbox-Content-Length", out var v) && long.TryParse(v.FirstOrDefault(), out var d)) length = d;
        return new RemoteInfo(length, resp.Headers.ETag?.ToString(), resp.Content.Headers.LastModified?.ToString("R"));
    }

    private static long ChooseChunkSize(long length)
    {
        var chunk = Math.Clamp(length / 32, 256 * 1024, 8L * 1024 * 1024);
        return (chunk + 65535) / 65536 * 65536;
    }

    /// <summary>Makes the server answer "only if it is still the same file".</summary>
    private static System.Net.Http.Headers.RangeConditionHeaderValue? IfRange(RemoteInfo remote)
    {
        if (!string.IsNullOrEmpty(remote.ETag) && !remote.ETag.StartsWith("W/", StringComparison.Ordinal) && EntityTagHeaderValue.TryParse(remote.ETag, out var tag))
            return new RangeConditionHeaderValue(tag);
        if (!string.IsNullOrEmpty(remote.LastModified) && DateTimeOffset.TryParse(remote.LastModified, out var when))
            return new RangeConditionHeaderValue(when);
        return null;
    }

    private async Task DownloadChunkedAsync(string url, string file, string statePath, RemoteInfo remote, int connections, IProgress<int> progress, CancellationToken ct,
        ByteReporter counter, Action<long>? resumedCallback, bool startOver)
    {
        // 1. continue an earlier attempt when its leftovers are trustworthy, otherwise begin a fresh one
        var state = startOver ? null : DownloadState.Load(statePath);
        var resuming = state != null && state.Matches(url, remote) && File.Exists(file) && new FileInfo(file).Length == remote.Length;
        if (!resuming)
        {
            Discard(file);
            state = new DownloadState { Url = DownloadState.Identity(url), Length = remote.Length, ChunkSize = ChunkSize ?? ChooseChunkSize(remote.Length), ETag = remote.ETag, LastModified = remote.LastModified };
            await using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(remote.Length);
            state.Save(statePath);
        }

        var length = remote.Length;
        var done = new HashSet<int>(state!.Done);
        long downloaded = done.Sum(state.ChunkLength);
        var pending = new System.Collections.Concurrent.ConcurrentQueue<int>(Enumerable.Range(0, state.ChunkCount).Where(i => !done.Contains(i)));
        int last = (int)(downloaded * 100 / length);
        if (downloaded > 0) { resumedCallback?.Invoke(downloaded); progress.Report(last); }

        var stateLock = new object();
        var lastSave = Environment.TickCount64;
        var ifRange = IfRange(remote);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;

        async Task FetchChunkAsync(int index)
        {
            var start = state.ChunkStart(index); var end = start + state.ChunkLength(index) - 1;
            for (var attempt = 1; ; attempt++)
            {
                long counted = 0;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(start, end) } };
                    if (ifRange != null) req.Headers.IfRange = ifRange;
                    using var resp = await SendWithTimeoutAsync(req, token);
                    if (resp.StatusCode == System.Net.HttpStatusCode.OK) throw resuming ? new ResumeRejectedException("the server sent the whole file") : new RangeNotSupportedException();
                    if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable) throw new ResumeRejectedException("range not satisfiable");
                    resp.EnsureSuccessStatusCode();
                    if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) throw new RangeNotSupportedException();
                    if (resp.Content.Headers.ContentRange is { } cr && (cr.From != start || cr.To != end || (cr.Length.HasValue && cr.Length != length)))
                        throw new ResumeRejectedException("the server answered a different range");

                    await using var input = await resp.Content.ReadAsStreamAsync(token);
                    await using var output = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 81920, true);
                    output.Seek(start, SeekOrigin.Begin);
                    var buf = new byte[81920]; int n;
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                    while (true)
                    {
                        stall.CancelAfter(StallTimeout);
                        try { n = await input.ReadAsync(buf, stall.Token); }
                        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("The connection stopped delivering data."); }
                        if (n == 0) break;
                        await output.WriteAsync(buf.AsMemory(0, n), token);
                        counted += n;
                        var d = Interlocked.Add(ref downloaded, n); counter.Add(n);
                        var p = (int)(d * 100 / length);
                        if (p != Volatile.Read(ref last)) { Volatile.Write(ref last, p); progress.Report(Math.Min(p, 100)); }
                    }
                    if (counted != end - start + 1) throw new IOException("The connection closed before the chunk was complete.");
                    await output.FlushAsync(token); output.Flush(true);         // on disk before it is remembered as done

                    lock (stateLock)
                    {
                        state.Done.Add(index);
                        if (Environment.TickCount64 - lastSave >= 1000) { state.Save(statePath); lastSave = Environment.TickCount64; }
                    }
                    return;
                }
                catch (Exception e) when (IsTransient(e) && attempt < MaxChunkAttempts && !token.IsCancellationRequested)
                {
                    Interlocked.Add(ref downloaded, -counted); counter.Add(-counted);      // this attempt's bytes will be fetched again
                    await Task.Delay(RetryDelayMs << (attempt - 1), token);
                }
            }
        }

        async Task WorkerAsync()
        {
            while (!token.IsCancellationRequested && pending.TryDequeue(out var index)) await FetchChunkAsync(index);
        }

        var workers = Enumerable.Range(0, Math.Min(connections, Math.Max(1, pending.Count))).Select(_ => WorkerAsync()).ToList();
        try { await Task.WhenAll(workers); }
        catch
        {
            cts.Cancel();   // stop the other workers and let them release the file
            try { await Task.WhenAll(workers); } catch { /* already failing */ }
            lock (stateLock) { try { state.Save(statePath); } catch { /* best effort */ } }      // remember everything that did arrive
            throw;
        }
        if (ct.IsCancellationRequested) { lock (stateLock) { try { state.Save(statePath); } catch { } } ct.ThrowIfCancellationRequested(); }
        if (state.Done.Distinct().Count() != state.ChunkCount) throw new IOException("The download finished with chunks missing.");
        counter.Flush(); progress.Report(100);
    }

    /// <summary>Failures worth trying again: a dropped or stalled connection, a server error. Not "gone", not "cannot resume".</summary>
    private static bool IsTransient(Exception e) => e switch
    {
        ResumeRejectedException or RangeNotSupportedException or OperationCanceledException => false,
        HttpRequestException h => h.StatusCode == null || (int)h.StatusCode >= 500 || h.StatusCode == System.Net.HttpStatusCode.RequestTimeout || (int)h.StatusCode == 429,
        IOException => true,
        _ => false,
    };

    private async Task DownloadSingleAsync(string url, string file, long length, IProgress<int> progress, CancellationToken ct, ByteReporter counter)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        length = resp.Content.Headers.ContentLength ?? length;
        await using var input = await resp.Content.ReadAsStreamAsync(ct);
        await using var output = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        var buf = new byte[81920]; long total = 0; int last = -1, n;
        while ((n = await input.ReadAsync(buf, ct)) > 0)
        {
            await output.WriteAsync(buf.AsMemory(0, n), ct);
            total += n; counter.Add(n);
            if (length > 0) { int p = (int)(total * 100 / length); if (p != last) { last = p; progress.Report(p); } }
        }
        counter.Flush(); progress.Report(100);
    }

    // ---------- checksum ----------
    public static async Task<string?> Md5Async(string file, IProgress<int>? progress, CancellationToken ct, bool bypassCache = false)
    {
        await using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
        if (bypassCache) CacheEvictor.Drop(s);         // read what is on the device, not what the OS still has in memory from the copy
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = new byte[1 << 20]; long total = 0, size = s.Length; int last = -1, n;
        while ((n = await s.ReadAsync(buf, ct)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            md5.AppendData(buf, 0, n);
            total += n;
            if (size > 0) { int p = (int)(total * 100 / size); if (p != last) { last = p; progress?.Report(p); } }
        }
        return Convert.ToHexString(md5.GetHashAndReset());
    }

    /// <summary>
    /// Checks <paramref name="localFile"/> against <paramref name="md5"/>. With no checksum available it falls back to
    /// comparing against the source file (localOnly) or the remote Content-Length.
    /// </summary>
    public async Task<ValidationResult> ValidateAsync(string source, string localFile, string? md5, bool localOnly, IProgress<int>? progress, CancellationToken ct, bool bypassCache = false)
    {
        if (!File.Exists(localFile)) return new(false);
        var name = Path.GetFileName(localFile);
        var localMd5 = await Md5Async(localFile, progress, ct, bypassCache);

        if (string.IsNullOrWhiteSpace(md5))
        {
            long size = new FileInfo(localFile).Length;
            if (localOnly)
            {
                if (File.Exists(source) && new FileInfo(source).Length == size && localMd5 == await Md5Async(source, progress, ct))
                    return new(true, $"{name} checksum matches already verified local copy");
            }
            else
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, source);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                var len = resp.Content.Headers.ContentLength ?? throw new IOException("Could not get size of file from remote server");
                if (len == size) return new(true, $"no source checksum available for {name} comparing file size");
            }
        }
        else if (string.Equals(localMd5, md5, StringComparison.OrdinalIgnoreCase)) return new(true);

        if (ct.IsCancellationRequested) return new(false, "Process cancelled by user");
        return new(false, $"Validate: {name} (Failed!, Downloading)");
    }

    // ---------- copy ----------
    public static async Task<bool> CopyAsync(string source, string destination, IProgress<int> progress, CancellationToken ct, IProgress<long>? bytes = null)
    {
        var counter = new ByteReporter(bytes);
        const int buffer = 512 * 1024;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, buffer, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buf = new byte[buffer]; long total = 0, size = input.Length; int last = -1, n;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                total += n; counter.Add(n);
                int p = size == 0 ? 100 : (int)(total * 100 / size);
                if (p != last) { last = p; progress.Report(p); }
            }
            counter.Flush();
            return true;
        }
        catch (OperationCanceledException) { TryDelete(destination); return false; }
    }

    // ---------- naviextras multi-package ----------
    /// <summary>Unpacks a Naviextras .tar.gz bundle into its map and licence packages and returns them as queue items.</summary>
    public async Task<List<Ivsu>> ExtractMultiPackageAsync(Ivsu item, string downloadDir, CancellationToken ct)
    {
        var result = new List<Ivsu>();
        if (item.Source != "naviextras") return result;
        var path = Path.Combine(downloadDir, item.FileName);
        var destination = Path.Combine(downloadDir, Path.GetFileNameWithoutExtension(item.FileName.Replace(".tar.gz", ".tar")));
        Directory.CreateDirectory(destination);

        await using (var inStream = File.OpenRead(path))
        await using (var gzip = new GZipInputStream(inStream))
        using (var tar = TarArchive.CreateInputTarArchive(gzip, Encoding.ASCII))
            tar.ExtractContents(destination);

        foreach (var tarFile in Directory.GetFiles(destination, "*.tar.gz*", SearchOption.AllDirectories))
        {
            var fileName = Path.GetFileName(tarFile);                  // never trust paths from inside an archive
            var name = fileName.Replace(".tar.gz", "");
            var newPath = Path.Combine(downloadDir, fileName);
            if (File.Exists(newPath)) File.Delete(newPath);
            File.Move(tarFile, newPath);
            result.Add(new Ivsu
            {
                Type = name.Contains("14G424") ? "MAP_LICENSE" : name.Contains("14G421") ? "MAP" : "",
                Name = name, FileName = fileName, FileSize = new FileInfo(newPath).Length, Selected = true,
                Md5 = await Md5Async(newPath, null, ct),
            });
        }
        return result;
    }

    public static string MakeValidFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }

    private static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch (IOException) { } }
}
