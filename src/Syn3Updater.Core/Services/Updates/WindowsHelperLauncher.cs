using System.Diagnostics;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>Starts the updater helper (replaced by tests).</summary>
public interface IHelperLauncher
{
    /// <summary>Starts <paramref name="helperExe"/> with the given arguments and returns once it is running.</summary>
    void Start(string helperExe, IReadOnlyList<string> args);
}

public sealed class ProcessHelperLauncher : IHelperLauncher
{
    public void Start(string helperExe, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helperExe) ?? "" };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new UpdateApplyException("the update helper could not be started");
    }
}

/// <summary>
/// Hands an update over to the helper: a running program cannot replace itself on Windows, so a private copy of the helper is started and this process then exits.
/// The helper runs with this process's (administrator) rights, so the copy is protected: it sits in a fresh, unpredictably named folder and this process keeps it open
/// with writes denied until it is running, so nothing can swap it for something else in between.
/// </summary>
public static class WindowsHelperLauncher
{
    public const string HelperName = "Syn3Updater.Updater.exe";

    public static bool HasHelper(string installDir) => File.Exists(Path.Combine(installDir, HelperName));

    /// <param name="updatesDir">Scratch folder inside the app's data folder; the copy goes in a new sub-folder.</param>
    /// <param name="resultFile">Where the helper writes the outcome for the app to read at its next start.</param>
    public static void Start(UpdatePlan plan, string zipPath, string updatesDir, string resultFile, string? launcherPrefsPath, int parentPid, IHelperLauncher launcher)
    {
        if (plan.Asset == null || plan.Manifest == null) throw new InvalidOperationException("this plan is not an installable update");
        var source = Path.Combine(plan.Install.AppDir, HelperName);
        if (!File.Exists(source)) throw new UpdateApplyException("this install has no update helper (" + HelperName + ")");
        var tag = ReleaseManifest.TagOf(plan.Asset) ?? throw new UpdateApplyException("cannot tell which release the update came from");

        var dir = Path.Combine(updatesDir, "helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var copy = Path.Combine(dir, HelperName);
        File.Copy(source, copy);

        var request = new ApplyRequest(plan.Install.AppDir, zipPath, plan.Asset.Sha256, plan.Asset.Size, tag, plan.Manifest.Version, plan.Manifest.Channel, parentPid,
            Path.Combine(plan.Install.AppDir, PackageStagerExe), resultFile, launcherPrefsPath);
        // the open handle denies writers until the helper is running; once it runs, Windows protects the image itself
        using var hold = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        launcher.Start(copy, new[] { "apply", request.Encode() });
    }

    private const string PackageStagerExe = "Syn3Updater.exe";
}
