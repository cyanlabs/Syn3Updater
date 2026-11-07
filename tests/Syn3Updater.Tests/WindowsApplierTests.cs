using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;
using Xunit;

namespace Syn3Updater.Tests;

public class WindowsApplierTests : IDisposable
{
    private readonly TempDir _t = new();
    private string Install => _t.Sub("install");
    private string ZipPath => _t.Sub("update.zip");
        private string Prefs => _t.Sub("programdata", "launcherPrefs.json");
    public void Dispose() => _t.Dispose();

    private static readonly (string, string)[] NewPackage =
    {
        ("Syn3Updater-3.1.0-win-x64/Syn3Updater.exe", "NEW-APP"), ("Syn3Updater-3.1.0-win-x64/Launcher.exe", "NEW-LAUNCHER"),
        ("Syn3Updater-3.1.0-win-x64/Syn3Updater.Updater.exe", "NEW-HELPER"), ("Syn3Updater-3.1.0-win-x64/LICENSE", "gpl")
    };

    private void Installed(params (string, string)[] files)
    {
        Directory.CreateDirectory(Install);
        foreach (var (p, c) in files) { var f = Path.Combine(Install, p); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, c); }
    }

    private WindowsUpdate Request(byte[]? zip = null, string tag = "v3.1.0", string version = "3.1.0", UpdateChannel ch = UpdateChannel.Stable, long? size = null, string? sha = null, string? prefs = null)
    {
        zip ??= Zips.Make(NewPackage);
        File.WriteAllBytes(ZipPath, zip);
        return new WindowsUpdate(Install, ZipPath, sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), size ?? zip.Length, tag, version, ch, prefs);
    }

    /// <summary>Applies the update; a failure is the message of the exception (the previous version is in place).</summary>
    private static (bool Success, string Message) Run(WindowsUpdate u, ISwapFileSystem? fs = null, RetryPolicy? retry = null, Action<string>? log = null)
    {
        try { WindowsApplier.Apply(u, fs, retry, log); return (true, "ok"); }
        catch (UpdateApplyException e) { return (false, e.Message); }
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(Install, rel));

    [Fact]
    public void A_good_update_replaces_the_files_removes_stale_ones_and_keeps_user_files()
    {
        Installed(("Syn3Updater.exe", "OLD-APP"), ("Launcher.exe", "OLD-LAUNCHER"), ("old.dll", "d"), ("notes.txt", "mine"), ("Languages/en-US.json", "{}"));
        var r = Run(Request());
        Assert.True(r.Success, r.Message);
        Assert.Equal("NEW-APP", Read("Syn3Updater.exe")); Assert.Equal("NEW-LAUNCHER", Read("Launcher.exe")); Assert.Equal("NEW-HELPER", Read("Syn3Updater.Updater.exe"));
        Assert.False(File.Exists(Path.Combine(Install, "old.dll")));                    // no install manifest yet: the old Launcher's rule removes .dll files and Languages
        Assert.False(Directory.Exists(Path.Combine(Install, "Languages")));
        Assert.Equal("mine", Read("notes.txt"));
        Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName))); Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.BackupDirName)));
    }

    [Fact]
    public void The_install_manifest_is_written_in_the_Launchers_format_so_the_next_update_removes_exactly_what_this_one_installed()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Assert.True(Run(Request()).Success);
        var m = InstallManifest.Load(Install)!;
        Assert.Equal("3.1.0.0", m.Version);
        Assert.Equal(new[] { "LICENSE", "Launcher.exe", "Syn3Updater.Updater.exe", "Syn3Updater.exe" }, m.Files.OrderBy(x => x, StringComparer.Ordinal));
        // a later package that drops the helper: only the files the manifest knows are removed
        File.WriteAllText(Path.Combine(Install, "user.dll"), "not ours");
        var next = Zips.Make(("Syn3Updater.exe", "N2"), ("Launcher.exe", "L2"), ("LICENSE", "gpl"));
        Assert.True(Run(Request(next, "v3.2.0", "3.2.0")).Success);
        Assert.False(File.Exists(Path.Combine(Install, "Syn3Updater.Updater.exe")));
        Assert.True(File.Exists(Path.Combine(Install, "user.dll")));
    }

    [Fact]
    public void A_zip_that_does_not_match_the_signed_hash_is_refused_and_nothing_changes()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = Run(Request(sha: new string('0', 64)));
        Assert.False(r.Success); Assert.Contains("signed checksum", r.Message);
        Assert.Equal("OLD", Read("Syn3Updater.exe")); Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName)));
    }

    [Fact]
    public void A_zip_of_the_wrong_size_is_refused()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = Run(Request(size: 5));
        Assert.False(r.Success); Assert.Contains("size", r.Message); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_missing_zip_or_install_folder_fails_cleanly()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var req = Request(); File.Delete(ZipPath);
        Assert.Contains("missing", Run(req).Message);
        Assert.Contains("does not contain", Run(req with { InstallDir = _t.Sub("nowhere") }).Message);
    }

    [Fact]
    public void A_corrupt_zip_with_a_matching_hash_still_fails_without_touching_the_install()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var junk = new byte[] { 1, 2, 3, 4, 5, 6 };
        var r = Run(Request(junk));
        Assert.False(r.Success); Assert.Equal("OLD", Read("Syn3Updater.exe")); Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName)));
    }

    [Fact]
    public void A_package_without_the_app_is_refused()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = Run(Request(Zips.Make(("readme.txt", "x"))));
        Assert.False(r.Success); Assert.Contains("Syn3Updater.exe", r.Message); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_zip_with_an_escaping_path_is_refused_and_writes_nothing_outside()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = Run(Request(Zips.Make(("Syn3Updater.exe", "N"), ("../evil.txt", "x"))));
        Assert.False(r.Success); Assert.False(File.Exists(_t.Sub("evil.txt"))); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_failure_part_way_restores_the_previous_version_and_says_so()
    {
        Installed(("Syn3Updater.exe", "OLD-APP"), ("Launcher.exe", "OLD-LAUNCHER"));
        var fs = new FaultyFileSystem { FailMove = (src, dst) => src.EndsWith("LICENSE") && !dst.Contains(InstallTransaction.BackupDirName) };
        var r = Run(Request(), fs: fs, retry: new RetryPolicy(2, 0));
        Assert.False(r.Success); Assert.Contains("previous version was restored", r.Message);
        Assert.Equal("OLD-APP", Read("Syn3Updater.exe")); Assert.Equal("OLD-LAUNCHER", Read("Launcher.exe"));
        Assert.False(File.Exists(Path.Combine(Install, "LICENSE")));
        Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName)));
    }

    [Fact]
    public void An_update_interrupted_earlier_is_rolled_back_before_this_one_starts()
    {
        Installed(("Syn3Updater.exe", "HALF-INSTALLED"));
        var backup = Path.Combine(Install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "ORIGINAL-LAUNCHER");
        var logs = new List<string>();
        Assert.True(Run(Request(), log: logs.Add).Success);
        Assert.Contains(logs, l => l.Contains("interrupted"));
        Assert.Equal("NEW-LAUNCHER", Read("Launcher.exe"));          // restored, then replaced by this update
    }

    [Fact]
    public void The_zip_is_locked_against_changes_while_it_is_being_installed()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Exception? swapIn = null;
        var fs = new HookFileSystem(() => { try { using var w = new FileStream(ZipPath, FileMode.Open, FileAccess.Write, FileShare.None); } catch (IOException e) { swapIn = e; } });
        Assert.True(Run(Request(), fs: fs).Success);
        Assert.IsType<IOException>(swapIn);                           // nobody could modify the verified zip between the check and the install
    }

    private sealed class HookFileSystem : ISwapFileSystem
    {
        private readonly RealSwapFileSystem _r = new(); private readonly Action _hook; private bool _done;
        public HookFileSystem(Action hook) => _hook = hook;
        public bool FileExists(string p) => _r.FileExists(p); public bool DirectoryExists(string p) => _r.DirectoryExists(p); public void CreateDirectory(string p) => _r.CreateDirectory(p);
        public void WriteAllText(string p, string t) => _r.WriteAllText(p, t);
        public DateTime GetCreationTimeUtc(string p) => _r.GetCreationTimeUtc(p); public DateTime GetLastWriteTimeUtc(string p) => _r.GetLastWriteTimeUtc(p);
        public void DeleteFile(string p) => _r.DeleteFile(p); public void DeleteDirectory(string p) => _r.DeleteDirectory(p);
        public void MoveFile(string s, string d, bool o) { if (!_done) { _done = true; _hook(); } _r.MoveFile(s, d, o); }
    }

    // ---- the Launcher's install record ----

    [Fact]
    public void An_existing_Launcher_is_told_what_was_installed_without_losing_the_channel_the_user_follows()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{\"ReleaseBranch\":1,\"ReleaseInstalled\":\"3.0.0.0\",\"ReleaseTypeInstalled\":1}");
        Assert.True(Run(Request(tag: "v3.1.0-beta.2", version: "3.1.0-beta.2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
        var o = JsonNode.Parse(File.ReadAllText(Prefs))!.AsObject();
        Assert.Equal("3.1.0.0", (string)o["ReleaseInstalled"]!);        // the tag as four numbers, exactly as the Launcher records it
        Assert.Equal(1, (int)o["ReleaseTypeInstalled"]!); Assert.Equal(1, (int)o["ReleaseBranch"]!);
    }

    [Fact]
    public void The_record_follows_the_release_tag_not_the_app_version()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{\"ReleaseBranch\":1}");
        // a test build published under a legacy tag: the Launcher compares these numbers, so they must be the tag's
        Assert.True(Run(Request(tag: "2.99.99.7", version: "3.1.0-test2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
        Assert.Equal("2.99.99.7", (string)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseInstalled"]!);
    }

    [Fact]
    public void A_nightly_is_recorded_as_the_Launchers_third_slot()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{}");
        Assert.True(Run(Request(tag: "3.1.0-nightly.20261108.5", version: "3.1.0-nightly.20261108.5", ch: UpdateChannel.Nightly, prefs: Prefs)).Success);
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseTypeInstalled"]!);
    }

    [Fact]
    public void No_Launcher_anywhere_means_no_record_is_written()
    {
        Installed(("Syn3Updater.exe", "OLD"));                         // no prefs file, and the package below has no Launcher.exe either
        Assert.True(Run(Request(Zips.Make(("Syn3Updater.exe", "N"), ("LICENSE", "g")), prefs: Prefs)).Success);
        Assert.False(File.Exists(Prefs));
    }

    [Fact]
    public void A_package_that_brings_a_Launcher_gets_a_record_so_that_Launcher_does_not_reinstall_or_switch_channel()
    {
        Installed(("Syn3Updater.exe", "OLD"));                         // started directly all along: no prefs file yet
        Assert.True(Run(Request(tag: "v3.1.0-beta.2", version: "3.1.0-beta.2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
        var o = JsonNode.Parse(File.ReadAllText(Prefs))!.AsObject();
        Assert.Equal("3.1.0.0", (string)o["ReleaseInstalled"]!);
        Assert.Equal(1, (int)o["ReleaseBranch"]!);                     // follows the channel installed, not the default Stable
        Assert.Equal(1, (int)o["ReleaseTypeInstalled"]!);
    }

    [Fact]
    public void A_tag_that_is_not_a_version_leaves_the_record_alone()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{\"ReleaseInstalled\":\"3.0.0.0\"}");
        Assert.True(Run(Request(tag: "nightly", version: "3.1.0-nightly.1.2", ch: UpdateChannel.Nightly, prefs: Prefs)).Success);
        Assert.Equal("3.0.0.0", (string)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseInstalled"]!);
    }

    [Theory]
    [InlineData("v3.0.0", "3.0.0.0")] [InlineData("3.0.0.0", "3.0.0.0")] [InlineData("3.1.0-beta.2", "3.1.0.0")] [InlineData("2.99.99.5", "2.99.99.5")] [InlineData("v4.2", "4.2.0.0")]
    [InlineData("nightly", null)] [InlineData("", null)] [InlineData(null, null)]
    public void Tags_are_recorded_as_four_numbers(string? tag, string? expected) => Assert.Equal(expected, LauncherPrefs.NumericTag(tag));
}
