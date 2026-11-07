using System.Text;
using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;

namespace Syn3Updater.Tests;

public class ReleaseManifestTests
{
    private static readonly string Sha = new('a', 64);
    private static string Url(string name, string ver = "3.1.0") => $"https://github.com/CyanLabs/Syn3Updater/releases/download/v{ver}/{name}";

    private static object Asset(string os = "windows", string arch = "x64", string kind = "zip", string name = "Syn3Updater-3.1.0-win-x64.zip", string? url = null, long size = 1000, string? sha = null) =>
        new { os, arch, kind, name, url = url ?? Url(name), size, sha256 = sha ?? Sha };

    private static byte[] Json(object? assets = null, string version = "3.1.0", string channel = "Stable", int schema = 1) =>
        JsonSerializer.SerializeToUtf8Bytes(new { schema, version, channel, published = "2026-11-07T00:00:00Z", notes = "Fixes things", assets = assets ?? new[] { Asset() } });

    [Fact]
    public void A_good_manifest_is_read()
    {
        var m = ReleaseManifest.Parse(Json());
        Assert.Equal("3.1.0", m.Version);
        Assert.Equal(UpdateChannel.Stable, m.Channel);
        Assert.Equal("Fixes things", m.Notes);
        var a = m.Find(UpdateOs.Windows, UpdateArch.X64, AssetKind.Zip)!;
        Assert.Equal(1000, a.Size);
        Assert.Null(m.Find(UpdateOs.Linux, UpdateArch.X64, AssetKind.TarGz));
    }

    [Fact]
    public void The_right_file_is_chosen_per_os_arch_and_kind()
    {
        var m = ReleaseManifest.Parse(Json(new[]
        {
            Asset("windows", "x64", "zip", "Syn3Updater-3.1.0-win-x64.zip"), Asset("windows", "arm64", "zip", "Syn3Updater-3.1.0-win-arm64.zip"),
            Asset("linux", "x64", "targz", "Syn3Updater-3.1.0-linux-x64.tar.gz"), Asset("linux", "x64", "deb", "Syn3Updater-3.1.0-linux-x64.deb"),
            Asset("macos", "arm64", "app-zip", "Syn3Updater-3.1.0-macos-arm64.zip")
        }));
        Assert.EndsWith("win-arm64.zip", m.Find(UpdateOs.Windows, UpdateArch.Arm64, AssetKind.Zip)!.Name);
        Assert.EndsWith("linux-x64.deb", m.Find(UpdateOs.Linux, UpdateArch.X64, AssetKind.Deb)!.Name);
        Assert.Null(m.Find(UpdateOs.Linux, UpdateArch.Arm64, AssetKind.TarGz));
        Assert.Null(m.Find(UpdateOs.MacOs, UpdateArch.X64, AssetKind.MacAppZip));
    }

    [Theory]
    [InlineData("http://github.com/CyanLabs/Syn3Updater/releases/download/v3.1.0/a.zip")]          // not https
    [InlineData("https://evil.example/CyanLabs/Syn3Updater/releases/download/v3.1.0/a.zip")]       // other host
    [InlineData("https://github.com/Evil/Syn3Updater/releases/download/v3.1.0/a.zip")]             // other repository
    [InlineData("https://github.com/CyanLabs/Syn3Updater/releases/download/../../../evil/a.zip")]  // path trick
    [InlineData("file:///etc/passwd")]
    [InlineData("not a url")]
    public void Download_addresses_outside_this_repos_releases_are_refused(string url) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(name: "a.zip", url: url) })));

    [Theory]
    [InlineData("../a.zip")]
    [InlineData("a/b.zip")]
    [InlineData("a\\b.zip")]
    [InlineData(".hidden")]
    [InlineData("a..b.zip")]
    [InlineData("")]
    public void Unsafe_file_names_are_refused(string name) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(name: name, url: Url("a.zip")) })));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(700L * 1024 * 1024)]
    public void Implausible_sizes_are_refused(long size) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(size: size) })));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]       // upper case is normalised, so this one is fine: see below
    public void Bad_hashes_are_refused_unless_they_are_64_hex(string sha)
    {
        if (sha.Length == 64) { Assert.NotNull(ReleaseManifest.Parse(Json(new[] { Asset(sha: sha) }))); return; }
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(sha: sha) })));
    }

    [Theory]
    [InlineData("3.1.0", "Beta")]                       // a release version in the Beta channel
    [InlineData("3.1.0-beta.1", "Stable")]
    [InlineData("3.1.0-nightly.20261108.5", "Beta")]
    [InlineData("v3.1.0", "Stable")]
    [InlineData("nonsense", "Stable")]
    public void Version_and_channel_must_agree(string version, string channel) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(version: version, channel: channel)));

    [Fact]
    public void Prerelease_versions_map_to_their_channels()
    {
        Assert.Equal(UpdateChannel.Beta, ReleaseManifest.Parse(Json(version: "3.1.0-beta.2", channel: "Beta")).Channel);
        Assert.Equal(UpdateChannel.Nightly, ReleaseManifest.Parse(Json(version: "3.1.0-nightly.20261108.5", channel: "Nightly")).Channel);
    }

    [Fact]
    public void A_newer_schema_is_refused() => Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(schema: 2)));

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"schema\":1,\"version\":\"3.1.0\",\"channel\":\"Stable\",\"assets\":[]}")]
    [InlineData("not json")]
    [InlineData("")]
    public void Garbage_is_a_format_error_never_another_exception(string json) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void Duplicate_assets_for_one_target_are_refused() =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(), Asset(name: "other.zip") })));

    [Theory]
    [InlineData("beos", "x64", "zip")]
    [InlineData("windows", "mips", "zip")]
    [InlineData("windows", "x64", "exe")]
    public void Unknown_targets_are_refused(string os, string arch, string kind) =>
        Assert.Throws<FormatException>(() => ReleaseManifest.Parse(Json(new[] { Asset(os, arch, kind) })));

    // ---- what may be installed ----

    private static ReleaseManifest M(string version, string channel) => ReleaseManifest.Parse(Json(version: version, channel: channel));

    [Theory]
    [InlineData("3.0.0", "3.0.1", "Stable", false, true)]
    [InlineData("3.0.1", "3.0.1", "Stable", false, false)]           // same version
    [InlineData("3.0.2", "3.0.1", "Stable", false, false)]           // older: never automatically
    [InlineData("3.0.0-test9", "3.0.0", "Stable", false, true)]      // a test build moves to the release
    [InlineData("3.1.0-beta.2", "3.0.0", "Stable", false, false)]    // wrong channel for the request
    public void Updates_only_move_forward_within_the_wanted_channel(string current, string target, string channel, bool sw, bool expected)
    {
        var ch = Channels.Of(target);
        Assert.Equal(expected, ManifestTrust.MayInstall(M(target, ch.ToString()), current, Enum.Parse<UpdateChannel>(channel), sw));
    }

    [Fact]
    public void Older_builds_are_installed_only_for_an_explicit_channel_switch()
    {
        var stable = M("3.0.0", "Stable");
        Assert.False(ManifestTrust.MayInstall(stable, "3.1.0-beta.2", UpdateChannel.Stable, explicitSwitch: false));
        Assert.True(ManifestTrust.MayInstall(stable, "3.1.0-beta.2", UpdateChannel.Stable, explicitSwitch: true));
        // an explicit switch never allows going backwards inside the same channel
        Assert.False(ManifestTrust.MayInstall(M("3.0.0", "Stable"), "3.0.1", UpdateChannel.Stable, explicitSwitch: true));
    }

    // ---- signature + manifest together (fixtures signed by a throwaway test key) ----

    private static string Fx(string name)
    {
        var relative = Path.Combine("tests", "Syn3Updater.Tests", "Fixtures", "Minisign", name);
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
    private static readonly MinisignKey[] TestKeys = { MinisignKey.Parse(File.ReadAllText(Fx("manifest-test.pub"))) };

    [Fact]
    public void A_correctly_signed_manifest_opens()
    {
        var m = ManifestTrust.Open(File.ReadAllBytes(Fx("manifest.json")), File.ReadAllText(Fx("manifest.json.minisig")), TestKeys);
        Assert.Equal("3.1.0", m.Version);
        Assert.Equal("Test release", m.Notes);
    }

    [Fact]
    public void A_manifest_signed_for_another_version_is_refused()
    {
        var ex = Assert.Throws<FormatException>(() => ManifestTrust.Open(File.ReadAllBytes(Fx("manifest.json")), File.ReadAllText(Fx("manifest-wrongversion.minisig")), TestKeys));
        Assert.Contains("different version", ex.Message);
    }

    [Fact]
    public void A_tampered_manifest_is_refused_even_though_the_signature_is_real()
    {
        var bytes = File.ReadAllBytes(Fx("manifest.json"));
        var text = Encoding.UTF8.GetString(bytes).Replace("\"size\":1234", "\"size\":1235");
        var ex = Assert.Throws<FormatException>(() => ManifestTrust.Open(Encoding.UTF8.GetBytes(text), File.ReadAllText(Fx("manifest.json.minisig")), TestKeys));
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void The_real_release_keys_refuse_a_test_signed_manifest() =>
        Assert.Throws<FormatException>(() => ManifestTrust.Open(File.ReadAllBytes(Fx("manifest.json")), File.ReadAllText(Fx("manifest.json.minisig")), UpdateKeys.Trusted));

    [Fact]
    public void A_junk_signature_is_refused_before_the_manifest_is_read() =>
        Assert.Contains("signature", Assert.Throws<FormatException>(() =>
            ManifestTrust.Open(Json(), "untrusted comment: x\nAAAA\ntrusted comment: version:3.1.0\nAAAA", UpdateKeys.Trusted)).Message);

    /// <summary>A manifest made by scripts/make-manifest.sh and signed by scripts/sign-manifest.sh with the REAL primary release key: what CI produces must open with the keys the app embeds.</summary>
    [Fact]
    public void A_manifest_made_and_signed_by_the_release_scripts_opens_with_the_embedded_keys()
    {
        var m = ManifestTrust.Open(File.ReadAllBytes(Fx("release-manifest.json")), File.ReadAllText(Fx("release-manifest.json.minisig")), UpdateKeys.Trusted);
        Assert.Equal("3.1.0-beta.2", m.Version);
        Assert.Equal(UpdateChannel.Beta, m.Channel);
        Assert.Equal(12, m.Assets.Count);
        Assert.NotNull(m.Find(UpdateOs.Windows, UpdateArch.Arm64, AssetKind.Zip));
        Assert.NotNull(m.Find(UpdateOs.Linux, UpdateArch.X64, AssetKind.ArchPkg));
        Assert.NotNull(m.Find(UpdateOs.MacOs, UpdateArch.Arm64, AssetKind.Dmg));
    }

    [Fact]
    public void The_public_key_files_CI_verifies_with_match_the_keys_compiled_into_the_app()
    {
        var root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Fx("manifest-test.pub"))))!;      // .../tests/Syn3Updater.Tests
        var repo = Path.GetFullPath(Path.Combine(root, "..", ".."));
        var primary = MinisignKey.Parse(File.ReadAllText(Path.Combine(repo, "packaging", "update", "primary.pub")));
        var backup = MinisignKey.Parse(File.ReadAllText(Path.Combine(repo, "packaging", "update", "backup.pub")));
        Assert.Equal(UpdateKeys.Trusted[0].PublicKey, primary.PublicKey);
        Assert.Equal(UpdateKeys.Trusted[1].PublicKey, backup.PublicKey);
    }
}

public class ReviewFindingsPlanningTests
{
    private static ReleaseManifest M(string version, string channel) => ReleaseManifest.Parse(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 1, version, channel, published = "2026-11-07T00:00:00Z", notes = "", assets = new[] { new { os = "linux", arch = "x64", kind = "targz", name = "a.tar.gz", url = "https://github.com/CyanLabs/Syn3Updater/releases/download/v1/a.tar.gz", size = 10, sha256 = new string('a', 64) } }
    }));

    [Theory]
    [InlineData(UpdateChannel.Stable, UpdateChannel.Stable, true)] [InlineData(UpdateChannel.Stable, UpdateChannel.Beta, false)] [InlineData(UpdateChannel.Stable, UpdateChannel.Nightly, false)]
    [InlineData(UpdateChannel.Beta, UpdateChannel.Stable, true)] [InlineData(UpdateChannel.Beta, UpdateChannel.Beta, true)] [InlineData(UpdateChannel.Beta, UpdateChannel.Nightly, false)]
    [InlineData(UpdateChannel.Nightly, UpdateChannel.Stable, false)] [InlineData(UpdateChannel.Nightly, UpdateChannel.Beta, false)] [InlineData(UpdateChannel.Nightly, UpdateChannel.Nightly, true)]
    public void A_followed_channel_accepts_the_releases_the_update_check_offers_for_it(UpdateChannel wanted, UpdateChannel release, bool expected) => Assert.Equal(expected, Channels.Accepts(wanted, release));

    [Fact]
    public void Every_release_the_update_check_offers_is_a_release_the_planner_accepts()
    {
        foreach (var wanted in new[] { UpdateChannel.Stable, UpdateChannel.Beta, UpdateChannel.Nightly })
            foreach (var (tag, version) in new[] { ("v3.0.0", "3.0.0"), ("v3.1.0-beta.2", "3.1.0-beta.2"), ("nightly", "3.1.0-nightly.20261108.5") })
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new[] { new { tag_name = tag, name = "Syn3 Updater " + version, prerelease = version.Contains('-'), draft = false, html_url = "u" } });
                var offered = UpdateChecker.Pick(json, "2.0.0", wanted) is not null;
                var m = M(version, Channels.Of(version).ToString());
                Assert.True(!offered || ManifestTrust.MayInstall(m, "2.0.0", wanted, false), $"{wanted} is offered {version} but the planner would refuse it");
            }
    }

    [Fact]
    public void A_beta_user_is_offered_the_stable_release_to_install()
    {
        Assert.True(ManifestTrust.MayInstall(M("3.0.0", "Stable"), "3.0.0-test9", UpdateChannel.Beta, false));
        Assert.False(ManifestTrust.MayInstall(M("3.0.0", "Stable"), "3.0.0-test9", UpdateChannel.Nightly, false));
    }
}
