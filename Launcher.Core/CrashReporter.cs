using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Cyanlabs.Launcher.Updating
{
    /// <summary>A Sentry-style DSN: https://&lt;public key&gt;@host[/prefix]/&lt;project id&gt;. Works with Sentry, GlitchTip and Bugsink.</summary>
    public sealed class CrashDsn
    {
        public string PublicKey { get; private set; }
        public Uri StoreUrl { get; private set; }

        public static CrashDsn TryParse(string dsn)
        {
            if (string.IsNullOrWhiteSpace(dsn) || !Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out Uri u)) return null;
            if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return null;
            string key = Uri.UnescapeDataString(u.UserInfo.Split(':')[0]);
            string[] segments = u.AbsolutePath.Trim('/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (key.Length == 0 || segments.Length == 0) return null;
            string project = segments[segments.Length - 1];
            string prefix = string.Join("/", segments, 0, segments.Length - 1);
            string path = (prefix.Length > 0 ? "/" + prefix : "") + "/api/" + project + "/store/";
            return new CrashDsn { PublicKey = key, StoreUrl = new UriBuilder(u.Scheme, u.Host, u.IsDefaultPort ? -1 : u.Port, path).Uri };
        }
    }

    /// <summary>
    /// Sends a launcher problem (an unhandled error or a failed update) as a Sentry-protocol event to the server named by the DSN compiled into the build
    /// (none = nothing is sent). A launcher problem holds no personal data: an error, the launcher version and the Windows build. Home folders are still scrubbed from paths.
    /// </summary>
    public static class CrashReporter
    {
        /// <summary>The DSN compiled into this build, or SYN3_SENTRY_DSN for development. Null when there is none.</summary>
        public static CrashDsn ConfiguredDsn
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("SYN3_SENTRY_DSN");
                return CrashDsn.TryParse(!string.IsNullOrWhiteSpace(env) ? env : LauncherSecret.SentryDsn);
            }
        }

        /// <summary>Replaces the home folder (and with it the user name) by "~".</summary>
        public static string Scrub(string text, string home = null)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (home == null) home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home) || home.Length < 3) return text;
            text = Regex.Replace(text, Regex.Escape(home), "~", RegexOptions.IgnoreCase);
            return Regex.Replace(text, Regex.Escape(home.Replace('\\', '/')), "~", RegexOptions.IgnoreCase);
        }

        // "   at Namespace.Type.Method(Args) in C:\path\File.cs:line 42"
        private static readonly Regex Frame = new Regex(@"^\s*at (?<fn>.+?)(?: in (?<file>.+?):line (?<line>\d+))?\s*$", RegexOptions.Compiled);

        /// <param name="kind">"update-failed" or "unhandled-exception".</param>
        public static string BuildEventJson(string kind, string type, string message, string stackTrace, string version, string os, string arch, string eventId = null)
        {
            List<object> frames = new List<object>();
            foreach (string line in Scrub(stackTrace).Split('\n'))
            {
                Match m = Frame.Match(line.TrimEnd('\r'));
                if (!m.Success) continue;
                Dictionary<string, object> f = new Dictionary<string, object> { ["function"] = m.Groups["fn"].Value, ["in_app"] = m.Groups["fn"].Value.StartsWith("Cyanlabs.", StringComparison.Ordinal) };
                if (m.Groups["file"].Success) { f["filename"] = m.Groups["file"].Value; f["lineno"] = int.Parse(m.Groups["line"].Value); }
                frames.Add(f);
            }
            frames.Reverse(); // .NET prints the newest call first, Sentry wants it last

            Dictionary<string, object> ev = new Dictionary<string, object>
            {
                ["event_id"] = eventId ?? Guid.NewGuid().ToString("N"),
                ["timestamp"] = DateTime.UtcNow.ToString("o"),
                ["platform"] = "csharp",
                ["level"] = "error",
                ["release"] = "syn3launcher@" + version,
                ["server_name"] = "", // never the machine name
                ["exception"] = new { values = new[] { new { type, value = Scrub(message), stacktrace = new { frames } } } },
                ["tags"] = new Dictionary<string, string> { ["os"] = os, ["component"] = "launcher", ["kind"] = kind },
                ["extra"] = new Dictionary<string, string> { ["architecture"] = arch },
            };
            return JsonConvert.SerializeObject(ev);
        }

        public static async Task<bool> SendAsync(HttpClient http, CrashDsn dsn, string eventJson, string version, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                using (HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, dsn.StoreUrl) { Content = new StringContent(eventJson, Encoding.UTF8, "application/json") })
                {
                    req.Headers.TryAddWithoutValidation("X-Sentry-Auth", "Sentry sentry_version=7, sentry_client=syn3launcher/" + version + ", sentry_key=" + dsn.PublicKey);
                    req.Headers.TryAddWithoutValidation("User-Agent", "Syn3Launcher/" + version);
                    using (HttpResponseMessage resp = await http.SendAsync(req, ct).ConfigureAwait(false)) return resp.IsSuccessStatusCode;
                }
            }
            catch (Exception e) when (e is HttpRequestException || e is OperationCanceledException) { return false; }
        }
    }
}
