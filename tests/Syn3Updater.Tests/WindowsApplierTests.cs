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
    private string ResultFile => _t.Sub("data", "update-result.json");
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

    private ApplyRequest Request(byte[]? zip = null, string tag = "v3.1.0", string version = "3.1.0", UpdateChannel ch = UpdateChannel.Stable, long? size = null, string? sha = null, int pid = 0, string? prefs = null)
    {
        zip ??= Zips.Make(NewPackage);
        File.WriteAllBytes(ZipPath, zip);
        return new ApplyRequest(Install, ZipPath, sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant(), size ?? zip.Length, tag, version, ch, pid, Path.Combine(Install, "Syn3Updater.exe"), ResultFile, prefs);
    }

    private string Read(string rel) => File.ReadAllText(Path.Combine(Install, rel));

    [Fact]
    public void A_good_update_replaces_the_files_removes_stale_ones_and_keeps_user_files()
    {
        Installed(("Syn3Updater.exe", "OLD-APP"), ("Launcher.exe", "OLD-LAUNCHER"), ("old.dll", "d"), ("notes.txt", "mine"), ("Languages/en-US.json", "{}"));
        var r = WindowsApplier.Run(Request());
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
        Assert.True(WindowsApplier.Run(Request()).Success);
        var m = InstallManifest.Load(Install)!;
        Assert.Equal("3.1.0.0", m.Version);
        Assert.Equal(new[] { "LICENSE", "Launcher.exe", "Syn3Updater.Updater.exe", "Syn3Updater.exe" }, m.Files.OrderBy(x => x, StringComparer.Ordinal));
        // a later package that drops the helper: only the files the manifest knows are removed
        File.WriteAllText(Path.Combine(Install, "user.dll"), "not ours");
        var next = Zips.Make(("Syn3Updater.exe", "N2"), ("Launcher.exe", "L2"), ("LICENSE", "gpl"));
        Assert.True(WindowsApplier.Run(Request(next, "v3.2.0", "3.2.0")).Success);
        Assert.False(File.Exists(Path.Combine(Install, "Syn3Updater.Updater.exe")));
        Assert.True(File.Exists(Path.Combine(Install, "user.dll")));
    }

    [Fact]
    public void A_zip_that_does_not_match_the_signed_hash_is_refused_and_nothing_changes()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = WindowsApplier.Run(Request(sha: new string('0', 64)));
        Assert.False(r.Success); Assert.Contains("signed checksum", r.Message);
        Assert.Equal("OLD", Read("Syn3Updater.exe")); Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName)));
    }

    [Fact]
    public void A_zip_of_the_wrong_size_is_refused()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = WindowsApplier.Run(Request(size: 5));
        Assert.False(r.Success); Assert.Contains("size", r.Message); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_missing_zip_or_install_folder_fails_cleanly()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var req = Request(); File.Delete(ZipPath);
        Assert.Contains("missing", WindowsApplier.Run(req).Message);
        Assert.Contains("does not contain", WindowsApplier.Run(req with { InstallDir = _t.Sub("nowhere") }).Message);
    }

    [Fact]
    public void If_the_app_does_not_close_nothing_is_touched()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = WindowsApplier.Run(Request(pid: 4242), waitForExit: (_, _) => false);
        Assert.False(r.Success); Assert.Contains("did not close", r.Message); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void The_helper_waits_for_the_given_process_and_proceeds_once_it_is_gone()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        int? waited = null;
        Assert.True(WindowsApplier.Run(Request(pid: 777), waitForExit: (pid, t) => { waited = pid; return true; }).Success);
        Assert.Equal(777, waited);
        Assert.Equal("NEW-APP", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_process_that_no_longer_exists_counts_as_closed() =>
        Assert.True(ApplierWait(int.MaxValue - 5));
    private static bool ApplierWait(int pid)
    {
        using var t = new TempDir(); var install = t.Sub("i"); Directory.CreateDirectory(install); File.WriteAllText(Path.Combine(install, "Syn3Updater.exe"), "OLD");
        var zip = Zips.Make(NewPackage); File.WriteAllBytes(t.Sub("z.zip"), zip);
        return WindowsApplier.Run(new ApplyRequest(install, t.Sub("z.zip"), Convert.ToHexString(SHA256.HashData(zip)), zip.Length, "v3.1.0", "3.1.0", UpdateChannel.Stable, pid, Path.Combine(install, "Syn3Updater.exe"), t.Sub("r.json"), null)).Success;
    }

    [Fact]
    public void A_corrupt_zip_with_a_matching_hash_still_fails_without_touching_the_install()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var junk = new byte[] { 1, 2, 3, 4, 5, 6 };
        var r = WindowsApplier.Run(Request(junk));
        Assert.False(r.Success); Assert.Equal("OLD", Read("Syn3Updater.exe")); Assert.False(Directory.Exists(Path.Combine(Install, InstallTransaction.StagingDirName)));
    }

    [Fact]
    public void A_package_without_the_app_is_refused()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = WindowsApplier.Run(Request(Zips.Make(("readme.txt", "x"))));
        Assert.False(r.Success); Assert.Contains("Syn3Updater.exe", r.Message); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_zip_with_an_escaping_path_is_refused_and_writes_nothing_outside()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        var r = WindowsApplier.Run(Request(Zips.Make(("Syn3Updater.exe", "N"), ("../evil.txt", "x"))));
        Assert.False(r.Success); Assert.False(File.Exists(_t.Sub("evil.txt"))); Assert.Equal("OLD", Read("Syn3Updater.exe"));
    }

    [Fact]
    public void A_failure_part_way_restores_the_previous_version_and_says_so()
    {
        Installed(("Syn3Updater.exe", "OLD-APP"), ("Launcher.exe", "OLD-LAUNCHER"));
        var fs = new FaultyFileSystem { FailMove = (src, dst) => src.EndsWith("LICENSE") && !dst.Contains(InstallTransaction.BackupDirName) };
        var r = WindowsApplier.Run(Request(), fs: fs, retry: new RetryPolicy(2, 0));
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
        Assert.True(WindowsApplier.Run(Request(), log: logs.Add).Success);
        Assert.Contains(logs, l => l.Contains("interrupted"));
        Assert.Equal("NEW-LAUNCHER", Read("Launcher.exe"));          // restored, then replaced by this update
    }

    [Fact]
    public void The_zip_is_locked_against_changes_while_it_is_being_installed()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Exception? swapIn = null;
        var fs = new HookFileSystem(() => { try { using var w = new FileStream(ZipPath, FileMode.Open, FileAccess.Write, FileShare.None); } catch (IOException e) { swapIn = e; } });
        Assert.True(WindowsApplier.Run(Request(), fs: fs).Success);
        Assert.IsType<IOException>(swapIn);                           // nobody could modify the verified zip between the check and the install
    }

    private sealed class HookFileSystem : ISwapFileSystem
    {
        private readonly RealSwapFileSystem _r = new(); private readonly Action _hook; private bool _done;
        public HookFileSystem(Action hook) => _hook = hook;
        public bool FileExists(string p) => _r.FileExists(p); public bool DirectoryExists(string p) => _r.DirectoryExists(p); public void CreateDirectory(string p) => _r.CreateDirectory(p);
        public void DeleteFile(string p) => _r.DeleteFile(p); public void DeleteDirectory(string p) => _r.DeleteDirectory(p);
        public void MoveFile(string s, string d, bool o) { if (!_done) { _done = true; _hook(); } _r.MoveFile(s, d, o); }
    }

    // ---- the Launcher's install record ----

    [Fact]
    public void An_existing_Launcher_is_told_what_was_installed_without_losing_the_channel_the_user_follows()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{\"ReleaseBranch\":1,\"ReleaseInstalled\":\"3.0.0.0\",\"ReleaseTypeInstalled\":1}");
        Assert.True(WindowsApplier.Run(Request(tag: "v3.1.0-beta.2", version: "3.1.0-beta.2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
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
        Assert.True(WindowsApplier.Run(Request(tag: "2.99.99.7", version: "3.1.0-test2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
        Assert.Equal("2.99.99.7", (string)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseInstalled"]!);
    }

    [Fact]
    public void A_nightly_is_recorded_as_the_Launchers_third_slot()
    {
        Installed(("Syn3Updater.exe", "OLD"));
        Directory.CreateDirectory(Path.GetDirectoryName(Prefs)!); File.WriteAllText(Prefs, "{}");
        Assert.True(WindowsApplier.Run(Request(tag: "3.1.0-nightly.20261108.5", version: "3.1.0-nightly.20261108.5", ch: UpdateChannel.Nightly, prefs: Prefs)).Success);
        Assert.Equal(2, (int)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseTypeInstalled"]!);
    }

    [Fact]
    public void No_Launcher_anywhere_means_no_record_is_written()
    {
        Installed(("Syn3Updater.exe", "OLD"));                         // no prefs file, and the package below has no Launcher.exe either
        Assert.True(WindowsApplier.Run(Request(Zips.Make(("Syn3Updater.exe", "N"), ("LICENSE", "g")), prefs: Prefs)).Success);
        Assert.False(File.Exists(Prefs));
    }

    [Fact]
    public void A_package_that_brings_a_Launcher_gets_a_record_so_that_Launcher_does_not_reinstall_or_switch_channel()
    {
        Installed(("Syn3Updater.exe", "OLD"));                         // started directly all along: no prefs file yet
        Assert.True(WindowsApplier.Run(Request(tag: "v3.1.0-beta.2", version: "3.1.0-beta.2", ch: UpdateChannel.Beta, prefs: Prefs)).Success);
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
        Assert.True(WindowsApplier.Run(Request(tag: "nightly", version: "3.1.0-nightly.1.2", ch: UpdateChannel.Nightly, prefs: Prefs)).Success);
        Assert.Equal("3.0.0.0", (string)JsonNode.Parse(File.ReadAllText(Prefs))!["ReleaseInstalled"]!);
    }

    [Theory]
    [InlineData("v3.0.0", "3.0.0.0")] [InlineData("3.0.0.0", "3.0.0.0")] [InlineData("3.1.0-beta.2", "3.1.0.0")] [InlineData("2.99.99.5", "2.99.99.5")] [InlineData("v4.2", "4.2.0.0")]
    [InlineData("nightly", null)] [InlineData("", null)] [InlineData(null, null)]
    public void Tags_are_recorded_as_four_numbers(string? tag, string? expected) => Assert.Equal(expected, LauncherPrefs.NumericTag(tag));

    // ---- the request and the result ----

    private static ApplyRequest Sample() => new("/opt/Syn3Updater", "/tmp/z.zip", new string('a', 64), 1234, "v3.1.0", "3.1.0", UpdateChannel.Beta, 4321, "/x/Syn3Updater.exe", "/d/r.json", "/p/launcherPrefs.json");

    [Fact]
    public void A_request_survives_the_trip_through_the_command_line()
    {
        var s = Sample();
        Assert.Equal(s, ApplyRequest.Decode(s.Encode()));
        Assert.Null(ApplyRequest.Decode((s with { LauncherPrefsPath = null }).Encode()).LauncherPrefsPath);
    }

    [Theory]
    [InlineData("")] [InlineData("not base64!!")] [InlineData("e30=")] [InlineData("W10=")]
    public void Garbage_requests_are_refused(string text) => Assert.Throws<FormatException>(() => ApplyRequest.Decode(text));

    [Fact]
    public void Requests_with_relative_paths_or_a_bad_hash_or_a_bad_version_are_refused()
    {
        var s = Sample();
        foreach (var bad in new[] { s with { InstallDir = "relative/dir" }, s with { ZipPath = "z.zip" }, s with { RestartExe = "Syn3Updater.exe" }, s with { ResultFile = "r.json" },
                                    s with { Sha256 = "abc" }, s with { Sha256 = new string('z', 64) }, s with { Size = 0 }, s with { ParentPid = -1 }, s with { Version = "nonsense" },
                                    s with { LauncherPrefsPath = "relative.json" } })
            Assert.Throws<FormatException>(() => ApplyRequest.Decode(bad.Encode()));
    }

    [Fact]
    public void The_result_round_trips_and_a_missing_or_broken_file_reads_as_nothing()
    {
        using var t = new TempDir();
        var r = new ApplyResult(false, "3.1.0", "it broke", DateTimeOffset.Now);
        r.Write(t.Sub("sub", "r.json"));
        var back = ApplyResult.Read(t.Sub("sub", "r.json"))!;
        Assert.False(back.Success); Assert.Equal("3.1.0", back.Version); Assert.Equal("it broke", back.Message);
        Assert.Null(ApplyResult.Read(t.Sub("nope.json")));
        File.WriteAllText(t.Sub("bad.json"), "{{{"); Assert.Null(ApplyResult.Read(t.Sub("bad.json")));
    }
}
