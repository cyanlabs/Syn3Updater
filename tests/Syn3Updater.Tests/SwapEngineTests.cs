using System.IO.Compression;
using Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;
using Xunit;

namespace Syn3Updater.Tests;

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("swaptest").FullName;
    public string Sub(params string[] parts) => System.IO.Path.Combine(new[] { Path }.Concat(parts).ToArray());
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

internal static class Zips
{
    public static byte[] Make(params (string Path, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var (path, content) in files) { using var w = new StreamWriter(zip.CreateEntry(path).Open()); w.Write(content); }
        return ms.ToArray();
    }
}

/// <summary>Wraps the real file system and makes chosen operations fail, to prove the rollback.</summary>
internal sealed class FaultyFileSystem : ISwapFileSystem
{
    private readonly RealSwapFileSystem _real = new();
    public Func<string, string, bool> FailMove { get; set; } = (_, _) => false;
    public int MoveFailuresLeft { get; set; }
    public int DeleteDirFailuresLeft { get; set; }
    public Func<string, bool> FailDeleteFile { get; set; } = _ => false;
    public Func<string, bool> FailWrite { get; set; } = _ => false;
    public DateTime? BackupCreated { get; set; }
    public DateTime? ManifestWritten { get; set; }
    public DateTime GetCreationTimeUtc(string p) => BackupCreated ?? _real.GetCreationTimeUtc(p);
    public DateTime GetLastWriteTimeUtc(string p) => ManifestWritten ?? _real.GetLastWriteTimeUtc(p);
    public void WriteAllText(string p, string t) { if (FailWrite(p)) throw new IOException("cannot write (simulated)"); _real.WriteAllText(p, t); }
    public int MoveCalls { get; private set; }
    public bool FileExists(string p) => _real.FileExists(p);
    public bool DirectoryExists(string p) => _real.DirectoryExists(p);
    public void CreateDirectory(string p) => _real.CreateDirectory(p);
    public void DeleteFile(string p) { if (FailDeleteFile(p)) throw new IOException("in use by the running program (simulated)"); _real.DeleteFile(p); }
    public void DeleteDirectory(string p) { if (DeleteDirFailuresLeft > 0) { DeleteDirFailuresLeft--; throw new IOException("still in use (simulated)"); } _real.DeleteDirectory(p); }
    public void MoveFile(string s, string d, bool o)
    {
        MoveCalls++;
        if (MoveFailuresLeft > 0) { MoveFailuresLeft--; throw new IOException("locked (simulated)"); }
        if (FailMove(s, d)) throw new IOException("cannot move " + System.IO.Path.GetFileName(s) + " (simulated)");
        _real.MoveFile(s, d, o);
    }
}

/// <summary>The Launcher's own engine tests, ported: the updater must behave exactly as the Launcher does, because the two share one on-disk protocol.</summary>
public class StagerTests
{
    [Fact]
    public void FlattensTheV3TopLevelFolder()
    {
        using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater-3.0.0-win-x64/Syn3Updater.exe", "exe"), ("Syn3Updater-3.0.0-win-x64/LICENSE", "gpl")));
        Assert.Equal(new[] { "LICENSE", "Syn3Updater.exe" }, PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
        Assert.True(File.Exists(t.Sub("stage", "Syn3Updater.exe")));
    }

    [Fact]
    public void KeepsAFlatV2Layout_AndSubfolders()
    {
        using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater.exe", "e"), ("Launcher.exe", "l"), ("Languages/en-US.json", "{}"), ("a.dll", "d")));
        Assert.Equal(new[] { "a.dll", "Languages/en-US.json", "Launcher.exe", "Syn3Updater.exe" }, PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
    }

    [Theory]
    [InlineData("../evil.exe")] [InlineData("a/../../evil.exe")] [InlineData("/abs.exe")] [InlineData("C:/x.exe")] [InlineData("..\\evil.exe")]
    public void RejectsPathsThatEscapeTheFolder(string path)
    {
        using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater.exe", "e"), (path, "x")));
        Assert.Throws<PackageException>(() => PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
        Assert.False(File.Exists(System.IO.Path.Combine(t.Path, "evil.exe")));
    }

    [Fact]
    public void RejectsAPackageWithoutTheApp()
    {
        using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("readme.txt", "x")));
        Assert.Contains("Syn3Updater.exe", Assert.Throws<PackageException>(() => PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage"))).Message);
    }

    [Fact]
    public void ACorruptZip_Throws()
    {
        using var t = new TempDir(); File.WriteAllText(t.Sub("p.zip"), "not a zip");
        Assert.ThrowsAny<Exception>(() => PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
    }

    [Fact]
    public void ZipWithTooManyEntries_IsRefused()
    {
        using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(Enumerable.Range(0, 2100).Select(i => ($"f{i}.txt", "x")).Append(("Syn3Updater.exe", "e")).ToArray()));
        Assert.Contains("too many", Assert.Throws<PackageException>(() => PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage"))).Message);
    }
}

public class StaleTests
{
    [Fact]
    public void WithoutAManifest_UsesTheOldLaunchersRule_DllsAndLanguages()
    {
        using var t = new TempDir();
        foreach (var f in new[] { "Syn3Updater.exe", "Launcher.exe", "a.dll", "Languages/en-US.json", "notes.txt", "Syn3Updater.exe.config" })
        { Directory.CreateDirectory(Path.GetDirectoryName(t.Sub(f.Split('/')))!); File.WriteAllText(t.Sub(f.Split('/')), "x"); }
        var stale = StalePlanner.Compute(t.Path, null, new[] { "Syn3Updater.exe", "Launcher.exe" });
        Assert.Equal(new[] { "a.dll", "Languages/en-US.json" }, stale.OrderBy(s => s));        // the user's notes.txt and the .config stay
    }

    [Fact]
    public void WithAManifest_RemovesOnlyWhatWasInstalledAndNoLongerShips()
    {
        using var t = new TempDir();
        foreach (var f in new[] { "Syn3Updater.exe", "old.dll", "keep.txt", "still.dll" }) File.WriteAllText(t.Sub(f), "x");
        var manifest = new InstallManifest { Files = { "Syn3Updater.exe", "old.dll", "still.dll", "gone-already.dll" } };
        Assert.Equal(new[] { "old.dll" }, StalePlanner.Compute(t.Path, manifest, new[] { "Syn3Updater.exe", "still.dll" }));
    }

    [Fact]
    public void ATamperedManifestCannotPointOutsideTheInstallFolder()
    {
        using var t = new TempDir(); var install = t.Sub("install"); Directory.CreateDirectory(install);
        File.WriteAllText(t.Sub("victim.txt"), "precious"); File.WriteAllText(Path.Combine(install, "old.dll"), "x");
        var manifest = new InstallManifest { Files = { "../victim.txt", "..\\victim.txt", "/etc/passwd", "C:/Windows/x.dll", "old.dll" } };
        Assert.Equal(new[] { "old.dll" }, StalePlanner.Compute(install, manifest, new[] { "Syn3Updater.exe" }));
    }

    [Fact]
    public void ManifestJsonRoundTrips_AndReadsWhatTheLauncherWrites()
    {
        using var t = new TempDir();
        // exactly the shape Newtonsoft produced in the Launcher (Formatting.Indented, property names as declared)
        File.WriteAllText(t.Sub("install-manifest.json"), "{\n  \"Version\": \"3.0.0.0\",\n  \"Files\": [\n    \"Launcher.exe\",\n    \"Syn3Updater.exe\"\n  ]\n}");
        var m = InstallManifest.Load(t.Path)!;
        Assert.Equal("3.0.0.0", m.Version); Assert.Equal(new[] { "Launcher.exe", "Syn3Updater.exe" }, m.Files);
        m.Version = "3.1.0.0"; m.Files.Add("x.txt"); m.Save(t.Path);
        var again = InstallManifest.Load(t.Path)!;
        Assert.Equal("3.1.0.0", again.Version); Assert.Equal(3, again.Files.Count);
        Assert.Contains("\"Files\"", File.ReadAllText(t.Sub("install-manifest.json")));            // the Launcher reads these names back
    }

    [Theory]
    [InlineData("")] [InlineData("not json")] [InlineData("[]")] [InlineData("{\"Files\":\"x\"}")]
    public void ACorruptManifestIsTreatedAsMissing_NeverAnError(string content)
    {
        using var t = new TempDir(); File.WriteAllText(t.Sub("install-manifest.json"), content);
        var m = InstallManifest.Load(t.Path); Assert.True(m == null || m.Files.Count == 0);
    }

    [Fact]
    public void NoManifestFile_IsNull() { using var t = new TempDir(); Assert.Null(InstallManifest.Load(t.Path)); }
}

public class TransactionTests
{
    private static (TempDir root, string install, string staging) Setup(params (string, string)[] installed)
    {
        var t = new TempDir(); var install = t.Sub("install"); var staging = Path.Combine(install, InstallTransaction.StagingDirName);
        Directory.CreateDirectory(install);
        foreach (var (p, c) in installed) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, p))!); File.WriteAllText(Path.Combine(install, p), c); }
        return (t, install, staging);
    }

    private static void Stage(string staging, params (string, string)[] files)
    {
        foreach (var (p, c) in files) { var f = Path.Combine(staging, p); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, c); }
    }

    [Fact]
    public void Success_ReplacesAddsAndRemoves_AndLeavesNoScratchFolders()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old"), ("Launcher.exe", "oldl"), ("a.dll", "d"), ("user.txt", "mine"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new"), ("Launcher.exe", "newl"), ("extra/x.txt", "x"));
            new InstallTransaction(install).Apply(staging, new[] { "Syn3Updater.exe", "Launcher.exe", "extra/x.txt" }, new[] { "a.dll" });
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.Equal("newl", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            Assert.True(File.Exists(Path.Combine(install, "extra", "x.txt")));
            Assert.False(File.Exists(Path.Combine(install, "a.dll")));
            Assert.Equal("mine", File.ReadAllText(Path.Combine(install, "user.txt")));          // files it never installed are untouched
            Assert.False(Directory.Exists(staging)); Assert.False(Directory.Exists(Path.Combine(install, InstallTransaction.BackupDirName)));
        }
    }

    [Fact]
    public void AFileThatCannotBePlaced_RollsEverythingBack()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old-app"), ("Launcher.exe", "old-launcher"), ("a.dll", "old-dll"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new-app"), ("Launcher.exe", "new-launcher"), ("b.dll", "new-b"));
            var fs = new FaultyFileSystem { FailMove = (src, dst) => src.EndsWith("b.dll") && !dst.Contains(InstallTransaction.BackupDirName) };
            var ex = Assert.Throws<InstallException>(() => new InstallTransaction(install, fs, new RetryPolicy(2, 0)).Apply(staging, new[] { "Syn3Updater.exe", "Launcher.exe", "b.dll" }, new[] { "a.dll" }));
            Assert.Contains("previous version was restored", ex.Message);
            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.Equal("old-launcher", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            Assert.Equal("old-dll", File.ReadAllText(Path.Combine(install, "a.dll")));          // the file marked for removal is back too
            Assert.False(File.Exists(Path.Combine(install, "b.dll")));
        }
    }

    [Fact]
    public void ABriefLock_IsRetriedAndTheUpdateSucceeds()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new"));
            var fs = new FaultyFileSystem { MoveFailuresLeft = 3 };
            new InstallTransaction(install, fs, new RetryPolicy(10, 0)).Apply(staging, new[] { "Syn3Updater.exe" }, Array.Empty<string>());
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.True(fs.MoveCalls > 3);
        }
    }

    [Fact]
    public void AnInterruptedUpdate_IsRolledBackAtTheNextStart()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "new-half-installed"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "original"); File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "original-launcher");
            Assert.True(InstallTransaction.Recover(install));
            Assert.Equal("original", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.Equal("original-launcher", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            Assert.False(Directory.Exists(backup));
        }
    }

    [Fact]
    public void ACompletedUpdatesLeftovers_AreJustDeleted_NotRestored()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "new"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "old-launcher-still-locked-at-the-time"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
            Assert.False(InstallTransaction.Recover(install));
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.False(File.Exists(Path.Combine(install, "Launcher.exe"))); Assert.False(Directory.Exists(backup));
        }
    }

    [Fact]
    public void TheLeftoverBackupOfAFinishedUpdate_IsRemovedOnceTheOldProgramLetsGo()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "new"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "old, still exiting"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
            var failures = 3;                                                                          // the old program still holds its file for the first few tries
            var fs = new FaultyFileSystem { FailDeleteFile = p => p.EndsWith("Syn3Updater.exe") && failures-- > 0 };
            Assert.False(InstallTransaction.Recover(install, fs, new RetryPolicy(10, 0)));
            Assert.False(Directory.Exists(backup)); Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
        }
    }

    [Fact]
    public void ALeftoverThatNeverLetsGo_IsLeftForTheNextStart_WithItsMarker_AndIsNeverMistakenForAnInterruptedUpdate()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "new"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "old"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
            var locked = new FaultyFileSystem { FailDeleteFile = p => p.EndsWith("Syn3Updater.exe") };
            for (var start = 0; start < 3; start++)                                                    // start after start while the old program is somehow still locked
            {
                Assert.False(InstallTransaction.Recover(install, locked, new RetryPolicy(2, 0)));      // never "rolled back"
                Assert.True(File.Exists(Path.Combine(backup, "COMPLETE")));                            // the marker survives
                Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));       // and the new program is never replaced by the old one
            }
            Assert.False(InstallTransaction.Recover(install));                                         // finally it is free
            Assert.False(Directory.Exists(backup));
        }
    }

    [Fact]
    public void AFinishedUpdateWhoseOldProgramCannotBeDeletedYet_KeepsItsMarker_SoTheNextStartDoesNotUndoIt()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old-running"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new"));
            // the old exe is in the backup and cannot be deleted because it is still running; the marker sorts BEFORE it, so a one-shot recursive delete would remove the marker first
            var fs = new FaultyFileSystem { FailDeleteFile = p => p.EndsWith("Syn3Updater.exe") && p.Contains(InstallTransaction.BackupDirName) };
            new InstallTransaction(install, fs).Apply(staging, new[] { "Syn3Updater.exe" }, Array.Empty<string>());
            var backup = Path.Combine(install, InstallTransaction.BackupDirName);
            Assert.True(File.Exists(Path.Combine(backup, "Syn3Updater.exe")));
            Assert.True(File.Exists(Path.Combine(backup, "COMPLETE")), "the marker was deleted while the old program was still there: the next start would roll the update back");
            Assert.False(InstallTransaction.Recover(install, fs, new RetryPolicy(1, 0)));
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.False(InstallTransaction.Recover(install));                                         // released: cleaned up
            Assert.False(Directory.Exists(backup)); Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
        }
    }

    [Fact]
    public void Recover_WithNothingToDo_IsHarmless()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "x"));
        using (t) { Assert.False(InstallTransaction.Recover(install)); Assert.Equal("x", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe"))); }
    }

    [Theory]
    [InlineData("../escape.txt")] [InlineData("/abs.txt")] [InlineData("C:/x.txt")] [InlineData("a/../../b.txt")]
    public void UnsafePathsAreRefusedBeforeAnythingIsTouched(string rel)
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old"), ("keep.dll", "keep"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new"));
            Assert.Throws<InstallException>(() => new InstallTransaction(install).Apply(staging, new[] { "Syn3Updater.exe", rel }, Array.Empty<string>()));
            Assert.Throws<InstallException>(() => new InstallTransaction(install).Apply(staging, new[] { "Syn3Updater.exe" }, new[] { rel }));
            Assert.Equal("old", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(install, "keep.dll")));
            Assert.False(File.Exists(Path.Combine(t.Path, "escape.txt")));
        }
    }

    [Fact]
    public void Recover_NeverMovesAFileOutOfTheInstallFolder()
    {
        var (t, install, _) = Setup(("Syn3Updater.exe", "x"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(Path.Combine(backup, "sub"));
            File.WriteAllText(Path.Combine(backup, "sub", "ok.txt"), "ok");
            Assert.True(InstallTransaction.Recover(install));
            Assert.Equal("ok", File.ReadAllText(Path.Combine(install, "sub", "ok.txt")));
        }
    }

    [Fact]
    public void RemovingTheLastFileOfAFolderRemovesTheFolderToo()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old"), ("Languages/en-US.json", "{}"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new"));
            new InstallTransaction(install).Apply(staging, new[] { "Syn3Updater.exe" }, new[] { "Languages/en-US.json" });
            Assert.False(Directory.Exists(Path.Combine(install, "Languages")));
        }
    }
}

public class ReviewFindingsSwapTests
{
    private static (TempDir root, string install, string staging) Setup(params (string, string)[] installed)
    {
        var t = new TempDir(); var install = t.Sub("install"); var staging = Path.Combine(install, InstallTransaction.StagingDirName);
        Directory.CreateDirectory(install);
        foreach (var (p, c) in installed) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, p))!); File.WriteAllText(Path.Combine(install, p), c); }
        return (t, install, staging);
    }
    private static void Stage(string staging, params (string, string)[] files) { foreach (var (p, c) in files) { var f = Path.Combine(staging, p); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, c); } }

    [Fact]
    public void Without_a_manifest_only_dlls_beside_the_program_and_the_Languages_folder_are_stale_never_dlls_in_sub_folders()
    {
        using var t = new TempDir();
        foreach (var f in new[] { "Syn3Updater.exe", "old.dll", "Languages/en-US.json", "tools/Other.dll", "OtherApp/lib/x.dll", "Downloads/readme.dll" })
        { Directory.CreateDirectory(Path.GetDirectoryName(t.Sub(f.Split('/')))!); File.WriteAllText(t.Sub(f.Split('/')), "x"); }
        var stale = StalePlanner.Compute(t.Path, null, new[] { "Syn3Updater.exe" });
        Assert.Equal(new[] { "Languages/en-US.json", "old.dll" }, stale.OrderBy(s => s, StringComparer.Ordinal));          // the other programs' DLLs are untouched
    }

    [Fact]
    public void Files_that_cannot_be_deleted_because_they_are_running_are_renamed_aside_and_removed_at_the_next_start()
    {
        using var t = new TempDir(); var f = t.Sub("Syn3Updater.exe"); File.WriteAllText(f, "current");
        var aside = f + "." + new string('a', 32) + ".old"; File.WriteAllText(aside, "was running"); File.WriteAllText(t.Sub("keep.old"), "not ours");
        Assert.True(RealSwapFileSystem.IsDisplaced(aside)); Assert.False(RealSwapFileSystem.IsDisplaced(t.Sub("keep.old")));
        Assert.False(InstallTransaction.Recover(t.Path));
        Assert.False(File.Exists(aside)); Assert.True(File.Exists(t.Sub("keep.old"))); Assert.Equal("current", File.ReadAllText(f));
    }

    [Fact]
    public void A_leftover_backup_of_a_finished_update_that_is_still_locked_blocks_the_next_update_and_keeps_its_marker()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "current"));
        using (t)
        {
            var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup, "Syn3Updater.exe"), "old, still running"); File.WriteAllText(Path.Combine(backup, "COMPLETE"), "x");
            Stage(staging, ("Syn3Updater.exe", "newer"));
            var fs = new FaultyFileSystem { FailDeleteFile = p => p.EndsWith("Syn3Updater.exe") && p.Contains(InstallTransaction.BackupDirName) };
            Assert.Throws<InstallException>(() => new InstallTransaction(install, fs, new RetryPolicy(1, 0)).Apply(staging, new[] { "Syn3Updater.exe" }, Array.Empty<string>()));
            Assert.True(File.Exists(Path.Combine(backup, "COMPLETE")), "the marker was removed: the next start would roll the CURRENT version back to the old one");
            Assert.Equal("current", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));        // nothing was touched
            Assert.False(InstallTransaction.Recover(install, fs, new RetryPolicy(1, 0))); Assert.Equal("current", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
        }
    }

    [Fact]
    public void If_the_completion_marker_cannot_be_written_the_update_is_undone_not_left_to_be_rolled_back_later()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old-app"), ("Launcher.exe", "old-launcher"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new-app"), ("Launcher.exe", "new-launcher"));
            var fs = new FaultyFileSystem { FailWrite = p => p.EndsWith("COMPLETE") };
            var ex = Assert.Throws<InstallException>(() => new InstallTransaction(install, fs, new RetryPolicy(2, 0)).Apply(staging, new[] { "Syn3Updater.exe", "Launcher.exe" }, Array.Empty<string>()));
            Assert.Contains("previous version was restored", ex.Message);
            Assert.Equal("old-app", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe"))); Assert.Equal("old-launcher", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            Assert.False(Directory.Exists(Path.Combine(install, InstallTransaction.BackupDirName)));
        }
    }

    [Fact]
    public void A_marker_write_that_succeeds_on_a_retry_completes_the_update_normally()
    {
        var (t, install, staging) = Setup(("Syn3Updater.exe", "old"));
        using (t)
        {
            Stage(staging, ("Syn3Updater.exe", "new")); var failures = 2;
            var fs = new FaultyFileSystem { FailWrite = p => p.EndsWith("COMPLETE") && failures-- > 0 };
            new InstallTransaction(install, fs, new RetryPolicy(5, 0)).Apply(staging, new[] { "Syn3Updater.exe" }, Array.Empty<string>());
            Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
        }
    }
}

/// <summary>The v2 -> v3 hop through the old hybrid Launcher: its own files are locked while it installs, so its one-shot backup delete removes the COMPLETE marker and leaves them behind.</summary>
public class LostMarkerRecoverTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 19, 30, 0, DateTimeKind.Utc);

    private static (TempDir t, string install, string backup) Hop(bool withManifest)
    {
        var t = new TempDir(); var install = t.Sub("install"); Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(install, "Syn3Updater.exe"), "NEW-APP"); File.WriteAllText(Path.Combine(install, "Launcher.exe"), "NEW-STUB");
        if (withManifest) File.WriteAllText(Path.Combine(install, "install-manifest.json"), "{\"Version\":\"2.99.99.12\",\"Files\":[\"Launcher.exe\",\"Syn3Updater.exe\"]}");
        var backup = Path.Combine(install, InstallTransaction.BackupDirName); Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "Launcher.exe"), "OLD-LAUNCHER (was locked)"); File.WriteAllText(Path.Combine(backup, "FluentWPF.dll"), "OLD-DLL (was locked)");     // no COMPLETE: the old engine deleted it
        return (t, install, backup);
    }

    [Fact]
    public void An_update_that_finished_but_lost_its_marker_is_not_rolled_back_over_the_new_files()
    {
        var (t, install, backup) = Hop(withManifest: true);
        using (t)
        {
            var fs = new FaultyFileSystem { BackupCreated = Now, ManifestWritten = Now.AddSeconds(2) };          // the manifest was written after the backup folder was created: the update completed
            Assert.False(InstallTransaction.Recover(install, fs, new RetryPolicy(1, 0)));
            Assert.Equal("NEW-STUB", File.ReadAllText(Path.Combine(install, "Launcher.exe")));                    // the new stub stays
            Assert.False(File.Exists(Path.Combine(install, "FluentWPF.dll")));                                    // and the old library is not put back
            Assert.False(Directory.Exists(backup));                                                               // the leftovers are simply removed
        }
    }

    [Fact]
    public void A_genuinely_interrupted_update_is_still_rolled_back_even_with_an_old_manifest_present()
    {
        var (t, install, backup) = Hop(withManifest: true);
        using (t)
        {
            var fs = new FaultyFileSystem { BackupCreated = Now, ManifestWritten = Now.AddMinutes(-30) };         // the manifest is the PREVIOUS update's: this one never got that far
            Assert.True(InstallTransaction.Recover(install, fs, new RetryPolicy(1, 0)));
            Assert.Equal("OLD-LAUNCHER (was locked)", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            Assert.Equal("OLD-DLL (was locked)", File.ReadAllText(Path.Combine(install, "FluentWPF.dll")));
        }
    }

    [Fact]
    public void Without_any_manifest_a_markerless_backup_is_an_interrupted_update_as_before()
    {
        var (t, install, _) = Hop(withManifest: false);
        using (t) { Assert.True(InstallTransaction.Recover(install, new FaultyFileSystem(), new RetryPolicy(1, 0))); Assert.Equal("OLD-LAUNCHER (was locked)", File.ReadAllText(Path.Combine(install, "Launcher.exe"))); }
    }

    [Fact]
    public void A_finished_update_whose_leftovers_stay_locked_is_never_rolled_back_on_any_later_start_either()
    {
        var (t, install, backup) = Hop(withManifest: true);
        using (t)
        {
            var fs = new FaultyFileSystem { BackupCreated = Now, ManifestWritten = Now.AddSeconds(2), FailDeleteFile = p => p.EndsWith("Launcher.exe") && p.Contains(InstallTransaction.BackupDirName) };
            for (var start = 0; start < 3; start++)
            {
                Assert.False(InstallTransaction.Recover(install, fs, new RetryPolicy(1, 0)));
                Assert.Equal("NEW-STUB", File.ReadAllText(Path.Combine(install, "Launcher.exe")));
            }
            Assert.True(File.Exists(Path.Combine(backup, "COMPLETE")));                                           // the marker was put back, so it stays recognised
            Assert.False(InstallTransaction.Recover(install, new FaultyFileSystem { BackupCreated = Now, ManifestWritten = Now.AddSeconds(2) }));
            Assert.False(Directory.Exists(backup));
        }
    }

    [Fact]
    public void If_the_times_cannot_be_read_the_old_safe_answer_applies()
    {
        var (t, install, _) = Hop(withManifest: true);
        using (t) Assert.True(InstallTransaction.Recover(install, new ThrowingTimes(), new RetryPolicy(1, 0)));
    }

    private sealed class ThrowingTimes : ISwapFileSystem
    {
        private readonly RealSwapFileSystem _r = new();
        public bool FileExists(string p) => _r.FileExists(p); public bool DirectoryExists(string p) => _r.DirectoryExists(p); public void CreateDirectory(string p) => _r.CreateDirectory(p);
        public void MoveFile(string s, string d, bool o) => _r.MoveFile(s, d, o); public void DeleteFile(string p) => _r.DeleteFile(p); public void DeleteDirectory(string p) => _r.DeleteDirectory(p);
        public void WriteAllText(string p, string t) => _r.WriteAllText(p, t);
        public DateTime GetCreationTimeUtc(string p) => throw new IOException("cannot read"); public DateTime GetLastWriteTimeUtc(string p) => throw new IOException("cannot read");
    }
}

