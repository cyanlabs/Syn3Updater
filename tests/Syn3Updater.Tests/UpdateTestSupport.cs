using System.Security.Cryptography;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;

namespace Syn3Updater.Tests;

/// <summary>An HttpClient handler answered by a function, so update tests never touch the network.</summary>
internal sealed class FuncHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _f;
    public List<HttpRequestMessage> Requests { get; } = new();
    public FuncHandler(Func<HttpRequestMessage, HttpResponseMessage> f) => _f = f;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var r = _f(request); r.RequestMessage = request;
        return Task.FromResult(r);
    }
}

internal static class UpdateTestSupport
{
    public static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    public static ManifestAsset Asset(byte[] content, string name = "Syn3Updater-3.1.0-linux-x64.tar.gz", long? size = null, string? sha = null) =>
        new(UpdateOs.Linux, UpdateArch.X64, AssetKind.TarGz, name,
            $"https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0/{name}", size ?? content.Length, sha ?? Sha(content));

    /// <summary>A .tar.gz made from (name, content, unix mode) entries; a name ending in '/' is a folder, content starting with "->" a symlink.</summary>
    public static string MakeTarGz(string path, params (string Name, string Content, int Mode)[] entries)
    {
        using var fs = File.Create(path);
        using var gz = new GZipOutputStream(fs);
        using var tar = new TarOutputStream(gz, Encoding.UTF8);
        foreach (var (name, content, mode) in entries)
        {
            var e = TarEntry.CreateTarEntry(name);
            e.TarHeader.Mode = mode;
            if (name.EndsWith('/')) { e.TarHeader.TypeFlag = TarHeader.LF_DIR; tar.PutNextEntry(e); tar.CloseEntry(); continue; }
            if (content.StartsWith("->")) { e.TarHeader.TypeFlag = TarHeader.LF_SYMLINK; e.TarHeader.LinkName = content[2..]; tar.PutNextEntry(e); tar.CloseEntry(); continue; }
            var bytes = Encoding.UTF8.GetBytes(content);
            e.Size = bytes.Length; tar.PutNextEntry(e); tar.Write(bytes, 0, bytes.Length); tar.CloseEntry();
        }
        return path;
    }

    public static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
}
