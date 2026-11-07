using System.Net;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;
using Cyanlabs.Syn3Updater.Tests;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

/// <summary>The Windows path of an update. The swap is done by the running app itself (a running program can be renamed, which is all it needs), so a real zip and a real folder prove it here.</summary>
public class WindowsInstallerTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private (UpdatePlan Plan, byte[] Zip) Scenario(string tag = "v3.1.0", (string, string)[]? package = null)
    {
        var install = _t.Sub("Program Files", "Syn3Updater"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "Syn3Updater.exe"), "OLD-APP"); File.WriteAllText(Path.Combine(install, "Launcher.exe"), "OLD-LAUNCHER");
        File.WriteAllText(Path.Combine(install, "old.dll"), "stale"); File.WriteAllText(Path.Combine(install, "notes.txt"), "mine");
        var zip = Zips.Make(package ?? new[] { ("Syn3Updater-3.1.0-win-x64/Syn3Updater.exe", "NEW-APP"), ("Syn3Updater-3.1.0-win-x64/Launcher.exe", "NEW-LAUNCHER"), ("Syn3Updater-3.1.0-win-x64/LICENSE", "gpl") });
        var name = "Syn3Updater-3.1.0-win-x64.zip";
        var asset = new ManifestAsset(UpdateOs.Windows, UpdateArch.X64, AssetKind.Zip, name, $"https://github.com/CyanLabs/Syn3Updater/releases/download/{tag}/{name}", zip.Length, Sha(zip));
        var manifest = ReleaseManifest.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version = "3.1.0", channel = "Stable", published = "2026-11-07T00:00:00Z", notes = "n",
            assets = new[] { new { os = "windows", arch = "x64", kind = "zip", name, url = asset.Url, size = asset.Size, sha256 = asset.Sha256 } }
        }));
        return (new UpdatePlan(PlanKind.Install, manifest, asset, new InstallInfo(InstallKind.WindowsFolder, install, UpdateRoute.InPlace, "t")), zip);
    }

    private UpdateInstaller Installer(byte[] served, string? prefs = null) =>
        new(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(served) })), _ => long.MaxValue), _t.Sub("updates"), null, prefs);

    [Fact]
    public async Task The_app_downloads_swaps_its_own_files_and_returns_the_new_program_to_start()
    {
        var (plan, zip) = Scenario(); var logs = new List<string>();
        var exe = await Installer(zip).RunAsync(plan, log: logs.Add);
        var dir = plan.Install.AppDir;
        Assert.Equal(Path.Combine(dir, "Syn3Updater.exe"), exe);
        Assert.Equal("NEW-APP", File.ReadAllText(exe)); Assert.Equal("NEW-LAUNCHER", File.ReadAllText(Path.Combine(dir, "Launcher.exe")));
        Assert.False(File.Exists(Path.Combine(dir, "old.dll"))); Assert.Equal("mine", File.ReadAllText(Path.Combine(dir, "notes.txt")));
        Assert.Equal("3.1.0.0", InstallManifest.Load(dir)!.Version);
        Assert.Empty(Directory.GetFiles(_t.Sub("updates")));                                  // the downloaded zip is removed
        Assert.Contains(logs, l => l.Contains("Checksum verified"));
    }

    [Fact]
    public async Task The_Launchers_record_follows_the_release_tag()
    {
        var (plan, zip) = Scenario(tag: "2.99.99.7"); var prefs = _t.Sub("pd", "launcherPrefs.json");
        Directory.CreateDirectory(Path.GetDirectoryName(prefs)!); File.WriteAllText(prefs, "{\"ReleaseBranch\":1}");
        await Installer(zip, prefs).RunAsync(plan);
        Assert.Contains("\"ReleaseInstalled\":\"2.99.99.7\"", File.ReadAllText(prefs));
    }

    [Fact]
    public async Task A_corrupt_download_is_refused_before_anything_in_the_install_changes()
    {
        var (plan, _) = Scenario();
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(new byte[] { 1, 2, 3 }).RunAsync(plan));
        Assert.Equal("OLD-APP", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe")));
    }

    [Fact]
    public async Task A_package_that_cannot_be_installed_leaves_the_old_version_in_place()
    {
        var (plan, zip) = Scenario(package: new[] { ("readme.txt", "no app in here") });
        await Assert.ThrowsAsync<UpdateApplyException>(() => Installer(zip).RunAsync(plan));
        Assert.Equal("OLD-APP", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe")));
        Assert.False(Directory.Exists(Path.Combine(plan.Install.AppDir, InstallTransaction.StagingDirName)));
    }

    /// <summary>
    /// The mechanism the whole Windows path rests on, on a real Windows machine (skipped elsewhere): a program that is RUNNING can be renamed aside and replaced, but not deleted, so the old
    /// program stays in the backup folder until it has exited and the next start removes it.
    /// </summary>
    [Fact]
    public async Task On_windows_a_running_program_is_replaced_by_renaming_it_aside()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (plan, zip) = Scenario(); var dir = plan.Install.AppDir; var exe = Path.Combine(dir, "Syn3Updater.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);           // a real executable image that is really running (a copy of ping.exe cannot start outside System32)
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(1000);
            Assert.False(running.HasExited, running.HasExited ? $"the stand-in exited by itself with code {running.ExitCode}: {running.StandardError.ReadToEnd()} {running.StandardOutput.ReadToEnd()}" : "");
            await Installer(zip).RunAsync(plan);
            Assert.Equal("NEW-APP", File.ReadAllText(exe));                                                // the new program is in place while the old one still runs
            var backup = Path.Combine(dir, InstallTransaction.BackupDirName);
            Assert.True(File.Exists(Path.Combine(backup, "Syn3Updater.exe")));                             // the old one could be renamed aside but not deleted
            Assert.True(File.Exists(Path.Combine(backup, "COMPLETE")), "the COMPLETE marker was lost: the next start would roll the new version back");
            Assert.False(running.HasExited, "the program stopped after its file was renamed aside and replaced");
        }
        finally { try { running.Kill(true); running.WaitForExit(5000); } catch (InvalidOperationException) { } }
        Assert.False(InstallTransaction.Recover(dir));                                                     // the next start: the update was complete, so the old program is removed
        Assert.False(Directory.Exists(Path.Combine(dir, InstallTransaction.BackupDirName)));
        Assert.Equal("NEW-APP", File.ReadAllText(exe));
    }

    [Fact]
    public async Task On_windows_an_interrupted_update_is_rolled_back_even_over_a_program_that_is_running()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (plan, _) = Scenario(); var dir = plan.Install.AppDir; var exe = Path.Combine(dir, "Syn3Updater.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), exe, overwrite: true);
        using var running = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "/c ping -n 120 127.0.0.1 >nul") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            await Task.Delay(1000); Assert.False(running.HasExited);
            var backup = Path.Combine(dir, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);       // an update cut short: the original is in the backup, no COMPLETE marker
            File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "ORIGINAL");
            Assert.True(InstallTransaction.Recover(dir));                                                             // runs inside the running program, which cannot be deleted
            Assert.Equal("ORIGINAL", File.ReadAllText(exe));
            Assert.False(Directory.Exists(backup));
            Assert.False(running.HasExited);
            Assert.Single(Directory.GetFiles(dir, "*.old"));                                                          // the running one was renamed aside
        }
        finally { try { running.Kill(true); running.WaitForExit(5000); } catch (InvalidOperationException) { } }
        InstallTransaction.Recover(dir);
        Assert.Empty(Directory.GetFiles(dir, "*.old"));                                                               // and is removed once it has exited
    }

    [Theory]
    [InlineData("v3.1.0", "v3.1.0")] [InlineData("2.99.99.7", "2.99.99.7")] [InlineData("nightly", "nightly")]
    public void The_release_tag_is_read_from_the_download_address(string tag, string expected) => Assert.Equal(expected, ReleaseManifest.TagOf(Scenario(tag: tag).Plan.Asset!));

    [Fact]
    public void An_address_outside_this_repos_releases_has_no_tag() =>
        Assert.Null(ReleaseManifest.TagOf(Scenario().Plan.Asset! with { Url = "https://github.com/Other/Repo/releases/download/v1/x.zip" }));

    [Fact]
    public void Old_scratch_folders_are_purged_and_recent_ones_kept()
    {
        var dir = _t.Sub("updates"); var work = Path.Combine(dir, "work"); Directory.CreateDirectory(work);
        Directory.SetLastWriteTimeUtc(work, DateTime.UtcNow.AddDays(-10));
        UpdateInstaller.PurgeOld(dir, TimeSpan.FromDays(7));
        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public void Windows_folders_and_linux_tar_installs_are_the_ones_the_app_replaces_itself()
    {
        Assert.True(UpdateService.DefaultCanApply(new InstallInfo(InstallKind.WindowsFolder, "/x", UpdateRoute.InPlace, "")));
        Assert.True(UpdateService.DefaultCanApply(new InstallInfo(InstallKind.LinuxTar, "/x", UpdateRoute.InPlace, "")));
        foreach (var k in new[] { InstallKind.LinuxDeb, InstallKind.LinuxRpm, InstallKind.LinuxPacman, InstallKind.LinuxPortable, InstallKind.MacApp, InstallKind.Development })
            Assert.False(UpdateService.DefaultCanApply(new InstallInfo(k, "/x", UpdateRoute.InPlace, "")));
    }
}

public class UpdateRestartLockTests
{
    private static (string Name, List<string> Got) Primary()
    {
        var name = "syn3-restart-test-" + Guid.NewGuid().ToString("N")[..12]; var got = new List<string>();
        AppLaunch.Primary = SingleInstance.TryBecomePrimary(name); AppLaunch.PipeName = name; AppLaunch.OnMessage = m => { lock (got) got.Add(m); };
        AppLaunch.Primary!.Listen(AppLaunch.OnMessage);
        return (name, got);
    }
    private static void Reset() { AppLaunch.Primary?.Dispose(); AppLaunch.Primary = null; AppLaunch.PipeName = null; AppLaunch.OnMessage = null; UpdateRestart.Starter = null!; UpdateRestart.Quit = null!; }

    [Fact]
    public void The_lock_is_free_while_the_new_program_starts_so_it_does_not_hand_its_message_to_this_one_and_exit()
    {
        var (name, _) = Primary(); var quits = 0; bool? freeWhenStarting = null;
        try
        {
            UpdateRestart.Quit = () => quits++;
            UpdateRestart.Starter = _ => { using var probe = SingleInstance.TryBecomePrimary(name); freeWhenStarting = probe != null; return true; };
            Assert.True(UpdateRestart.Restart("/x")); Assert.True(freeWhenStarting); Assert.Equal(1, quits);
        }
        finally { Reset(); }
    }

    [Fact]
    public async Task If_the_new_program_cannot_be_started_this_one_takes_the_lock_back_and_still_receives_links()
    {
        var (name, got) = Primary(); var quits = 0;
        try
        {
            UpdateRestart.Quit = () => quits++; UpdateRestart.Starter = _ => false;
            Assert.False(UpdateRestart.Restart("/x"));
            Assert.Equal(0, quits); Assert.NotNull(AppLaunch.Primary);
            using (var second = SingleInstance.TryBecomePrimary(name)) Assert.Null(second);          // a later launch cannot become a second copy
            Assert.True(await SingleInstance.TrySendAsync(name, "syn3updater://?region=NA"));          // it hands its link to this copy, as it always did
            await AppEnv.Until(() => { lock (got) return got.Contains("syn3updater://?region=NA"); }, 5000);
        }
        finally { Reset(); }
    }
}

public class WindowsUpdateAppTests
{
    private sealed class Env : IDisposable
    {
        public AppEnv App = new(); public WindowsInstallerTests Inner = new(); public List<string> Started = new(); public int Quits;
        public Env() { UpdateRestart.Starter = e => { Started.Add(e); return true; }; UpdateRestart.Quit = () => Quits++; Ctx.LauncherPrefsPath = null; }
        public void Dispose() { UpdateRestart.Starter = null!; UpdateRestart.Quit = null!; Workflow.Busy = false; App.Dispose(); Inner.Dispose(); }
    }

    private static (UpdatePlan Plan, byte[] Zip) Ready(Env e, (string, string)[]? package = null)
    {
        var (plan, zip) = ((UpdatePlan, byte[]))typeof(WindowsInstallerTests).GetMethod("Scenario", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(e.Inner, new object?[] { "v3.1.0", package })!;
        Ctx.Install = plan.Install;
        Ctx.UpdateInstaller = new UpdateInstaller(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) })), _ => long.MaxValue), Path.Combine(plan.Install.AppDir, "..", "updates"));
        e.App.Main.Update = new UpdateInfo("3.1.0", "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0", false); e.App.Main.Plan = plan;
        return (plan, zip);
    }

    [AvaloniaFact]
    public async Task On_windows_the_app_swaps_its_files_and_restarts_into_the_new_program()
    {
        using var e = new Env(); var (plan, _) = Ready(e); e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        var exe = Path.Combine(plan.Install.AppDir, "Syn3Updater.exe");
        Assert.Equal("NEW-APP", File.ReadAllText(exe));
        Assert.Equal(new[] { exe }, e.Started); Assert.Equal(1, e.Quits);
        Assert.Equal("3.1.0", Ctx.Main.WhatsNewVersion);
    }

    [AvaloniaFact]
    public async Task A_package_that_cannot_be_installed_shows_the_error_and_leaves_the_app_open_and_free()
    {
        using var e = new Env(); var (plan, _) = Ready(e, new[] { ("readme.txt", "x") }); e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("could not be installed", e.App.Dialogs.Messages.Last());
        Assert.Equal("OLD-APP", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe")));
        Assert.Empty(e.Started); Assert.Equal(0, e.Quits); Assert.False(Workflow.Busy); Assert.False(e.App.Main.InstallingUpdate);
    }

    [AvaloniaFact]
    public async Task At_start_an_interrupted_windows_update_is_rolled_back_and_a_finished_ones_old_program_is_removed()
    {
        using var e = new Env(); var (plan, _) = Ready(e);
        var backup = Path.Combine(plan.Install.AppDir, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "ORIGINAL");                  // no COMPLETE marker: interrupted
        await e.App.Main.OnStartedAsync();
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Launcher.exe"))); Assert.False(Directory.Exists(backup));

        Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "OLD-RUNNING"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
        await e.App.Main.OnStartedAsync();
        Assert.False(Directory.Exists(backup)); Assert.Equal("OLD-APP", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe")));
    }
}
