namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>Downloads the release file a plan chose, proves it, and applies it for this kind of install. Returns the program to start afterwards.</summary>
public sealed class UpdateInstaller
{
    private readonly UpdateDownloader _downloader;
    private readonly string _dir;
    private readonly string? _dataHome;

    /// <param name="dir">Where downloads and scratch files go (inside the app's data folder).</param>
    /// <param name="dataHome">Linux: the XDG data home install.sh will use (null = the environment's). Tests set it.</param>
    public UpdateInstaller(UpdateDownloader downloader, string dir, string? dataHome = null) { _downloader = downloader; _dir = dir; _dataHome = dataHome; }

    public enum Stage { Downloading, Installing }

    /// <returns>The path of the new program, ready to be started.</returns>
    public async Task<string> RunAsync(UpdatePlan plan, IProgress<double>? progress = null, Action<Stage>? stage = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (plan.Kind != PlanKind.Install || plan.Asset == null || plan.Manifest == null) throw new InvalidOperationException("this plan is not an installable update");
        stage?.Invoke(Stage.Downloading);
        var file = await _downloader.DownloadAsync(plan.Asset, _dir, progress, ct);
        stage?.Invoke(Stage.Installing);
        string exe;
        switch (plan.Install.Kind)
        {
            case InstallKind.LinuxTar when plan.Asset.Kind == AssetKind.TarGz:
                exe = await LinuxTarApplier.ApplyAsync(file, plan.Install, Path.Combine(_dir, "work"), _dataHome, null, ct, extraEnv);
                break;
            default:
                throw new UpdateApplyException($"updating an install of this kind ({plan.Install.Kind}) is not supported");
        }
        TryDelete(file);
        return exe;
    }

    /// <summary>Removes downloads and partial files older than <paramref name="age"/> (a download left behind by a cancelled or failed update).</summary>
    public static void PurgeOld(string dir, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir)) if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > age) TryDelete(f);
            var work = Path.Combine(dir, "work");
            if (Directory.Exists(work) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(work) > age) Directory.Delete(work, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
