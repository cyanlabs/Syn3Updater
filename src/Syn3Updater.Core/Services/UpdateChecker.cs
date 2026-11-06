using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

public record UpdateInfo(string Version, string Url, bool Prerelease);

/// <summary>Asks GitHub whether a newer release of Syn3 Updater exists. Only notifies: it never downloads or installs anything.</summary>
public class UpdateChecker
{
    public const string Repo = "CyanLabs/Syn3Updater";
    public const string ReleasesApi = $"https://api.github.com/repos/{Repo}/releases?per_page=20";
    private readonly HttpClient _http;
    private readonly string _url;

    public UpdateChecker(HttpClient http, string? url = null) { _http = http; _url = url ?? ReleasesApi; }

    /// <summary>
    /// The newest published release that is newer than <paramref name="current"/>, or null. Pre-releases are only offered
    /// to someone already running a pre-release. Returns null (never throws) when GitHub cannot be reached.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string current, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _url);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return Pick(await resp.Content.ReadAsStringAsync(cts.Token), current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public static UpdateInfo? Pick(string json, string current)
    {
        var running = SemVer.Parse(current);
        if (running == null) return null;
        UpdateInfo? best = null; SemVer? bestVer = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var pre = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            if (pre && !running.IsPrerelease) continue;
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var v = SemVer.Parse(tag);
            if (v == null || v.CompareTo(running) <= 0) continue;
            if (bestVer != null && v.CompareTo(bestVer) <= 0) continue;
            var url = rel.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            best = new UpdateInfo(tag!.TrimStart('v', 'V'), url ?? $"https://github.com/{Repo}/releases", pre);
            bestVer = v;
        }
        return best;
    }
}

/// <summary>Just enough of semantic versioning to order "3.0.0", "v3.0.0-test6" and "3.1.0-rc.2".</summary>
public class SemVer : IComparable<SemVer>
{
    private static readonly Regex Pattern = new(@"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:-([0-9A-Za-z.\-]+))?(?:\+.*)?$", RegexOptions.Compiled);
    public int[] Numbers { get; }
    public string Pre { get; }
    public bool IsPrerelease => Pre.Length > 0;
    private SemVer(int[] n, string pre) { Numbers = n; Pre = pre; }

    public static SemVer? Parse(string? s)
    {
        var m = Pattern.Match((s ?? "").Trim());
        if (!m.Success) return null;
        var n = new[] { 1, 2, 3, 4 }.Select(i => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0).ToArray();
        return new SemVer(n, m.Groups[5].Success ? m.Groups[5].Value : "");
    }

    public int CompareTo(SemVer? other)
    {
        if (other == null) return 1;
        for (var i = 0; i < Numbers.Length; i++) if (Numbers[i] != other.Numbers[i]) return Numbers[i].CompareTo(other.Numbers[i]);
        if (Pre == other.Pre) return 0;
        if (Pre.Length == 0) return 1;                  // a release is newer than any of its pre-releases
        if (other.Pre.Length == 0) return -1;
        // compare dot-separated identifiers: numbers numerically, text lexically ("test9" < "test10" handled by splitting digits)
        var a = Tokens(Pre); var b = Tokens(other.Pre);
        for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
        {
            var c = a[i] is int x && b[i] is int y ? x.CompareTo(y) : string.CompareOrdinal(a[i].ToString(), b[i].ToString());
            if (c != 0) return c;
        }
        return a.Count.CompareTo(b.Count);
    }

    private static List<object> Tokens(string s) =>
        Regex.Matches(s, @"\d+|[A-Za-z]+").Select(m => char.IsDigit(m.Value[0]) ? (object)int.Parse(m.Value) : m.Value).ToList();
}
