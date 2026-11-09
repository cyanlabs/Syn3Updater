using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

public static class ApiEndpoints
{
    public const string GraphQl = "https://syn3updater.cyanlabs.net/graphql";
    public const string ApiBase = "https://api.cyanlabs.net/";
    /// <summary>Where CyanLabs says whether its servers are up; named in the "cannot reach the backend" messages.</summary>
    public const string StatusPage = "https://status.cyanlabs.net";
    public const string ChangelogUrl = ApiBase + "app/syn3updater/githubchangelog/json";
    public const string LogPost = ApiBase + "Syn3Updater/logs/post.php";
    public const string LogUrl = "https://cyanlabs.net/syn3-updater-log/?uuid=";
    public const string CrashLogPost = ApiBase + "Syn3Updater/crash-logs/post.php";
    public const string CrashLogUrl = "https://cyanlabs.net/syn3-updater-crash-log/?uuid=";
    public const string AsBuiltPost = ApiBase + "Syn3Updater/apim-asbuilt-decode/";
    public const string AsBuiltOutput = "https://cyanlabs.net/fhub/sync3/apim-asbuilt-decoder/?filename=";
    public const string DocsBase = "https://cyanlabs.net/docs/syn3updater/";
    /// <summary>Instructions page for an install mode: autoinstall, reformat or downgrade.</summary>
    public static string InstallDocsUrl(string mode) => DocsBase + "installmodes/" + mode.Trim().ToLowerInvariant() + "/";

    public const int BlacklistedVersion = 3419274;
    public const int ReformatVersion = 3200000;
}

public class ApiException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Talks to the CyanLabs GraphQL backend. The bearer token is not part of the source: it is generated into
/// the build from the git-ignored secrets/api-token.txt (or SYN3_API_TOKEN).
/// </summary>
public class ApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString, // the API sends ids and sizes as strings
    };
    private readonly HttpClient _http;
    private int _reachable = -1;                      // -1 = nothing asked yet, 0 = the backend did not answer, 1 = it did
    private readonly Func<string?> _token;
    private readonly Func<string> _licenseKey;

    public ApiClient(HttpClient http, Func<string?> tokenProvider, Func<string> licenseKeyProvider)
    {
        _http = http; _token = tokenProvider; _licenseKey = licenseKeyProvider;
    }

    /// <summary>
    /// Raised, from whatever thread the request ran on, when the answer to "can the backend be reached?" changes: false after a network error, a timeout or a 5xx answer,
    /// true after any other answer (a 400 or 401 means the server is there, just unhappy with the request). Only changes are reported, not every request.
    /// </summary>
    public event Action<bool>? ReachabilityChanged;

    private void Report(bool reachable)
    {
        if (Interlocked.Exchange(ref _reachable, reachable ? 1 : 0) != (reachable ? 1 : 0)) ReachabilityChanged?.Invoke(reachable);
    }

    /// <summary>A tiny request to find out whether the backend answers now. Never throws; <see cref="ReachabilityChanged"/> reports the result like any other request.</summary>
    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try { await GetUserAgents(ct); return true; }
        catch (Exception e) when (e is ApiException or TaskCanceledException or HttpRequestException) { return false; }
    }

    /// <summary>The compiled-in token (see secrets/README.md). SYN3_API_TOKEN at runtime overrides it for development.</summary>
    public static string? ResolveToken()
    {
        var env = Environment.GetEnvironmentVariable("SYN3_API_TOKEN");
        return !string.IsNullOrWhiteSpace(env) ? env : (ApiSecret.Token.Length > 0 ? ApiSecret.Token : null);
    }

    public static bool HasToken => ResolveToken() != null;

    /// <summary>Escapes a value for use inside a GraphQL string literal.</summary>
    public static string Esc(string? s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ");

    public async Task<T> QueryAsync<T>(string query, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.GraphQl)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json")
        };
        var token = _token();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var resp = await _http.SendAsync(req, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            Report((int)resp.StatusCode < 500);
            if (!resp.IsSuccessStatusCode)
                throw new ApiException($"API returned {(int)resp.StatusCode} {resp.ReasonPhrase}" +
                    (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? " - check the API token in Settings (or SYN3_API_TOKEN)." : ""));
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errs) && !doc.RootElement.TryGetProperty("data", out _))
                throw new ApiException("API error: " + errs[0].GetProperty("message").GetString());
            return doc.RootElement.GetProperty("data").Deserialize<T>(JsonOpts)
                   ?? throw new ApiException("API returned empty data");
        }
        catch (HttpRequestException e) { Report(false); throw new ApiException("Unable to reach the CyanLabs API: " + e.Message, e); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { Report(false); throw; }                       // the HttpClient timeout, not the caller giving up
        catch (JsonException e) { throw new ApiException("Unexpected API response: " + e.Message, e); }
    }

    /// <summary>The minimum versions the backend still allows to run (see UpdatePolicy). Rows are public to the app's token and hold nothing sensitive.</summary>
    public Task<UpdatePolicyRoot> GetUpdatePolicy(CancellationToken ct = default) => QueryAsync<UpdatePolicyRoot>(
        "{ update_policy(limit: -1) { channel enabled min_version_all min_version_windows min_version_linux min_version_macos message } }", ct);

    // ---- Queries (same shapes as upstream) ----
    public Task<ReleasesRoot> GetReleases(string region, CancellationToken ct = default) => QueryAsync<ReleasesRoot>($$$"""
        { releases(sort: "-name", limit: -1, filter: {
            status: { _in: ["published", "private"] },
            key: { _in: ["public", "v2", "{{{Esc(_licenseKey())}}}"] },
            regions: { _contains: "{{{Esc(region)}}}" } })
          { name, notes, regions, version, feedbackurl } }
        """, ct);

    public Task<ReleasesRoot> GetMapReleases(string region, string compat, CancellationToken ct = default)
    {
        var key = _licenseKey();
        var license = key.Length > 10 ? $$$"""{licensekeys:{_contains:"{{{Esc(key)}}}"}},""" : "";
        return QueryAsync<ReleasesRoot>($$$"""
            { map_releases(sort: "-name", limit: -1, filter: {_and:
                [{ _or: [ {licensekeys: { _null: true}}, {licensekeys: { _empty: true}}, {{{license}}} ],
                   status: { _in: ["published", "private"] }, regions: {_in: "{{{Esc(region)}}}"},
                   compatibility: {_contains: "{{{Esc(compat)}}}"} }] })
              { name, notes, regions, esn } }
            """, ct);
    }

    public Task<ReleasesRoot> GetReleaseIvsus(string release, string navType, CancellationToken ct = default) => QueryAsync<ReleasesRoot>($$$"""
        { releases(limit: -1, filter: {name: {_eq: "{{{Esc(release)}}}"}}) { name
            ivsus { ivsu(filter: {navtype: { _in: ["{{{Esc(navType)}}}","all"]}})
              { id, name, type, version, notes, url, md5, filesize, regions } } } }
        """, ct);

    public Task<ReleasesRoot> GetMapReleaseIvsus(string mapRelease, CancellationToken ct = default) => QueryAsync<ReleasesRoot>($$$"""
        { map_releases(limit: -1, filter: {name: {_eq: "{{{Esc(mapRelease)}}}"}}) { name
            ivsus { map_ivsu { id, name, type, version, notes, url, md5, filesize, regions, source } } } }
        """, ct);

    public async Task<List<Notice>> GetNotices(CancellationToken ct = default) =>
        (await QueryAsync<NoticesRoot>("""{ notices(limit: -1, filter: {enabled: {_eq: true}}) { id, title, notice, date_created, date_updated, important } }""", ct)).Notices ?? new();

    public async Task<List<UserAgent>> GetUserAgents(CancellationToken ct = default) =>
        (await QueryAsync<UserAgentRoot>("{ useragents { useragent,min,max } }", ct)).UserAgents ?? new();

    public async Task<List<My20Model>> GetMy20Models(CancellationToken ct = default) =>
        (await QueryAsync<My20ModelsRoot>("{ my20models { model } }", ct)).Models ?? new();

    /// <summary>
    /// Every SYNC application version the backend knows (APPS packages), newest first, without duplicates.
    /// Used to suggest versions in Settings; users can still type a version that is not in the list.
    /// </summary>
    public async Task<List<string>> GetAppVersions(CancellationToken ct = default)
    {
        var r = await QueryAsync<IvsuRoot>("""{ ivsu(sort: "-name", limit: -1, filter: {type: {_eq: "APPS"}}) { version } }""", ct);
        return (r.Ivsus ?? new())
            .Select(i => i.Version?.Trim()).Where(v => !string.IsNullOrEmpty(v)).Select(v => v!)
            .Distinct()
            .OrderByDescending(v => v, VersionOrder.Instance)
            .ToList();
    }

    /// <summary>Compares "3.4.26057"-style versions one dotted segment at a time, as numbers ("3.4.9999" is older than "3.4.23088").</summary>
    public sealed class VersionOrder : IComparer<string>
    {
        public static readonly VersionOrder Instance = new();
        public int Compare(string? a, string? b)
        {
            var x = Segments(a); var y = Segments(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                var c = (i < x.Length ? x[i] : 0).CompareTo(i < y.Length ? y[i] : 0);
                if (c != 0) return c;
            }
            return string.CompareOrdinal(a, b);                          // same numbers, different text: keep a stable order
        }
        private static long[] Segments(string? v) =>
            (v ?? "").Split('.').Select(s => long.TryParse(new string(s.Where(char.IsDigit).ToArray()), out var n) ? n : 0).ToArray();
    }

    public async Task<Ivsu> GetSpecialPackage(string name, CancellationToken ct = default)
    {
        var r = await QueryAsync<IvsuRoot>($$$"""
            { ivsu(limit: 1, filter: {specialpackage: {_eq: "{{{Esc(name)}}}"}})
              { id, name, type, regions, md5, url, version, filesize, notes, navtype, specialpackage } }
            """, ct);
        var first = r.Ivsus?.FirstOrDefault() ?? throw new ApiException($"Special package '{name}' was not found");
        var ivsu = first.ToIvsu();
        ivsu.FileName = ivsu.FileName.Replace("?dl=1", "");
        ivsu.SpecialPackage ??= name;                                    // looked up by this name, so it is that special package even if the backend left the field empty
        return ivsu;
    }

    /// <summary>Looks up the friendly app version (e.g. 3.4.20282) for an "Embedded Consumer Operating System Part Number".</summary>
    public async Task<string?> LookupVersion(string ivsuName, CancellationToken ct = default) =>
        (await QueryAsync<IvsuRoot>($$$"""{ ivsu(limit: 1, filter: {name: {_eq: "{{{Esc(ivsuName)}}}"}}) { version } }""", ct)).Ivsus?.FirstOrDefault()?.Version;

    public async Task<List<Changelog>> GetChangelog(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiEndpoints.ChangelogUrl);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return (await JsonSerializer.DeserializeAsync<ChangelogRoot>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct))?.Data ?? new();
    }

    public async Task<string?> UploadLog(string computer, string contents, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.LogPost)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["computername"] = computer, ["contents"] = contents })
        };
        var token = _token();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("uuid", out var u) ? ApiEndpoints.LogUrl + u.GetString() : null;
    }

    public async Task<string?> UploadAsBuilt(string xml, string apim, bool nav, int size, string vin, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiEndpoints.AsBuiltPost)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
                { ["xml"] = xml, ["apim"] = apim, ["nav"] = nav.ToString(), ["size"] = size.ToString(), ["vin"] = vin })
        };
        var token = _token();
        if (!string.IsNullOrWhiteSpace(token)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return doc.RootElement.TryGetProperty("filename", out var f) ? ApiEndpoints.AsBuiltOutput + f.GetString() : null;
    }

    /// <summary>Picks a random browser User-Agent from the API list (as upstream does), with a safe fallback.</summary>
    public async Task<string> RandomUserAgent(CancellationToken ct = default)
    {
        try
        {
            var uas = await GetUserAgents(ct);
            var rand = new Random();
            var list = uas.Select(u => u.Value.Replace("[PLACEHOLDER]", rand.Next(u.Min, Math.Max(u.Min + 1, u.Max)).ToString())).ToList();
            if (list.Count > 0) return list[rand.Next(list.Count)];
        }
        catch (Exception) { /* fall through */ }
        return "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:70.0) Gecko/20100101 Firefox/86.0";
    }
}
