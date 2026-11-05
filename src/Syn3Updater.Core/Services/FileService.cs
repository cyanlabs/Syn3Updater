using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Models;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace Cyanlabs.Syn3Updater.Core.Services;

public record ValidationResult(bool Ok, string Message = "");

public class RangeNotSupportedException : IOException { public RangeNotSupportedException() : base("Server does not support ranged downloads") { } }

/// <summary>Download, checksum and copy operations. Progress is reported as 0-100.</summary>
public class FileService
{
    private readonly HttpClient _http;
    public FileService(HttpClient http) { _http = http; }

    // ---------- download ----------
    public async Task<bool> DownloadAsync(string url, string destination, int connections, IProgress<int> progress, CancellationToken ct)
    {
        var temp = destination + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            long length = await GetLengthAsync(url, ct);
            if (File.Exists(temp)) File.Delete(temp);

            if (length > 0 && connections > 1)
            {
                try { await DownloadParallelAsync(url, temp, length, Math.Clamp(connections, 1, 8), progress, ct); }
                catch (RangeNotSupportedException)
                {
                    TryDelete(temp);   // server ignores Range: fall back to one connection instead of failing
                    await DownloadSingleAsync(url, temp, length, progress, ct);
                }
            }
            else
                await DownloadSingleAsync(url, temp, length, progress, ct);

            if (File.Exists(destination)) File.Delete(destination);
            File.Move(temp, destination);
            return true;
        }
        catch (OperationCanceledException) { TryDelete(temp); return false; }
        catch { TryDelete(temp); throw; }
    }

    private async Task<long> GetLengthAsync(string url, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, url);
        using var resp = await _http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long l) return l;
        if (resp.Headers.TryGetValues("X-Dropbox-Content-Length", out var v) && long.TryParse(v.FirstOrDefault(), out var d)) return d;
        return -1;
    }

    private async Task DownloadSingleAsync(string url, string file, long length, IProgress<int> progress, CancellationToken ct)
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
            total += n;
            if (length > 0) { int p = (int)(total * 100 / length); if (p != last) { last = p; progress.Report(p); } }
        }
        progress.Report(100);
    }

    private async Task DownloadParallelAsync(string url, string file, long length, int connections, IProgress<int> progress, CancellationToken ct)
    {
        await using (var fs = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None)) fs.SetLength(length);
        long downloaded = 0; int last = -1;
        long chunk = length / connections;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = cts.Token;
        var tasks = Enumerable.Range(0, connections).Select(async i =>
        {
            long start = i * chunk, end = i == connections - 1 ? length - 1 : start + chunk - 1;
            using var req = new HttpRequestMessage(HttpMethod.Get, url) { Headers = { Range = new RangeHeaderValue(start, end) } };
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            resp.EnsureSuccessStatusCode();
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                throw new RangeNotSupportedException();
            await using var input = await resp.Content.ReadAsStreamAsync(token);
            await using var output = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Write, 81920, true);
            output.Seek(start, SeekOrigin.Begin);
            var buf = new byte[81920]; int n;
            while ((n = await input.ReadAsync(buf, token)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), token);
                long d = Interlocked.Add(ref downloaded, n);
                int p = (int)(d * 100 / length);
                if (p != Volatile.Read(ref last)) { Volatile.Write(ref last, p); progress.Report(p); }
            }
        }).ToList();
        try { await Task.WhenAll(tasks); }
        catch
        {
            cts.Cancel();   // stop the other parts and let them release the file before the caller retries or cleans up
            try { await Task.WhenAll(tasks); } catch { /* already failing */ }
            throw;
        }
        progress.Report(100);
    }

    // ---------- checksum ----------
    public static async Task<string?> Md5Async(string file, IProgress<int>? progress, CancellationToken ct)
    {
        await using var s = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, true);
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
    public async Task<ValidationResult> ValidateAsync(string source, string localFile, string? md5, bool localOnly, IProgress<int>? progress, CancellationToken ct)
    {
        if (!File.Exists(localFile)) return new(false);
        var name = Path.GetFileName(localFile);
        var localMd5 = await Md5Async(localFile, progress, ct);

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
    public static async Task<bool> CopyAsync(string source, string destination, IProgress<int> progress, CancellationToken ct)
    {
        const int buffer = 512 * 1024;
        try
        {
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, buffer, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buf = new byte[buffer]; long total = 0, size = input.Length; int last = -1, n;
            while ((n = await input.ReadAsync(buf, ct)) > 0)
            {
                await output.WriteAsync(buf.AsMemory(0, n), ct);
                total += n;
                int p = size == 0 ? 100 : (int)(total * 100 / size);
                if (p != last) { last = p; progress.Report(p); }
            }
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
