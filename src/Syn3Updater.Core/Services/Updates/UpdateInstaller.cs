namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>What an installation left behind.</summary>
/// <param name="Exe">The program to start next.</param>
/// <param name="HelperRestarts">True when a helper process is doing the swap and will start the program itself once this one has exited (Windows): the caller only has to close.</param>
public sealed record InstallOutcome(string Exe, bool HelperRestarts);

/// <summary>Downloads the release file a plan chose, proves it, and applies it for this kind of install.</summary>
public sealed class UpdateInstaller
{
    private readonly UpdateDownloader _downloader;
    private readonly string _dir;
    private readonly string? _dataHome;
    private readonly string? _resultFile, _launcherPrefs;
    private readonly IHelperLauncher _helper;

    /// <param name="dir">Where downloads and scratch files go (inside the app's data folder).</param>
    /// <param name="dataHome">Linux: the XDG data home install.sh will use (null = the environment's). Tests set it.</param>
    /// <param name="resultFile">Windows: where the helper writes its outcome for the app to read at its next start.</param>
    /// <param name="launcherPrefsPath">Windows: the Launcher's prefs file, which the helper keeps in step (null when there is none).</param>
    public UpdateInstaller(UpdateDownloader downloader, string dir, string? dataHome = null, string? resultFile = null, string? launcherPrefsPath = null, IHelperLauncher? helper = null)
    {
        _downloader = downloader; _dir = dir; _dataHome = dataHome; _resultFile = resultFile ?? Path.Combine(Path.GetDirectoryName(dir) ?? dir, "update-result.json");
        _launcherPrefs = launcherPrefsPath; _helper = helper ?? new ProcessHelperLauncher();
    }

    public enum Stage { Downloading, Installing }

    public async Task<InstallOutcome> RunAsync(UpdatePlan plan, IProgress<double>? progress = null, Action<Stage>? stage = null, CancellationToken ct = default, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        if (plan.Kind != PlanKind.Install || plan.Asset == null || plan.Manifest == null) throw new InvalidOperationException("this plan is not an installable update");
        stage?.Invoke(Stage.Downloading);
        var file = await _downloader.DownloadAsync(plan.Asset, _dir, progress, ct);
        stage?.Invoke(Stage.Installing);
        switch (plan.Install.Kind)
        {
            case InstallKind.LinuxTar when plan.Asset.Kind == AssetKind.TarGz:
                var exe = await LinuxTarApplier.ApplyAsync(file, plan.Install, Path.Combine(_dir, "work"), _dataHome, null, ct, extraEnv);
                TryDelete(file);
                return new InstallOutcome(exe, false);
            case InstallKind.WindowsFolder when plan.Asset.Kind == AssetKind.Zip:
                // the helper deletes the zip once it has installed it
                WindowsHelperLauncher.Start(plan, file, _dir, _resultFile!, _launcherPrefs, Environment.ProcessId, _helper);
                return new InstallOutcome(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe"), true);
            default:
                throw new UpdateApplyException($"updating an install of this kind ({plan.Install.Kind}) is not supported");
        }
    }

    /// <summary>Removes downloads, partial files and helper copies older than <paramref name="age"/> (left behind by a cancelled or failed update).</summary>
    public static void PurgeOld(string dir, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir)) if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > age) TryDelete(f);
            foreach (var d in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(d);
                if ((name == "work" || name.StartsWith("helper-", StringComparison.Ordinal)) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(d) > age)
                    try { Directory.Delete(d, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static void TryDelete(string f) { try { File.Delete(f); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
