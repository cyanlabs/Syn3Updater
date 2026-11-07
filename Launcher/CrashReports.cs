using System;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using Cyanlabs.Launcher.Updating;

namespace Cyanlabs.Launcher
{
    /// <summary>Reports a launcher problem to the CyanLabs crash server (when this build has a DSN). Never throws and never waits long: the launcher must still start the app.</summary>
    internal static class CrashReports
    {
        public static void Report(string kind, Exception ex) =>
            Report(kind, ex?.GetType().FullName ?? "UnknownError", ex?.Message ?? "", ex?.ToString() ?? "");

        public static void Report(string kind, string type, string message, string stackTrace)
        {
            try
            {
                CrashDsn dsn = CrashReporter.ConfiguredDsn;
                if (dsn == null) return;
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                string version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                string json = CrashReporter.BuildEventJson(kind, type, message, stackTrace, version, "Windows build " + OsInfo.BuildNumber(), Compatibility.ArchLabel());
                // on a worker thread, so a UI thread that is going down cannot deadlock on it; give up after 6 seconds
                Task.Run(async () =>
                {
                    using (HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }) await CrashReporter.SendAsync(http, dsn, json, version).ConfigureAwait(false);
                }).Wait(TimeSpan.FromSeconds(6));
            }
            catch
            {
                // reporting is best effort
            }
        }
    }
}
