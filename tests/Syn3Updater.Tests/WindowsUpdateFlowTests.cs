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

internal sealed class FakeHelper : IHelperLauncher
{
    public List<(string Exe, string[] Args)> Started { get; } = new();
    public Action<string>? OnStart { get; set; }
    public Exception? Fail { get; set; }
    public void Start(string helperExe, IReadOnlyList<string> args)
    {
        OnStart?.Invoke(helperExe);
        if (Fail != null) throw Fail;
        Started.Add((helperExe, args.ToArray()));
    }
}

/// <summary>The Windows path of an update, with a fake in place of the helper process (the helper's own work is covered by WindowsApplierTests and the end-to-end run of the real binary).</summary>
public class WindowsHelperLauncherTests : IDisposable
{
    private readonly TempDir _t = new();
    public void Dispose() => _t.Dispose();

    private (UpdatePlan Plan, string Zip) Scenario(bool withHelper = true, string tag = "v3.1.0")
    {
        var install = _t.Sub("Program Files", "Syn3Updater"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "Syn3Updater.exe"), "APP");
        if (withHelper) File.WriteAllText(Path.Combine(install, WindowsHelperLauncher.HelperName), "HELPER-BINARY");
        var zip = Zips.Make(("Syn3Updater.exe", "NEW"));
        var zipPath = _t.Sub("updates", "Syn3Updater-3.1.0-win-x64.zip"); Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!); File.WriteAllBytes(zipPath, zip);
        var name = "Syn3Updater-3.1.0-win-x64.zip";
        var asset = new ManifestAsset(UpdateOs.Windows, UpdateArch.X64, AssetKind.Zip, name, $"https://github.com/CyanLabs/Syn3Updater/releases/download/{tag}/{name}", zip.Length, Sha(zip));
        var manifest = ReleaseManifest.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version = "3.1.0", channel = "Stable", published = "2026-11-07T00:00:00Z", notes = "n",
            assets = new[] { new { os = "windows", arch = "x64", kind = "zip", name, url = asset.Url, size = asset.Size, sha256 = asset.Sha256 } }
        }));
        return (new UpdatePlan(PlanKind.Install, manifest, asset, new InstallInfo(InstallKind.WindowsFolder, install, UpdateRoute.InPlace, "t")), zipPath);
    }

    [Fact]
    public void The_helper_is_started_from_a_private_copy_with_the_request_on_its_command_line()
    {
        var (plan, zip) = Scenario(); var fake = new FakeHelper();
        WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), _t.Sub("data", "update-result.json"), @"/pd/launcherPrefs.json", 4242, fake);
        var (exe, args) = fake.Started.Single();
        Assert.NotEqual(Path.Combine(plan.Install.AppDir, WindowsHelperLauncher.HelperName), exe);          // never the installed one: that is about to be replaced
        Assert.StartsWith(_t.Sub("updates", "helper-"), exe);
        Assert.Equal("HELPER-BINARY", File.ReadAllText(exe));
        Assert.Equal("apply", args[0]);
        var r = ApplyRequest.Decode(args[1]);
        Assert.Equal(plan.Install.AppDir, r.InstallDir); Assert.Equal(zip, r.ZipPath);
        Assert.Equal(plan.Asset!.Sha256, r.Sha256); Assert.Equal(plan.Asset.Size, r.Size);
        Assert.Equal("v3.1.0", r.Tag); Assert.Equal("3.1.0", r.Version); Assert.Equal(UpdateChannel.Stable, r.Channel);
        Assert.Equal(4242, r.ParentPid);
        Assert.Equal(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe"), r.RestartExe);
        Assert.Equal(_t.Sub("data", "update-result.json"), r.ResultFile); Assert.Equal("/pd/launcherPrefs.json", r.LauncherPrefsPath);
    }

    [Fact]
    public void Each_update_gets_its_own_unpredictable_folder()
    {
        var (plan, zip) = Scenario(); var fake = new FakeHelper();
        WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), "/r.json", null, 1, fake);
        WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), "/r.json", null, 1, fake);
        Assert.NotEqual(fake.Started[0].Exe, fake.Started[1].Exe);
        Assert.Matches(@"helper-[0-9a-f]{32}", fake.Started[0].Exe);
    }

    [Fact]
    public void The_copy_cannot_be_modified_between_being_made_and_being_run()
    {
        var (plan, zip) = Scenario(); Exception? tamper = null;
        var fake = new FakeHelper { OnStart = exe => { try { using var w = new FileStream(exe, FileMode.Open, FileAccess.Write, FileShare.None); } catch (IOException e) { tamper = e; } } };
        WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), "/r.json", null, 1, fake);
        Assert.IsType<IOException>(tamper);
    }

    [Fact]
    public void An_install_without_the_helper_cannot_be_handed_over()
    {
        var (plan, zip) = Scenario(withHelper: false);
        Assert.False(WindowsHelperLauncher.HasHelper(plan.Install.AppDir));
        Assert.Throws<UpdateApplyException>(() => WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), "/r.json", null, 1, new FakeHelper()));
    }

    [Fact]
    public void A_helper_that_cannot_start_is_reported_as_an_update_error()
    {
        var (plan, zip) = Scenario();
        Assert.Throws<UpdateApplyException>(() => WindowsHelperLauncher.Start(plan, zip, _t.Sub("updates"), "/r.json", null, 1, new FakeHelper { Fail = new UpdateApplyException("nope") }));
    }

    [Fact]
    public void A_plan_without_a_file_is_refused()
    {
        var (plan, zip) = Scenario();
        Assert.Throws<InvalidOperationException>(() => WindowsHelperLauncher.Start(plan with { Asset = null }, zip, _t.Sub("updates"), "/r.json", null, 1, new FakeHelper()));
    }

    [Theory]
    [InlineData("v3.1.0", "v3.1.0")] [InlineData("2.99.99.7", "2.99.99.7")] [InlineData("nightly", "nightly")]
    public void The_release_tag_is_read_from_the_download_address(string tag, string expected) =>
        Assert.Equal(expected, ReleaseManifest.TagOf(Scenario(tag: tag).Plan.Asset!));

    [Fact]
    public void An_address_outside_this_repos_releases_has_no_tag() =>
        Assert.Null(ReleaseManifest.TagOf(Scenario().Plan.Asset! with { Url = "https://github.com/Other/Repo/releases/download/v1/x.zip" }));

    [Fact]
    public async Task The_installer_hands_a_windows_update_to_the_helper_and_keeps_the_zip_for_it()
    {
        var (plan, zipPath) = Scenario(); var fake = new FakeHelper();
        var bytes = File.ReadAllBytes(zipPath); File.Delete(zipPath);
        var installer = new UpdateInstaller(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) })), _ => long.MaxValue),
            _t.Sub("updates"), null, _t.Sub("data", "r.json"), null, fake);
        var outcome = await installer.RunAsync(plan);
        Assert.True(outcome.HelperRestarts);
        Assert.Equal(Path.Combine(plan.Install.AppDir, "Syn3Updater.exe"), outcome.Exe);
        Assert.True(File.Exists(ApplyRequest.Decode(fake.Started.Single().Args[1]).ZipPath));        // the helper deletes it after installing
    }

    [Fact]
    public async Task A_corrupt_windows_download_never_reaches_the_helper()
    {
        var (plan, _) = Scenario(); var fake = new FakeHelper();
        var installer = new UpdateInstaller(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2, 3 }) })), _ => long.MaxValue),
            _t.Sub("updates2"), null, _t.Sub("data", "r.json"), null, fake);
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.RunAsync(plan));
        Assert.Empty(fake.Started);
    }

    [Fact]
    public void Old_helper_copies_are_purged_with_the_other_leftovers()
    {
        var dir = _t.Sub("updates"); var old = Path.Combine(dir, "helper-aaaa"); var fresh = Path.Combine(dir, "helper-bbbb"); var other = Path.Combine(dir, "keep-me");
        foreach (var d in new[] { old, fresh, other }) Directory.CreateDirectory(d);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10)); Directory.SetLastWriteTimeUtc(other, DateTime.UtcNow.AddDays(-10));
        UpdateInstaller.PurgeOld(dir, TimeSpan.FromDays(7));
        Assert.False(Directory.Exists(old)); Assert.True(Directory.Exists(fresh)); Assert.True(Directory.Exists(other));
    }

    [Fact]
    public async Task The_service_offers_a_windows_install_only_when_the_package_came_with_the_helper()
    {
        var manifest = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json")));
        var sig = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json.minisig")));
        var svc = new UpdateService(new HttpClient(new FuncHandler(r => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? sig : manifest) })));
        const string b = "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0-beta.2/";
        var info = new UpdateInfo("3.1.0-beta.2", "u", true, false, b + "manifest.json", b + "manifest.json.minisig");
        var with = new InstallInfo(InstallKind.WindowsFolder, Scenario().Plan.Install.AppDir, UpdateRoute.InPlace, "t");
        var without = with with { AppDir = Scenario(withHelper: false).Plan.Install.AppDir + "-none" };
        Directory.CreateDirectory(without.AppDir);
        Assert.Equal(PlanKind.Install, (await svc.PlanAsync(info, with, "3.0.0", UpdateChannel.Beta, UpdateOs.Windows, UpdateArch.X64)).Kind);
        Assert.Equal(PlanKind.Manual, (await svc.PlanAsync(info, without, "3.0.0", UpdateChannel.Beta, UpdateOs.Windows, UpdateArch.X64)).Kind);
    }
}

public class WindowsUpdateAppTests
{
    private sealed class Env : IDisposable
    {
        public AppEnv App = new(); public TempDir T = new(); public FakeHelper Helper = new(); public List<string> Started = new(); public int Quits;
        public Env()
        {
            UpdateRestart.Starter = e => { Started.Add(e); return true; }; UpdateRestart.Quit = () => Quits++; Ctx.LauncherPrefsPath = null;
        }
        public UpdatePlan Ready()
        {
            var install = T.Sub("install"); Directory.CreateDirectory(install);
            File.WriteAllText(Path.Combine(install, "Syn3Updater.exe"), "APP"); File.WriteAllText(Path.Combine(install, WindowsHelperLauncher.HelperName), "H");
            var zip = Zips.Make(("Syn3Updater.exe", "NEW")); var name = "Syn3Updater-3.1.0-win-x64.zip";
            var asset = new ManifestAsset(UpdateOs.Windows, UpdateArch.X64, AssetKind.Zip, name, "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0/" + name, zip.Length, Sha(zip));
            var manifest = ReleaseManifest.Parse(JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, version = "3.1.0", channel = "Stable", published = "2026-11-07T00:00:00Z", notes = "Windows notes",
                assets = new[] { new { os = "windows", arch = "x64", kind = "zip", name, url = asset.Url, size = asset.Size, sha256 = asset.Sha256 } }
            }));
            var plan = new UpdatePlan(PlanKind.Install, manifest, asset, new InstallInfo(InstallKind.WindowsFolder, install, UpdateRoute.InPlace, "t"));
            Ctx.Install = plan.Install;
            Ctx.UpdateInstaller = new UpdateInstaller(new UpdateDownloader(new HttpClient(new FuncHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) })), _ => long.MaxValue),
                T.Sub("updates"), null, Ctx.UpdateResultFile, null, Helper);
            App.Main.Update = new UpdateInfo("3.1.0", "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0", false); App.Main.Plan = plan;
            return plan;
        }
        public void Dispose() { UpdateRestart.Starter = null!; UpdateRestart.Quit = null!; Workflow.Busy = false; App.Dispose(); T.Dispose(); }
    }

    [AvaloniaFact]
    public async Task On_windows_the_app_hands_over_and_closes_without_starting_anything_itself()
    {
        using var e = new Env(); e.Ready(); e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Single(e.Helper.Started);                       // the helper does the swap and the restart
        Assert.Empty(e.Started);                              // so the app does not start a program that is about to be replaced
        Assert.Equal(1, e.Quits);
        Assert.Equal("3.1.0", Ctx.Main.WhatsNewVersion); Assert.Equal("Windows notes", Ctx.Main.WhatsNewNotes);
        Assert.True(Workflow.Busy);
    }

    [AvaloniaFact]
    public async Task A_helper_that_cannot_start_leaves_the_app_open_and_free()
    {
        using var e = new Env(); e.Ready(); e.Helper.Fail = new UpdateApplyException("blocked by antivirus"); e.App.Dialogs.DefaultYes = true;
        await e.App.Main.UpdateNowCommand.ExecuteAsync(null);
        Assert.Contains("blocked by antivirus", e.App.Dialogs.Messages.Last());
        Assert.Equal(0, e.Quits); Assert.False(Workflow.Busy); Assert.False(e.App.Main.InstallingUpdate);
    }

    [AvaloniaFact]
    public async Task A_failed_update_reported_by_the_helper_is_shown_once_and_the_whats_new_is_dropped()
    {
        using var e = new Env(); e.Ready();
        Ctx.Main.WhatsNewVersion = Ctx.Version; Ctx.Main.WhatsNewNotes = "should not show";
        new ApplyResult(false, "3.1.0", "Your previous version was restored.", DateTimeOffset.Now).Write(Ctx.UpdateResultFile);
        await e.App.Main.OnStartedAsync();
        Assert.Contains("Your previous version was restored.", e.App.Dialogs.Messages.Single());
        Assert.Null(Ctx.Main.WhatsNewVersion); Assert.False(File.Exists(Ctx.UpdateResultFile));
        await e.App.Main.OnStartedAsync();
        Assert.Single(e.App.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task A_successful_result_is_cleaned_up_and_the_whats_new_still_shows()
    {
        using var e = new Env(); e.Ready();
        Ctx.Main.WhatsNewVersion = Ctx.Version; Ctx.Main.WhatsNewNotes = "Fresh notes";
        new ApplyResult(true, Ctx.Version, "Installed", DateTimeOffset.Now).Write(Ctx.UpdateResultFile);
        await e.App.Main.OnStartedAsync();
        Assert.False(File.Exists(Ctx.UpdateResultFile));
        Assert.Equal("Fresh notes", e.App.Dialogs.Messages.Single());
    }

    [AvaloniaFact]
    public async Task At_start_an_interrupted_windows_update_is_rolled_back()
    {
        using var e = new Env(); var plan = e.Ready();
        var backup = Path.Combine(plan.Install.AppDir, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "ORIGINAL");
        await e.App.Main.OnStartedAsync();
        Assert.Equal("ORIGINAL", File.ReadAllText(Path.Combine(plan.Install.AppDir, "Launcher.exe")));
        Assert.False(Directory.Exists(backup));
    }
}
