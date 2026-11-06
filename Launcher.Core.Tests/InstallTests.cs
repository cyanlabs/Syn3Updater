using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace Cyanlabs.Launcher.Updating.Tests
{
    public class ChecksumTests
    {
        private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

        [Theory]
        [InlineData("sha256:" + Hash, Hash)] [InlineData(Hash, Hash)] [InlineData("SHA256:" + "9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08", Hash)]
        [InlineData("md5:abc", null)] [InlineData("sha256:short", null)] [InlineData("", null)] [InlineData(null, null)]
        public void Normalize(string input, string expected) => Assert.Equal(expected, Checksums.Normalize(input));

        [Fact]
        public void FindsTheRightLineInASumsFile()
        {
            var sums = $"{Hash}  Syn3Updater-3.0.0-win-x64.zip\r\n{new string('a', 64)} *Syn3Updater-3.0.0-win-arm64.zip\n";
            Assert.Equal(Hash, Checksums.FindInSums(sums, "Syn3Updater-3.0.0-win-x64.zip"));
            Assert.Equal(new string('a', 64), Checksums.FindInSums(sums, "Syn3Updater-3.0.0-win-arm64.zip"));
            Assert.Null(Checksums.FindInSums(sums, "other.zip"));
        }

        [Fact]
        public void ComputesSha256()
        {
            using var t = new TempDir(); File.WriteAllText(t.Sub("f"), "test");
            Assert.Equal(Hash, Checksums.ComputeSha256(t.Sub("f")));
        }
    }

    public class StagerTests
    {
        [Fact]
        public void FlattensTheV3TopLevelFolder()
        {
            using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater-3.0.0-win-x64/Syn3Updater.exe", "exe"), ("Syn3Updater-3.0.0-win-x64/LICENSE", "gpl")));
            var files = PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage"));
            Assert.Equal(new[] { "LICENSE", "Syn3Updater.exe" }, files);
            Assert.True(File.Exists(t.Sub("stage", "Syn3Updater.exe")));
        }

        [Fact]
        public void KeepsAFlatV2Layout_AndSubfolders()
        {
            using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater.exe", "e"), ("Launcher.exe", "l"), ("Languages/en-US.json", "{}"), ("a.dll", "d")));
            Assert.Equal(new[] { "a.dll", "Languages/en-US.json", "Launcher.exe", "Syn3Updater.exe" }, PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
        }

        [Theory]
        [InlineData("../evil.exe")] [InlineData("a/../../evil.exe")] [InlineData("/abs.exe")] [InlineData("C:/x.exe")]
        public void RejectsPathsThatEscapeTheFolder(string path)
        {
            using var t = new TempDir(); File.WriteAllBytes(t.Sub("p.zip"), Zips.Make(("Syn3Updater.exe", "e"), (path, "x")));
            Assert.Throws<PackageException>(() => PackageStager.Extract(t.Sub("p.zip"), t.Sub("stage")));
            Assert.False(File.Exists(Path.Combine(t.Path, "evil.exe")));
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
    }

    public class StaleTests
    {
        [Fact]
        public void WithoutAManifest_UsesTheOldLaunchersRule_DllsAndLanguages()
        {
            using var t = new TempDir();
            foreach (var f in new[] { "Syn3Updater.exe", "Launcher.exe", "a.dll", "Languages/en-US.json", "notes.txt", "Syn3Updater.exe.config" })
            { Directory.CreateDirectory(Path.GetDirectoryName(t.Sub(f.Split('/')))); File.WriteAllText(t.Sub(f.Split('/')), "x"); }
            var stale = StalePlanner.Compute(t.Path, null, new[] { "Syn3Updater.exe", "Launcher.exe" });
            Assert.Equal(new[] { "a.dll", "Languages/en-US.json" }, stale.OrderBy(s => s));     // user's notes.txt and the .config stay
        }

        [Fact]
        public void WithAManifest_RemovesOnlyWhatTheLauncherInstalledAndNoLongerShips()
        {
            using var t = new TempDir();
            foreach (var f in new[] { "Syn3Updater.exe", "old.dll", "keep.txt", "still.dll" }) File.WriteAllText(t.Sub(f), "x");
            var manifest = new InstallManifest { Files = { "Syn3Updater.exe", "old.dll", "still.dll", "gone-already.dll" } };
            Assert.Equal(new[] { "old.dll" }, StalePlanner.Compute(t.Path, manifest, new[] { "Syn3Updater.exe", "still.dll" }));
        }
    }

    public class TransactionTests
    {
        private static (TempDir root, string install, string staging) Setup(params (string, string)[] installed)
        {
            var t = new TempDir(); var install = t.Sub("install"); var staging = Path.Combine(install, InstallTransaction.StagingDirName);
            Directory.CreateDirectory(install);
            foreach (var (p, c) in installed) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, p))); File.WriteAllText(Path.Combine(install, p), c); }
            return (t, install, staging);
        }

        private static void Stage(string staging, params (string, string)[] files)
        {
            foreach (var (p, c) in files) { var f = Path.Combine(staging, p); Directory.CreateDirectory(Path.GetDirectoryName(f)); File.WriteAllText(f, c); }
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
                Assert.Equal("newl", File.ReadAllText(Path.Combine(install, "Launcher.exe")));       // the running launcher is replaced too
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
                var fs = new FaultyFileSystem { FailMove = (src, dst) => src.EndsWith("b.dll") && dst.Contains("install") && !dst.Contains(InstallTransaction.BackupDirName) };
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
                new InstallTransaction(install, fs, new RetryPolicy(10, 0)).Apply(staging, new[] { "Syn3Updater.exe" }, new string[0]);
                Assert.Equal("new", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe")));
                Assert.True(fs.MoveCalls > 3);
            }
        }

        [Fact]
        public void AnInterruptedUpdate_IsRolledBackAtTheNextStart()
        {
            var (t, install, staging) = Setup(("Syn3Updater.exe", "new-half-installed"));
            using (t)
            {
                // a crash left the originals in the backup folder with no COMPLETE marker
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
        public void Recover_WithNothingToDo_IsHarmless()
        {
            var (t, install, _) = Setup(("Syn3Updater.exe", "x"));
            using (t) { Assert.False(InstallTransaction.Recover(install)); Assert.Equal("x", File.ReadAllText(Path.Combine(install, "Syn3Updater.exe"))); }
        }
    }

    public class DataBackupTests
    {
        [Fact]
        public void CopiesSettingsAside_SkippingLogs_AndKeepsOnlyTheNewestThree()
        {
            using var t = new TempDir();
            var data = t.Sub("data"); Directory.CreateDirectory(Path.Combine(data, "Profiles")); Directory.CreateDirectory(Path.Combine(data, "Logs"));
            File.WriteAllText(Path.Combine(data, "settings.json"), "s"); File.WriteAllText(Path.Combine(data, "Profiles", "default.json"), "p"); File.WriteAllText(Path.Combine(data, "Logs", "big.log"), "l");
            var dest = DataBackup.Create(t.Sub("backups"), "2.14.2.0", data, t.Sub("missing"));
            Assert.Equal("s", File.ReadAllText(Path.Combine(dest, "data1", "settings.json")));
            Assert.Equal("p", File.ReadAllText(Path.Combine(dest, "data1", "Profiles", "default.json")));
            Assert.False(File.Exists(Path.Combine(dest, "data1", "Logs", "big.log")));
            Assert.Contains("before-2.14.2.0", dest);

            for (var i = 0; i < 4; i++) { System.Threading.Thread.Sleep(1100); DataBackup.Create(t.Sub("backups"), "2.14." + i, data); }
            Assert.Equal(3, Directory.GetDirectories(t.Sub("backups")).Length);
        }

        [Fact] public void NothingToBackUp_ReturnsNull() { using var t = new TempDir(); Assert.Null(DataBackup.Create(t.Sub("b"), "x", t.Sub("nope"))); }
    }
}
