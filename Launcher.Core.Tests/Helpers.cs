using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cyanlabs.Launcher.Core;

namespace Cyanlabs.Launcher.Core.Tests
{
    public sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("launchertest").FullName;
        public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    public static class Zips
    {
        /// <summary>Builds a zip from (path, content) pairs.</summary>
        public static byte[] Make(params (string Path, string Content)[] files)
        {
            using var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (path, content) in files)
                {
                    var e = zip.CreateEntry(path);
                    using var w = new StreamWriter(e.Open()); w.Write(content);
                }
            return ms.ToArray();
        }
    }

    public sealed class FakeSource : IReleaseSource
    {
        public List<ReleaseInfo> Releases { get; } = new List<ReleaseInfo>();
        public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>();
        public Exception ListError { get; set; }
        public Exception DownloadError { get; set; }
        public int Downloads { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
        {
            if (ListError != null) throw ListError;
            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(Releases);
        }

        public Task DownloadFileAsync(string url, string destinationPath, IProgress<KeyValuePair<long, long>> progress, CancellationToken ct)
        {
            Downloads++;
            if (DownloadError != null) throw DownloadError;
            File.WriteAllBytes(destinationPath, Files[url]);
            progress?.Report(new KeyValuePair<long, long>(Files[url].Length, Files[url].Length));
            return Task.CompletedTask;
        }

        public Task<string> DownloadStringAsync(string url, CancellationToken ct) => Task.FromResult(System.Text.Encoding.UTF8.GetString(Files[url]));

        /// <summary>Publishes a release with one zip asset (and optionally a SHA256SUMS file).</summary>
        public ReleaseInfo Publish(string tag, byte[] zip, string assetName = null, bool prerelease = false, bool sums = false, string contentType = "application/x-zip-compressed")
        {
            assetName ??= tag.TrimStart('v') + ".zip";
            var url = "https://dl/" + tag + "/" + assetName;
            Files[url] = zip;
            var r = new ReleaseInfo { TagName = tag, Prerelease = prerelease };
            r.Assets.Add(new ReleaseAsset { Name = assetName, ContentType = contentType, DownloadUrl = url, Size = zip.Length });
            if (sums)
            {
                var sumsUrl = "https://dl/" + tag + "/SHA256SUMS";
                Files[sumsUrl] = System.Text.Encoding.UTF8.GetBytes(Hash(zip) + "  " + assetName + "\n");
                r.Assets.Add(new ReleaseAsset { Name = "SHA256SUMS", ContentType = "text/plain", DownloadUrl = sumsUrl });
            }
            Releases.Add(r);
            return r;
        }

        public static string Hash(byte[] data)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }

    /// <summary>Wraps the real file system and makes chosen operations fail, to prove the rollback.</summary>
    public sealed class FaultyFileSystem : IFileSystem
    {
        private readonly RealFileSystem _real = new RealFileSystem();
        public Func<string, string, bool> FailMove { get; set; } = (_, _) => false;
        public int MoveFailuresLeft { get; set; }
        public int MoveCalls { get; private set; }

        public bool FileExists(string path) => _real.FileExists(path);
        public bool DirectoryExists(string path) => _real.DirectoryExists(path);
        public void CreateDirectory(string path) => _real.CreateDirectory(path);
        public void DeleteFile(string path) => _real.DeleteFile(path);
        public void DeleteDirectory(string path) => _real.DeleteDirectory(path);

        public void MoveFile(string source, string destination, bool overwrite)
        {
            MoveCalls++;
            if (MoveFailuresLeft > 0) { MoveFailuresLeft--; throw new IOException("locked (simulated)"); }
            if (FailMove(source, destination)) throw new IOException("cannot move " + Path.GetFileName(source) + " (simulated)");
            _real.MoveFile(source, destination, overwrite);
        }
    }
}
