using System.Reflection;
using System.Runtime.InteropServices;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Application-wide services (a tiny hand-rolled container; there is exactly one of each).</summary>
public static class Ctx
{
    public static AppPaths Paths { get; private set; } = null!;
    public static SettingsStore Store { get; private set; } = null!;
    public static AppLog Log { get; } = new();
    public static Localizer Loc { get; } = new();
    public static HttpClient Http { get; private set; } = null!;
    public static ApiClient Api { get; private set; } = null!;
    public static FileService Files { get; private set; } = null!;
    public static IDriveService Drives { get; private set; } = null!;
    public static UpdateRunner Runner { get; private set; } = null!;

    public static MainSettings Main => Store.Main;
    public static ProfileSettings Profile => Store.Profile;
    public static bool ModeForced => Profile.InstallMode != "autodetect" && !string.IsNullOrEmpty(Profile.InstallMode);
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "3.0.0";
    public static string Branch => "Stable";

    public static void Init(string? dataDirOverride = null, HttpMessageHandler? handler = null)
    {
        Paths = new AppPaths(dataDirOverride ?? Environment.GetEnvironmentVariable("SYN3_DATA_DIR"));
        Store = new SettingsStore(Paths);
        Store.Load();
        Store.Save();

        Http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxConnectionsPerServer = 8 })
        { Timeout = TimeSpan.FromMinutes(5) };
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:70.0) Gecko/20100101 Firefox/86.0");

        Api = new ApiClient(Http, ApiClient.ResolveToken, () => Main.LicenseKey ?? "");
        Files = new FileService(Http);
        Drives = DriveServiceFactory.Create();
        Runner = new UpdateRunner(Drives, Files, Log);

        Loc.SetLanguage(Main.Lang);
        Main.Lang = Loc.Code;
        Log.Debug($"Syn3 Updater {Version} ({Branch}) is starting on {OsName()}");
    }

    /// <summary>Swaps in a random browser User-Agent from the API, as the original does (best effort).</summary>
    public static async Task RandomizeUserAgentAsync()
    {
        var ua = await Api.RandomUserAgent();
        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua);
    }

    public static string OsName()
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/etc/os-release"))
            {
                var line = File.ReadAllLines("/etc/os-release").FirstOrDefault(l => l.StartsWith("PRETTY_NAME="));
                if (line != null) return line["PRETTY_NAME=".Length..].Trim('"') + " (" + RuntimeInformation.OSArchitecture + ")";
            }
        }
        catch (IOException) { }
        return RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
    }

    public static string T(string key) => Loc[key];
    public static string T(string key, params object[] args) => Loc.Format(key, args);

    public static void Save() => Store.Save();
}
