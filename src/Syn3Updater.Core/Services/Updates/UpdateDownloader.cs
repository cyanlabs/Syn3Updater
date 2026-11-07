using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>
/// Downloads one release file and proves it is the file the signed manifest promised: the size and the SHA-256 must both match, or the file is deleted and the
/// update refused. A partly downloaded file is continued where it stopped (the hash is checked over the whole file, so a resumed download is as trustworthy as a fresh one).
/// </summary>
public sealed class UpdateDownloader
{
    private readonly HttpClient _http;
    private readonly Func<string, long> _freeSpace;

    public UpdateDownloader(HttpClient http, Func<string, long>? freeSpace = null) { _http = http; _freeSpace = freeSpace ?? FreeSpaceOf; }

    /// <returns>The path of the verified file inside <paramref name="dir"/>.</returns>
    public async Task<string> DownloadAsync(ManifestAsset asset, string dir, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(dir);
        var final = Path.Combine(dir, asset.Name);
        var part = final + ".part";

        if (File.Exists(final))
        {
            if (await IsGoodAsync(final, asset, ct)) { progress?.Report(1); return final; }
            File.Delete(final);
        }
        // the file, the unpacked copy and some slack
        var need = asset.Size * 3 + 64L * 1024 * 1024;
        if (_freeSpace(dir) is var free && free >= 0 && free < need)
            throw new IOException($"Not enough free disk space for the update: {need / 1048576} MB needed, {free / 1048576} MB free");

        for (var attempt = 0; ; attempt++)
        {
            var start = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (start > asset.Size) { File.Delete(part); start = 0; }
            if (start < asset.Size && !await TryFetchAsync(asset, part, start, progress, ct))
            {
                // the server could not continue from there: start over once
                if (attempt == 0) { File.Delete(part); continue; }
                throw new HttpRequestException("the server refused the download");
            }
            break;
        }

        if (!await IsGoodAsync(part, asset, ct))
        {
            File.Delete(part);
            throw new InvalidDataException($"{asset.Name} does not match the signed manifest (wrong size or checksum): the download was discarded");
        }
        File.Move(part, final, overwrite: true);
        progress?.Report(1);
        return final;
    }

    /// <summary>False when the server cannot serve this range (the caller then starts again from nothing).</summary>
    private async Task<bool> TryFetchAsync(ManifestAsset asset, string part, long start, IProgress<double>? progress, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, asset.Url);
        if (start > 0) req.Headers.Range = new RangeHeaderValue(start, null);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable) return false;
        resp.EnsureSuccessStatusCode();
        // a redirect must not leave https
        if (resp.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps) throw new HttpRequestException("the download was redirected away from https");
        var resumed = resp.StatusCode == HttpStatusCode.PartialContent && start > 0;
        if (!resumed) start = 0;                                  // a plain 200 sends the whole file
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None);
        var buf = new byte[81920]; long done = start; int n;
        while ((n = await src.ReadAsync(buf, ct)) > 0)
        {
            done += n;
            if (done > asset.Size) throw new InvalidDataException($"{asset.Name} is larger than the signed manifest says");
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            progress?.Report((double)done / asset.Size);
        }
        return true;
    }

    private static async Task<bool> IsGoodAsync(string path, ManifestAsset asset, CancellationToken ct)
    {
        if (new FileInfo(path).Length != asset.Size) return false;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        return hash == asset.Sha256;
    }

    private static long FreeSpaceOf(string dir)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir)) ?? "/").AvailableFreeSpace; }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { return -1; }      // unknown: do not block on it
    }
}
