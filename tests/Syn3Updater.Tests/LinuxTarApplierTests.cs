using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;
using static Syn3Updater.Tests.UpdateTestSupport;

namespace Syn3Updater.Tests;

public class LinuxTarApplierTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syn3-apply-" + Guid.NewGuid().ToString("N"));
    private readonly string _xdg, _appDir, _work, _archive;
    public LinuxTarApplierTests()
    {
        _xdg = Path.Combine(_root, "xdg"); _appDir = Path.Combine(_xdg, "syn3updater"); _work = Path.Combine(_root, "work"); _archive = Path.Combine(_root, "a.tar.gz");
        Directory.CreateDirectory(_appDir); File.WriteAllText(Path.Combine(_appDir, "Syn3Updater"), "OLD");
        File.WriteAllText(Path.Combine(_appDir, ".install-method"), "tar\n");
    }
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private InstallInfo Install(string? dir = null, UpdateRoute route = UpdateRoute.InPlace, InstallKind kind = InstallKind.LinuxTar) => new(kind, dir ?? _appDir, route, "test");
    private string Program => Path.Combine(_appDir, "Syn3Updater");
    private const string CopyScript = "#!/usr/bin/env bash\nset -e\nmkdir -p \"$XDG_DATA_HOME/syn3updater\"\ninstall -m 755 \"$(dirname \"$0\")/Syn3Updater\" \"$XDG_DATA_HOME/syn3updater/Syn3Updater\"\n";

    private Task<string> Apply(InstallInfo? i = null, IReadOnlyDictionary<string, string>? env = null) =>
        LinuxTarApplier.ApplyAsync(_archive, i ?? Install(), _work, _xdg, TimeSpan.FromSeconds(20), default, env);

    [Fact]
    public async Task A_good_archive_replaces_the_program_and_keeps_a_copy_of_the_old_one()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("Syn3Updater-9/", "", 0x1ed), ("Syn3Updater-9/install.sh", CopyScript, 0x1ed), ("Syn3Updater-9/Syn3Updater", "NEW", 0x1ed));
        Assert.Equal(Program, await Apply());
        Assert.Equal("NEW", File.ReadAllText(Program));
        Assert.Equal("OLD", File.ReadAllText(Program + LinuxTarApplier.PreviousSuffix));
        Assert.False(Directory.Exists(_work));
        LinuxTarApplier.CleanUpPrevious(_appDir);
        Assert.False(File.Exists(Program + LinuxTarApplier.PreviousSuffix));
    }

    [Fact]
    public async Task A_failing_install_script_puts_the_old_program_back()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", "#!/usr/bin/env bash\necho half > \"$XDG_DATA_HOME/syn3updater/Syn3Updater\"\necho boom >&2\nexit 3\n", 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed));
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Contains("boom", ex.Message);
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task An_install_that_did_not_produce_the_downloaded_program_is_rolled_back()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", "#!/usr/bin/env bash\nexit 0\n", 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed));       // "succeeds" but installs nothing
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Contains("not the one that was downloaded", ex.Message);
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task A_hanging_install_script_is_stopped()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", "#!/usr/bin/env bash\nsleep 60\n", 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed));
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => LinuxTarApplier.ApplyAsync(_archive, Install(), _work, _xdg, TimeSpan.FromMilliseconds(500)));
        Assert.Contains("too long", ex.Message);
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("d/../../evil")]
    [InlineData("/etc/evil")]
    public async Task Paths_that_escape_the_folder_are_refused_and_nothing_is_written_outside(string name)
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", CopyScript, 0x1ed), (name, "x", 0x1a4));
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.False(File.Exists(Path.Combine(_root, "evil")));
        Assert.False(File.Exists("/etc/evil"));
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task Links_in_the_archive_are_refused()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", CopyScript, 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed), ("d/link", "->/etc/passwd", 0x1ff));
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Contains("other than files and folders", ex.Message);
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task An_archive_without_install_sh_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/Syn3Updater", "NEW", 0x1ed));
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
    }

    [Fact]
    public async Task An_archive_with_too_many_entries_is_refused()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, Enumerable.Range(0, 250).Select(i => ($"d/f{i}", "x", 0x1a4)).ToArray());
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply());
        Assert.Contains("too many", ex.Message);
    }

    [Fact]
    public async Task A_corrupt_archive_fails_cleanly_and_leaves_the_program_alone()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        await File.WriteAllBytesAsync(_archive, new byte[] { 1, 2, 3, 4, 5 });
        await Assert.ThrowsAnyAsync<Exception>(() => Apply());
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task The_install_script_never_runs_if_it_would_install_somewhere_else()
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        var sentinel = Path.Combine(_root, "ran");
        MakeTarGz(_archive, ("d/install.sh", $"#!/usr/bin/env bash\ntouch '{sentinel}'\n", 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed));
        var other = Path.Combine(_root, "elsewhere"); Directory.CreateDirectory(other);
        var ex = await Assert.ThrowsAsync<UpdateApplyException>(() => Apply(Install(other)));
        Assert.Contains("not touching either", ex.Message);
        Assert.False(File.Exists(sentinel));
    }

    [Theory]
    [InlineData(InstallKind.LinuxDeb, UpdateRoute.Manual)]
    [InlineData(InstallKind.LinuxPortable, UpdateRoute.Manual)]
    [InlineData(InstallKind.LinuxTar, UpdateRoute.Manual)]
    [InlineData(InstallKind.WindowsFolder, UpdateRoute.InPlace)]
    public async Task Only_a_tar_install_that_can_update_itself_is_accepted(InstallKind kind, UpdateRoute route)
    {
        if (OperatingSystem.IsWindows()) return;                    // runs a shell script: the Linux install path, never used on Windows
        MakeTarGz(_archive, ("d/install.sh", CopyScript, 0x1ed), ("d/Syn3Updater", "NEW", 0x1ed));
        await Assert.ThrowsAsync<UpdateApplyException>(() => Apply(Install(kind: kind, route: route)));
        Assert.Equal("OLD", File.ReadAllText(Program));
    }

    [Fact]
    public async Task Extracted_files_never_keep_setuid_or_world_write_bits()
    {
        if (OperatingSystem.IsWindows()) return;
        MakeTarGz(_archive, ("d/install.sh", "#!/usr/bin/env bash\nmkdir -p \"$XDG_DATA_HOME/syn3updater\"\ncp -p \"$(dirname \"$0\")/Syn3Updater\" \"$XDG_DATA_HOME/syn3updater/Syn3Updater\"\ncp -p \"$(dirname \"$0\")/Syn3Updater\" \"$XDG_DATA_HOME/extracted\"\n", 0x1ed), ("d/Syn3Updater", "NEW", 0x9ff));       // mode 4777
        await Apply();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute,
                     File.GetUnixFileMode(Path.Combine(_xdg, "extracted")));            // 0755, whatever the archive said (it said 04777)
    }

    /// <summary>The REAL install.sh, taken out of scripts/package-linux.sh, run inside a throwaway home with a minimal PATH (no xdg-mime, no desktop database): the update must produce exactly what a fresh install does.</summary>
    [Fact]
    public async Task The_real_install_script_updates_an_existing_install()
    {
        if (OperatingSystem.IsWindows()) return;
        var packager = File.ReadAllText(FindRepoFile(Path.Combine("scripts", "package-linux.sh")));
        var start = packager.IndexOf("<<'INST'\n", StringComparison.Ordinal) + "<<'INST'\n".Length;
        var script = packager[start..packager.IndexOf("\nINST\n", start, StringComparison.Ordinal)] + "\n";
        MakeTarGz(_archive, ("Syn3Updater-9/", "", 0x1ed), ("Syn3Updater-9/install.sh", script, 0x1ed), ("Syn3Updater-9/Syn3Updater", "NEW PROGRAM", 0x1ed),
                  ("Syn3Updater-9/syn3updater.png", "png", 0x1a4), ("Syn3Updater-9/LICENSE", "licence", 0x1a4));
        var home = Path.Combine(_root, "home"); Directory.CreateDirectory(home);
        var bin = Path.Combine(_root, "tools"); Directory.CreateDirectory(bin);
        foreach (var t in new[] { "install", "ln", "mkdir", "chmod", "cat", "dirname", "rm" })        // /usr/bin on Linux, partly /bin on macOS
            File.CreateSymbolicLink(Path.Combine(bin, t), new[] { "/usr/bin/" + t, "/bin/" + t }.First(File.Exists));
        var env = new Dictionary<string, string> { ["HOME"] = home, ["PATH"] = bin, ["XDG_CONFIG_HOME"] = Path.Combine(home, ".config") };

        Assert.Equal(Program, await Apply(env: env));

        Assert.Equal("NEW PROGRAM", File.ReadAllText(Program));
        Assert.Equal("tar", File.ReadAllText(Path.Combine(_appDir, ".install-method")).Trim());          // the marker survives an update
        var desktop = File.ReadAllText(Path.Combine(_xdg, "applications", "syn3updater.desktop"));
        Assert.Contains($"Exec=\"{Program}\" %u", desktop);
        Assert.True(File.Exists(Path.Combine(_xdg, "icons", "hicolor", "128x128", "apps", "syn3updater.png")));
        Assert.True(File.Exists(Path.Combine(home, ".local", "bin", "syn3updater")));
    }
}
