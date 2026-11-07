using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>A Sentry-style DSN: https://&lt;public key&gt;@host[/prefix]/&lt;project id&gt;. Works with Sentry, GlitchTip and Bugsink.</summary>
public sealed record CrashDsn(string PublicKey, Uri StoreUrl)
{
    public static CrashDsn? TryParse(string? dsn)
    {
        if (string.IsNullOrWhiteSpace(dsn) || !Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
        var key = Uri.UnescapeDataString(u.UserInfo.Split(':')[0]);
        var segments = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (key.Length == 0 || segments.Length == 0) return null;
        var project = segments[^1];
        var prefix = string.Join("/", segments[..^1]);
        var path = (prefix.Length > 0 ? "/" + prefix : "") + "/api/" + project + "/store/";
        return new CrashDsn(key, new UriBuilder(u.Scheme, u.Host, u.IsDefaultPort ? -1 : u.Port, path).Uri);
    }
}

/// <summary>What is remembered about a crash until the next start, when the user is asked whether to send it.</summary>
public sealed class CrashRecord
{
    public string Time { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Type { get; set; } = "";
    public string Message { get; set; } = "";
    public string StackTrace { get; set; } = "";
    public string Os { get; set; } = "";
    public string Arch { get; set; } = "";
    public string Version { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Vin { get; set; } = "";
}

/// <summary>
/// Remembers an unhandled error and, only after the user agrees on the next start, sends it as a Sentry-protocol event to the server
/// named by the DSN compiled into the build (none = nothing is ever offered or sent). Plain HTTP + System.Text.Json, like the API client.
/// </summary>
public static class CrashReporter
{
    private const string PendingFile = "crash-pending.json";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The VIN of the car whose log was last read, if any. Kept because it helps troubleshooting; it is only ever sent with the user's OK.</summary>
    public static volatile string? CurrentVin;

    /// <summary>The DSN compiled into this build, or SYN3_SENTRY_DSN for development. Null when there is none.</summary>
    public static CrashDsn? ConfiguredDsn
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("SYN3_SENTRY_DSN");
            return CrashDsn.TryParse(!string.IsNullOrWhiteSpace(env) ? env : ApiSecret.SentryDsn);
        }
    }

    public static string PendingPath(string dataDir) => Path.Combine(dataDir, PendingFile);

    /// <summary>Replaces the home folder (and with it the user name) by "~" so a report holds no personal paths.</summary>
    public static string Scrub(string? text, string? home = null)
    {
        if (string.IsNullOrEmpty(text)) return "";
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home) || home.Length < 3) return text;
        return text.Replace(home, "~", StringComparison.OrdinalIgnoreCase).Replace(home.Replace('\\', '/'), "~", StringComparison.OrdinalIgnoreCase);
    }

    public static CrashRecord Capture(string kind, object? error, string version, string channel, string os, string arch, string? home = null)
    {
        var ex = error as Exception ?? (error as AggregateException)?.GetBaseException();
        return new CrashRecord
        {
            Time = DateTime.UtcNow.ToString("o"), Kind = kind,
            Type = ex?.GetType().FullName ?? "UnknownError",
            Message = Scrub(ex?.Message ?? error?.ToString(), home),
            StackTrace = Scrub(ex?.StackTrace ?? error?.ToString(), home),
            Os = os, Arch = arch, Version = version, Channel = channel, Vin = CurrentVin ?? "",
        };
    }

    public static void SavePending(string dataDir, CrashRecord record)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(PendingPath(dataDir), JsonSerializer.Serialize(record, Json));
    }

    /// <summary>The crash waiting to be offered, or null. A damaged file is removed rather than reported.</summary>
    public static CrashRecord? LoadPending(string dataDir)
    {
        var f = PendingPath(dataDir);
        if (!File.Exists(f)) return null;
        try { return JsonSerializer.Deserialize<CrashRecord>(File.ReadAllText(f)); }
        catch (Exception e) when (e is JsonException or IOException) { ClearPending(dataDir); return null; }
    }

    public static void ClearPending(string dataDir)
    {
        try { File.Delete(PendingPath(dataDir)); } catch (IOException) { /* nothing to do */ }
    }

    // "   at Namespace.Type.Method(Args) in C:\path\File.cs:line 42"
    private static readonly Regex Frame = new(@"^\s*at (?<fn>.+?)(?: in (?<file>.+?):line (?<line>\d+))?\s*$", RegexOptions.Compiled);

    /// <summary>The Sentry event: exception with frames (oldest first), release, environment and tags. The VIN, when known, goes in "extra".</summary>
    public static string BuildEventJson(CrashRecord r, string? eventId = null)
    {
        var frames = new List<object>();
        foreach (var line in r.StackTrace.Split('\n'))
        {
            var m = Frame.Match(line.TrimEnd('\r'));
            if (!m.Success) continue;
            var f = new Dictionary<string, object> { ["function"] = m.Groups["fn"].Value, ["in_app"] = m.Groups["fn"].Value.StartsWith("Cyanlabs.", StringComparison.Ordinal) };
            if (m.Groups["file"].Success) { f["filename"] = m.Groups["file"].Value; f["lineno"] = int.Parse(m.Groups["line"].Value); }
            frames.Add(f);
        }
        frames.Reverse();                                               // .NET prints the newest call first, Sentry wants it last

        var extra = new Dictionary<string, string> { ["kind"] = r.Kind, ["architecture"] = r.Arch };
        if (!string.IsNullOrWhiteSpace(r.Vin)) extra["vin"] = r.Vin;
        var ev = new Dictionary<string, object>
        {
            ["event_id"] = eventId ?? Guid.NewGuid().ToString("N"),
            ["timestamp"] = r.Time,
            ["platform"] = "csharp",
            ["level"] = "fatal",
            ["release"] = "syn3updater@" + r.Version,
            ["environment"] = r.Channel.ToLowerInvariant(),
            ["server_name"] = "",                                       // never the machine name
            ["exception"] = new { values = new[] { new { type = r.Type, value = r.Message, stacktrace = new { frames } } } },
            ["tags"] = new Dictionary<string, string> { ["os"] = r.Os, ["channel"] = r.Channel, ["kind"] = r.Kind },
            ["extra"] = extra,
        };
        return JsonSerializer.Serialize(ev);
    }

    public static async Task<bool> SendAsync(HttpClient http, CrashDsn dsn, CrashRecord record, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, dsn.StoreUrl) { Content = new StringContent(BuildEventJson(record), Encoding.UTF8, "application/json") };
            req.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7, sentry_client=syn3updater/{record.Version}, sentry_key={dsn.PublicKey}");
            req.Headers.UserAgent.Add(new ProductInfoHeaderValue("Syn3Updater", record.Version.Split('-')[0]));
            using var resp = await http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException) { return false; }
    }
}
