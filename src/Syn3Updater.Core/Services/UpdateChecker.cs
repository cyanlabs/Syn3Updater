using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>A release the app could move to. ManifestUrl and SignatureUrl are the release's signed manifest.json and manifest.json.minisig, when it has them (older releases do not).</summary>
public record UpdateInfo(string Version, string Url, bool Prerelease, bool Switch = false, string? ManifestUrl = null, string? SignatureUrl = null);

/// <summary>Where a build comes from. Stable = tested releases, Beta = early releases, Nightly = automatic builds of the latest code.</summary>
public enum UpdateChannel { Stable, Beta, Nightly }

public static class Channels
{
    /// <summary>A build's own channel is read from its version: 3.0.0 is Stable, 3.1.0-beta.2 / 3.0.0-test7 are Beta, 3.1.0-nightly.20261108.5 is Nightly.</summary>
    public static UpdateChannel Of(string? version)
    {
        var v = SemVer.Parse(version);
        if (v == null || !v.IsPrerelease) return UpdateChannel.Stable;
        return v.Pre.StartsWith("nightly", StringComparison.OrdinalIgnoreCase) ? UpdateChannel.Nightly : UpdateChannel.Beta;
    }

    /// <summary>The channel saved in settings; anything missing or unknown means "the channel of the build that is running".</summary>
    public static UpdateChannel Parse(string? saved, string runningVersion) =>
        Enum.TryParse<UpdateChannel>(saved, ignoreCase: true, out var c) && Enum.IsDefined(c) ? c : Of(runningVersion);
}

/// <summary>Asks GitHub whether a newer release of Syn3 Updater exists. Only notifies: it never downloads or installs anything.</summary>
public class UpdateChecker
{
    public const string Repo = "CyanLabs/Syn3Updater";
    public const string ReleasesApi = $"https://api.github.com/repos/{Repo}/releases?per_page=30";
    private readonly HttpClient _http;
    private readonly string? _url;

    public UpdateChecker(HttpClient http, string? url = null) { _http = http; _url = url; }

    /// <summary>
    /// The build to offer on <paramref name="channel"/> (default: the channel of the running build), or null. A newer build in the same
    /// channel is an update; picking a different channel offers that channel's newest build even when it is older (a "switch", as in v2).
    /// Returns null (never throws) when GitHub cannot be reached.
    /// </summary>
    public async Task<UpdateInfo?> CheckAsync(string current, UpdateChannel? channel = null, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, _url ?? ReleasesApi);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await _http.SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            return Pick(await resp.Content.ReadAsStringAsync(cts.Token), current, channel);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return null; }
    }

    public static UpdateInfo? Pick(string json, string current, UpdateChannel? channel = null)
    {
        var running = SemVer.Parse(current);
        if (running == null) return null;
        var own = Channels.Of(current);
        var want = channel ?? own;
        UpdateInfo? best = null; SemVer? bestVer = null;
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            var pre = rel.TryGetProperty("prerelease", out var p) && p.GetBoolean();
            var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var title = rel.TryGetProperty("name", out var n) ? n.GetString() : null;
            var version = VersionOf(tag, title, pre);               // nightlies sit under a rolling "nightly" tag, so their version is in the title
            var v = SemVer.Parse(version);
            if (v == null) continue;
            var kind = Channels.Of(version);
            if (want == UpdateChannel.Stable ? pre || kind != UpdateChannel.Stable : want == UpdateChannel.Beta ? kind == UpdateChannel.Nightly : kind != UpdateChannel.Nightly) continue;
            if (bestVer != null && v.CompareTo(bestVer) <= 0) continue;
            var url = rel.TryGetProperty("html_url", out var u) ? u.GetString() : null;
            string? manifest = null, signature = null;
            if (rel.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                foreach (var a in assets.EnumerateArray())
                {
                    var an = a.TryGetProperty("name", out var nn) ? nn.GetString() : null;
                    var au = a.TryGetProperty("browser_download_url", out var uu) ? uu.GetString() : null;
                    if (an == "manifest.json") manifest = au; else if (an == "manifest.json.minisig") signature = au;
                }
            best = new UpdateInfo(version!.TrimStart('v', 'V'), url ?? $"https://github.com/{Repo}/releases", pre, false, manifest, manifest != null ? signature : null);
            bestVer = v;
        }
        if (best == null || bestVer == null) return null;
        var cmp = bestVer.CompareTo(running);
        if (cmp > 0) return best;
        return want != own && cmp != 0 ? best with { Switch = true } : null;
    }

    private static readonly Regex VersionInTitle = new(@"\d+\.\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.\-]+)?", RegexOptions.Compiled);
    /// <summary>
    /// The version a release really is. Normally its tag. Two cases use the title instead: nightlies (the tag is just "nightly"), and a release flagged
    /// pre-release that sits under a bare numeric tag (2.99.99.5, 3.0.0.0: the v2 Launcher only accepts those) while its title names the real pre-release,
    /// "Syn3 Updater 3.0.0-test9". Without that, the bare tag would look like a different (and for 3.0.0.0 a newer) version than the build it contains.
    /// A release that is NOT flagged pre-release always keeps its tag, so the final 3.0.0.0 still beats every test build.
    /// </summary>
    private static string? VersionOf(string? tag, string? title, bool prerelease)
    {
        var fromTitle = title != null && VersionInTitle.Match(title) is { Success: true } m ? m.Value : null;
        var t = SemVer.Parse(tag);
        if (t == null) return fromTitle;
        if (prerelease && !t.IsPrerelease && SemVer.Parse(fromTitle) is { IsPrerelease: true }) return fromTitle;
        return tag;
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
