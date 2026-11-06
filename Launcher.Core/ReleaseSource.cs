using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace Cyanlabs.Launcher.Core
{
    public class RateLimitedException : Exception
    {
        public RateLimitedException() : base("GitHub's request limit was reached.") { }
    }

    public class NetworkException : Exception
    {
        public NetworkException(string message, Exception inner) : base(message, inner) { }
    }

    public interface IReleaseSource
    {
        Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct);
        Task DownloadFileAsync(string url, string destinationPath, IProgress<KeyValuePair<long, long>> progress, CancellationToken ct);
        Task<string> DownloadStringAsync(string url, CancellationToken ct);
    }

    /// <summary>Reads releases from the GitHub REST API (plain HTTPS, no extra libraries).</summary>
    public sealed class GitHubReleaseSource : IReleaseSource, IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _owner;
        private readonly string _repo;

        public GitHubReleaseSource(string owner, string repo, HttpMessageHandler handler = null)
        {
            _owner = owner; _repo = repo;
            _http = handler == null ? new HttpClient() : new HttpClient(handler);
            _http.Timeout = TimeSpan.FromMinutes(30);
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("CyanLabs-Launcher/3");
            _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        }

        public async Task<IReadOnlyList<ReleaseInfo>> GetReleasesAsync(CancellationToken ct)
        {
            var url = $"https://api.github.com/repos/{_owner}/{_repo}/releases?per_page=30";
            try
            {
                using (var response = await _http.GetAsync(url, ct).ConfigureAwait(false))
                {
                    if (response.StatusCode == (HttpStatusCode)429 || response.StatusCode == HttpStatusCode.Forbidden && IsExhausted(response))
                        throw new RateLimitedException();
                    response.EnsureSuccessStatusCode();
                    return Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
            }
            catch (RateLimitedException) { throw; }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException || e is WebException)
            {
                throw new NetworkException("Could not reach GitHub: " + e.Message, e);
            }
        }

        public async Task DownloadFileAsync(string url, string destinationPath, IProgress<KeyValuePair<long, long>> progress, CancellationToken ct)
        {
            try
            {
                using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength ?? -1;
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = File.Create(destinationPath))
                    {
                        var buffer = new byte[81920]; long done = 0; int read;
                        while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct).ConfigureAwait(false)) > 0)
                        {
                            await output.WriteAsync(buffer, 0, read, ct).ConfigureAwait(false);
                            done += read;
                            progress?.Report(new KeyValuePair<long, long>(done, total));
                        }
                    }
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException || e is WebException)
            {
                throw new NetworkException("The download failed: " + e.Message, e);
            }
        }

        public async Task<string> DownloadStringAsync(string url, CancellationToken ct)
        {
            try { return await _http.GetStringAsync(url).ConfigureAwait(false); }
            catch (Exception e) when (e is HttpRequestException || e is TaskCanceledException || e is IOException || e is WebException)
            {
                throw new NetworkException("The download failed: " + e.Message, e);
            }
        }

        public static IReadOnlyList<ReleaseInfo> Parse(string json)
        {
            var list = new List<ReleaseInfo>();
            foreach (var r in JArray.Parse(json).OfType<JObject>())
            {
                var info = new ReleaseInfo
                {
                    TagName = (string)r["tag_name"] ?? "",
                    Prerelease = (bool?)r["prerelease"] ?? false,
                    Draft = (bool?)r["draft"] ?? false
                };
                foreach (var a in (r["assets"] as JArray ?? new JArray()).OfType<JObject>())
                    info.Assets.Add(new ReleaseAsset
                    {
                        Name = (string)a["name"] ?? "",
                        ContentType = (string)a["content_type"] ?? "",
                        DownloadUrl = (string)a["browser_download_url"] ?? "",
                        Size = (long?)a["size"] ?? 0,
                        Digest = (string)a["digest"]
                    });
                list.Add(info);
            }
            return list;
        }

        private static bool IsExhausted(HttpResponseMessage response) =>
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var v) && v.FirstOrDefault() == "0";

        public void Dispose() => _http.Dispose();
    }
}
