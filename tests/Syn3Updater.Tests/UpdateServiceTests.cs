using System.Net;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

/// <summary>Uses the manifest signed by the REAL release key (Fixtures/Minisign/release-manifest.json): version 3.1.0-beta.2, Beta channel, 12 files.</summary>
public class UpdateServiceTests
{
    private const string Base = "https://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0-beta.2/";
    private static readonly byte[] Manifest = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json")));
    private static readonly byte[] Signature = File.ReadAllBytes(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "release-manifest.json.minisig")));

    private static UpdateInfo Info(string version = "3.1.0-beta.2", bool sw = false, string? manifest = Base + "manifest.json", string? sig = Base + "manifest.json.minisig") =>
        new(version, "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0-beta.2", true, sw, manifest, sig);

    private static InstallInfo Inst(InstallKind kind, UpdateRoute route) => new(kind, "/x", route, "reason");

    private static UpdateService Svc(Func<HttpRequestMessage, HttpResponseMessage>? f = null, FuncHandler? h = null) =>
        new(new HttpClient(h ?? new FuncHandler(f ?? (r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Signature) : Ok(Manifest)))));

    private static HttpResponseMessage Ok(byte[] b) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(b) };

    private static Task<UpdatePlan> Plan(UpdateService s, InstallInfo i, UpdateInfo? info = null, string current = "3.0.0-test9", UpdateChannel ch = UpdateChannel.Beta,
        UpdateOs os = UpdateOs.Linux, UpdateArch arch = UpdateArch.X64) => s.PlanAsync(info ?? Info(), i, current, ch, os, arch);

    [Fact]
    public async Task A_tar_install_gets_its_own_architectures_archive_to_install()
    {
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace));
        Assert.Equal(PlanKind.Install, p.Kind);
        Assert.Equal("Syn3Updater-3.1.0-beta.2-linux-x64.tar.gz", p.Asset!.Name);
        Assert.Equal("3.1.0-beta.2", p.Manifest!.Version);
        p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), arch: UpdateArch.Arm64);
        Assert.Equal("Syn3Updater-3.1.0-beta.2-linux-arm64.tar.gz", p.Asset!.Name);
    }

    [Fact]
    public async Task A_windows_install_gets_the_matching_zip_once_the_app_can_apply_it()
    {
        var svc = new UpdateService(new HttpClient(new FuncHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Signature) : Ok(Manifest))), null, _ => true);
        var p = await Plan(svc, Inst(InstallKind.WindowsFolder, UpdateRoute.InPlace), os: UpdateOs.Windows, arch: UpdateArch.Arm64);
        Assert.Equal(PlanKind.Install, p.Kind);
        Assert.Equal("Syn3Updater-3.1.0-beta.2-win-arm64.zip", p.Asset!.Name);
    }

    [Fact]
    public async Task An_install_the_app_cannot_yet_apply_is_shown_instructions_not_a_button_that_would_fail()
    {
        // by default only user-level Linux tar installs are applied; Windows follows in a later step
        var p = await Plan(Svc(), Inst(InstallKind.WindowsFolder, UpdateRoute.InPlace), os: UpdateOs.Windows, arch: UpdateArch.X64);
        Assert.Equal(PlanKind.Manual, p.Kind);
        Assert.Equal("Syn3Updater-3.1.0-beta.2-win-x64.zip", p.Asset!.Name);
        Assert.Contains("cannot update this kind of install by itself yet", p.Reason);
    }

    [Theory]
    [InlineData(InstallKind.LinuxDeb, "sudo apt install ./Syn3Updater-3.1.0-beta.2-linux-x64.deb")]
    [InlineData(InstallKind.LinuxRpm, "sudo dnf install ./Syn3Updater-3.1.0-beta.2-linux-x64.rpm")]
    [InlineData(InstallKind.LinuxPacman, "sudo pacman -U Syn3Updater-3.1.0-beta.2-linux-x64.pkg.tar.zst")]
    public async Task Package_managed_installs_get_the_exact_command_not_an_install(InstallKind kind, string command)
    {
        var p = await Plan(Svc(), Inst(kind, UpdateRoute.Manual));
        Assert.Equal(PlanKind.Manual, p.Kind);
        Assert.Contains(command, p.Instructions);
        Assert.NotNull(p.Asset);
    }

    [Fact]
    public async Task Portable_and_system_unknown_installs_are_pointed_at_the_download_page()
    {
        foreach (var kind in new[] { InstallKind.LinuxPortable, InstallKind.LinuxSystemUnknown })
        {
            var p = await Plan(Svc(), Inst(kind, UpdateRoute.Manual));
            Assert.Equal(PlanKind.Manual, p.Kind);
            Assert.Contains("github.com/CyanLabs/Syn3Updater/releases", p.Instructions);
            Assert.Null(p.Asset);
        }
    }

    [Fact]
    public async Task The_mac_app_is_manual_with_the_dmg()
    {
        var p = await Plan(Svc(), Inst(InstallKind.MacApp, UpdateRoute.Manual), os: UpdateOs.MacOs, arch: UpdateArch.Arm64);
        Assert.Equal(PlanKind.Manual, p.Kind);
        Assert.Equal("Syn3Updater-3.1.0-beta.2-macos-arm64.dmg", p.Asset!.Name);
    }

    [Fact]
    public async Task A_development_run_is_only_notified_and_nothing_is_fetched()
    {
        var h = new FuncHandler(_ => Ok(Manifest));
        var p = await Plan(Svc(h: h), Inst(InstallKind.Development, UpdateRoute.None));
        Assert.Equal(PlanKind.NotifyOnly, p.Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task A_release_without_a_manifest_is_only_notified()
    {
        var h = new FuncHandler(_ => Ok(Manifest));
        Assert.Equal(PlanKind.NotifyOnly, (await Plan(Svc(h: h), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), Info(manifest: null, sig: null))).Kind);
        Assert.Equal(PlanKind.NotifyOnly, (await Plan(Svc(h: h), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), Info(sig: null))).Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task A_manifest_that_cannot_be_downloaded_is_only_notified()
    {
        var p = await Plan(Svc(_ => new HttpResponseMessage(HttpStatusCode.NotFound)), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace));
        Assert.Equal(PlanKind.NotifyOnly, p.Kind);
    }

    [Fact]
    public async Task A_tampered_manifest_is_rejected()
    {
        var bad = (byte[])Manifest.Clone(); bad[bad.Length / 2] ^= 1;
        var p = await Plan(Svc(r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Signature) : Ok(bad)), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace));
        Assert.Equal(PlanKind.Rejected, p.Kind);
        Assert.Null(p.Asset);
    }

    [Fact]
    public async Task A_missing_or_garbage_signature_is_rejected()
    {
        var p = await Plan(Svc(r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Encoding.UTF8.GetBytes("nope")) : Ok(Manifest)), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace));
        Assert.Equal(PlanKind.Rejected, p.Kind);
    }

    [Fact]
    public async Task A_manifest_signed_by_an_untrusted_key_is_rejected()
    {
        var other = new UpdateService(new HttpClient(new FuncHandler(r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Signature) : Ok(Manifest))),
            new[] { MinisignKey.Parse(File.ReadAllText(FindRepoFile(Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", "other.pub")))) });
        Assert.Equal(PlanKind.Rejected, (await Plan(other, Inst(InstallKind.LinuxTar, UpdateRoute.InPlace))).Kind);
    }

    [Fact]
    public async Task A_manifest_for_a_different_version_than_announced_is_rejected()
    {
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), Info(version: "3.2.0-beta.1"));
        Assert.Equal(PlanKind.Rejected, p.Kind);
        Assert.Contains("3.1.0-beta.2", p.Reason);
    }

    [Theory]
    [InlineData("3.1.0-beta.2")]          // already running it
    [InlineData("3.1.0")]                 // running something newer
    [InlineData("3.2.0-beta.1")]
    public async Task Never_moves_to_the_same_or_an_older_version(string current)
    {
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), current: current);
        Assert.Equal(PlanKind.Rejected, p.Kind);
    }

    [Fact]
    public async Task A_beta_release_is_not_offered_to_someone_following_stable()
    {
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), current: "3.0.0", ch: UpdateChannel.Stable);
        Assert.Equal(PlanKind.Rejected, p.Kind);
    }

    [Fact]
    public async Task An_explicit_channel_switch_may_go_to_an_older_build()
    {
        // a stable user choosing Beta is a switch to a build that is newer here, so use a running beta with a higher number to prove the older build is allowed
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), Info(sw: true), current: "3.2.0", ch: UpdateChannel.Beta);
        Assert.Equal(PlanKind.Install, p.Kind);
    }

    [Theory]
    [InlineData("https://evil.example/CyanLabs/Syn3Updater/releases/download/v1/manifest.json")]
    [InlineData("http://github.com/CyanLabs/Syn3Updater/releases/download/v1/manifest.json")]
    [InlineData("https://github.com/Other/Repo/releases/download/v1/manifest.json")]
    public async Task Manifest_addresses_outside_this_repo_are_never_fetched(string url)
    {
        var h = new FuncHandler(_ => Ok(Manifest));
        var p = await Plan(Svc(h: h), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), Info(manifest: url));
        Assert.Equal(PlanKind.Rejected, p.Kind);
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task An_oversized_manifest_is_rejected()
    {
        var p = await Plan(Svc(r => r.RequestUri!.AbsolutePath.EndsWith(".minisig") ? Ok(Signature) : Ok(new byte[2 * 1024 * 1024])), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace));
        Assert.Equal(PlanKind.Rejected, p.Kind);
    }

    [Fact]
    public async Task A_missing_file_for_this_system_is_only_notified_for_an_in_place_install()
    {
        var p = await Plan(Svc(), Inst(InstallKind.LinuxTar, UpdateRoute.InPlace), os: UpdateOs.Linux, arch: (UpdateArch)99);
        Assert.Equal(PlanKind.NotifyOnly, p.Kind);
    }

    [Fact]
    public void The_update_checker_surfaces_the_manifest_addresses_of_a_release()
    {
        var json = $$"""
        [{"tag_name":"v3.1.0-beta.2","name":"Syn3 Updater 3.1.0-beta.2","prerelease":true,"draft":false,"html_url":"https://github.com/x/y",
          "assets":[{"name":"Syn3Updater-3.1.0-beta.2-linux-x64.tar.gz","browser_download_url":"{{Base}}a.tar.gz"},
                    {"name":"manifest.json","browser_download_url":"{{Base}}manifest.json"},
                    {"name":"manifest.json.minisig","browser_download_url":"{{Base}}manifest.json.minisig"}]}]
        """;
        var u = UpdateChecker.Pick(json, "3.0.0-test9", UpdateChannel.Beta)!;
        Assert.Equal(Base + "manifest.json", u.ManifestUrl);
        Assert.Equal(Base + "manifest.json.minisig", u.SignatureUrl);
    }

    [Fact]
    public void A_release_with_only_half_the_pair_has_neither()
    {
        var json = $$"""[{"tag_name":"v3.1.0","name":"x","prerelease":false,"draft":false,"html_url":"u","assets":[{"name":"manifest.json","browser_download_url":"{{Base}}manifest.json"}]}]""";
        var u = UpdateChecker.Pick(json, "3.0.0", UpdateChannel.Stable)!;
        Assert.Null(u.SignatureUrl);
    }

    [Fact]
    public void Releases_without_assets_still_work()
    {
        var u = UpdateChecker.Pick("""[{"tag_name":"v3.1.0","name":"x","prerelease":false,"draft":false,"html_url":"u"}]""", "3.0.0", UpdateChannel.Stable)!;
        Assert.Null(u.ManifestUrl);
    }
}
