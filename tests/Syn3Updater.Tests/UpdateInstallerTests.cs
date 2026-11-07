using System.Net;
using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

public class UpdateInstallerTests : IDisposable
{
    internal readonly string Root = Path.Combine(Path.GetTempPath(), "syn3-inst-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(Root, true); } catch { } }

    internal const string CopyScript = "#!/usr/bin/env bash\nset -e\nmkdir -p \"$XDG_DATA_HOME/syn3updater\"\ninstall -m 755 \"$(dirname \"$0\")/Syn3Updater\" \"$XDG_DATA_HOME/syn3updater/Syn3Updater\"\n";

    /// <summary>A whole scenario: an installed old program, a release archive containing a new one, and a plan to install it.</summary>
    internal (UpdatePlan Plan, byte[] Archive, string Xdg, string AppDir) Scenario(string newContent = "NEW", string script = CopyScript)
    {
        var xdg = Path.Combine(Root, "xdg"); var appDir = Path.Combine(xdg, "syn3updater");
        Directory.CreateDirectory(appDir); File.WriteAllText(Path.Combine(appDir, "Syn3Updater"), "OLD"); File.WriteAllText(Path.Combine(appDir, ".install-method"), "tar\n");
        var path = MakeTarGz(Path.Combine(Root, "r.tar.gz"), ("Syn3Updater-3.1.0-linux-x64/", "", 0x1ed), ("Syn3Updater-3.1.0-linux-x64/install.sh", script, 0x1ed), ("Syn3Updater-3.1.0-linux-x64/Syn3Updater", newContent, 0x1ed));
        var bytes = File.ReadAllBytes(path); File.Delete(path);
        var asset = Asset(bytes);
        var manifest = ReleaseManifest.Parse(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = 1, version = "3.1.0", channel = "Stable", published = "2026-11-07T00:00:00Z", notes = "Notes here",
            assets = new[] { new { os = "linux", arch = "x64", kind = "targz", name = asset.Name, url = asset.Url, size = asset.Size, sha256 = asset.Sha256 } }
        }));
        var install = new InstallInfo(InstallKind.LinuxTar, appDir, UpdateRoute.InPlace, "test");
        return (new UpdatePlan(PlanKind.Install, manifest, asset, install), bytes, xdg, appDir);
    }

    internal static FuncHandler Serve(byte[] data) => new(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

    internal UpdateInstaller Installer(FuncHandler h, string xdg) => new(new UpdateDownloader(new HttpClient(h), _ => long.MaxValue), Path.Combine(Root, "updates"), xdg);

    [Fact]
    public async Task Download_then_install_replaces_the_program_and_reports_both_stages()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        var (plan, bytes, xdg, appDir) = Scenario();
        var stages = new List<UpdateInstaller.Stage>();
        var exe = await Installer(Serve(bytes), xdg).RunAsync(plan, null, stages.Add);
        Assert.Equal(Path.Combine(appDir, "Syn3Updater"), exe);
        Assert.Equal("NEW", File.ReadAllText(exe));
        Assert.Equal(new[] { UpdateInstaller.Stage.Downloading, UpdateInstaller.Stage.Installing }, stages);
        Assert.Empty(Directory.GetFiles(Path.Combine(Root, "updates")));                 // the archive is not kept
    }

    [Fact]
    public async Task A_download_that_does_not_match_the_manifest_never_reaches_install_sh()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        var (plan, bytes, xdg, appDir) = Scenario(script: "#!/usr/bin/env bash\ntouch \"$XDG_DATA_HOME/script-ran\"\n");
        var tampered = (byte[])bytes.Clone(); tampered[tampered.Length / 2] ^= 1;
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer(Serve(tampered), xdg).RunAsync(plan));
        Assert.False(File.Exists(Path.Combine(xdg, "script-ran")));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
    }

    [Fact]
    public async Task A_failed_install_leaves_the_old_program()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        var (plan, bytes, xdg, appDir) = Scenario(script: "#!/usr/bin/env bash\nexit 9\n");
        await Assert.ThrowsAsync<UpdateApplyException>(() => Installer(Serve(bytes), xdg).RunAsync(plan));
        Assert.Equal("OLD", File.ReadAllText(Path.Combine(appDir, "Syn3Updater")));
    }

    [Fact]
    public async Task Plans_that_are_not_installable_are_refused()
    {
        var (plan, bytes, xdg, _) = Scenario();
        var i = Installer(Serve(bytes), xdg);
        await Assert.ThrowsAsync<InvalidOperationException>(() => i.RunAsync(plan with { Kind = PlanKind.Manual }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => i.RunAsync(plan with { Asset = null }));
    }

    [Fact]
    public async Task An_install_kind_with_no_applier_yet_is_refused_after_nothing_is_changed()
    {
        var (plan, bytes, xdg, _) = Scenario();
        var deb = plan with { Install = plan.Install with { Kind = InstallKind.LinuxDeb } };
        await Assert.ThrowsAsync<UpdateApplyException>(() => Installer(Serve(bytes), xdg).RunAsync(deb));
    }

    [Fact]
    public void Old_downloads_are_purged_and_recent_ones_kept()
    {
        var dir = Path.Combine(Root, "updates"); Directory.CreateDirectory(Path.Combine(dir, "work"));
        var old = Path.Combine(dir, "old.part"); var fresh = Path.Combine(dir, "fresh.part");
        File.WriteAllText(old, "x"); File.WriteAllText(fresh, "x"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
        UpdateInstaller.PurgeOld(dir, TimeSpan.FromDays(7));
        Assert.False(File.Exists(old)); Assert.True(File.Exists(fresh));
        UpdateInstaller.PurgeOld(Path.Combine(Root, "does-not-exist"), TimeSpan.Zero);          // must not throw
    }
}
