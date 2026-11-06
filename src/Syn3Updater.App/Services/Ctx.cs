using System.Reflection;
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
    public static DiagnosticsService Diagnostics { get; private set; } = null!;
    public static UpdateChecker Updates { get; private set; } = null!;

    public static MainSettings Main => Store.Main;
    public static ProfileSettings Profile => Store.Profile;
    public static bool ModeForced => Profile.InstallMode != "autodetect" && !string.IsNullOrEmpty(Profile.InstallMode);
    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "3.0.0";
    /// <summary>The channel the running build came from (also what the API and logs call its "branch").</summary>
    public static string Branch => Channels.Of(Version).ToString();
    /// <summary>The channel chosen in Settings, which decides what the update notice offers.</summary>
    public static UpdateChannel UpdateChannel => Channels.Parse(Main.UpdateChannel, Version);

    /// <summary>Watches for drives being plugged in or removed (started by the main window).</summary>
    public static DriveMonitor DriveMonitor { get; private set; } = null!;

    public static void Init(string? dataDirOverride = null, HttpMessageHandler? handler = null, IDriveService? drives = null, Func<string?>? driveFingerprint = null)
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
        Drives = drives ?? DriveServiceFactory.Create();
        Runner = new UpdateRunner(Drives, Files, Log);
        Diagnostics = new DiagnosticsService(Drives, Http, Paths.DataDir, () => Main.DownloadPath, () => Main.LogPath, ApiClient.HasToken);
        Updates = new UpdateChecker(Http);
        DriveMonitor = new DriveMonitor(Drives, driveFingerprint ?? DriveFingerprint.Current, () => !Workflow.Busy && DialogHostViewModel.Instance?.HasDialog != true);

        DriveInfoModel.Label = key => Loc[key switch { "free" => "Home.DriveFreeSpace", "size" => "Home.DriveSize", _ => "Home.DriveFileSystem" }];
        Loc.SetLanguage(Main.Lang);
        Main.Lang = Loc.Code;
        Log.Info($"Syn3 Updater {Version} ({Branch}) is starting on {OsName()}");
        foreach (var fact in SystemFacts()) Log.Debug($"System - {fact.Key}: {fact.Value}");
    }

    /// <summary>Machine and environment facts for the app log, log.txt and the Diagnostics page.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> SystemFacts() =>
        SystemInfo.Collect(Version, Branch, Paths.DataDir, Main.DownloadPath);

    /// <summary>Swaps in a random browser User-Agent from the API, as the original does (best effort).</summary>
    public static async Task RandomizeUserAgentAsync()
    {
        var ua = await Api.RandomUserAgent();
        Http.DefaultRequestHeaders.UserAgent.Clear();
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua);
    }

    public static string OsName() => SystemInfo.OsName();

    public static string T(string key) => Loc[key];
    public static string T(string key, params object[] args) => Loc.Format(key, args);

    public static void Save() => Store.Save();
}
