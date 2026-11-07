using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Xunit;

namespace Syn3Updater.Tests;

public class InstallDetectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "syn3-detect-" + Guid.NewGuid().ToString("N"));
    public InstallDetectorTests() => Directory.CreateDirectory(_root);
    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows()) foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(d, UnixFileMode.UserRead | UserWrite | UserExecute);
            Directory.Delete(_root, true);
        }
        catch { /* best effort */ }
    }
    private const UnixFileMode UserWrite = UnixFileMode.UserWrite, UserExecute = UnixFileMode.UserExecute;

    private string Dir(string name, string? marker = null)
    {
        var d = Path.Combine(_root, name); Directory.CreateDirectory(d);
        if (marker != null) File.WriteAllText(Path.Combine(d, InstallDetector.MarkerFile), marker + "\n");
        return d;
    }

    [Theory]
    [InlineData("deb", InstallKind.LinuxDeb)]
    [InlineData("rpm", InstallKind.LinuxRpm)]
    [InlineData("pacman", InstallKind.LinuxPacman)]
    public void Package_managed_installs_are_never_updated_in_place(string marker, InstallKind kind)
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, Dir("pkg-" + marker, marker), "/x/Syn3Updater", "/home/u");
        Assert.Equal(kind, info.Kind);
        Assert.Equal(UpdateRoute.Manual, info.Route);          // even though the folder here IS writable
        Assert.False(info.CanSelfUpdate);
    }

    [Fact]
    public void A_tar_install_in_a_writable_folder_updates_in_place()
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, Dir("tar", "tar"), "/x/Syn3Updater", "/home/u");
        Assert.Equal(InstallKind.LinuxTar, info.Kind);
        Assert.True(info.CanSelfUpdate);
    }

    [Fact]
    public void A_tar_install_in_a_folder_that_cannot_be_written_falls_back_to_manual()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;         // root ignores permission bits
        var d = Dir("ro", "tar"); File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var info = InstallDetector.Detect(UpdateOs.Linux, d, "/x/Syn3Updater", "/home/u");
        Assert.Equal(UpdateRoute.Manual, info.Route);
        Assert.Contains("cannot be written", info.Reason);
    }

    [Fact]
    public void An_unrecognised_marker_is_treated_as_system_managed()
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, Dir("odd", "flatpak"), "/x/Syn3Updater", "/home/u");
        Assert.Equal(InstallKind.LinuxSystemUnknown, info.Kind);
        Assert.Equal(UpdateRoute.Manual, info.Route);
    }

    [Fact]
    public void An_empty_marker_is_ignored()
    {
        var d = Dir("empty"); File.WriteAllText(Path.Combine(d, InstallDetector.MarkerFile), "  \n");
        Assert.Equal(InstallKind.LinuxPortable, InstallDetector.Detect(UpdateOs.Linux, d, "/x/Syn3Updater", "/home/u").Kind);
    }

    [Theory]
    [InlineData("/opt/syn3updater-test-no-marker")]          // a path that cannot exist on any machine: the real /opt/syn3updater has a marker once the package is installed
    [InlineData("/usr/lib/syn3updater")]
    [InlineData("/usr/local/bin")]
    [InlineData("/snap/syn3updater/current")]
    [InlineData("/app/bin")]
    public void System_locations_without_a_marker_are_never_touched(string dir)
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, dir, "/x/Syn3Updater", "/home/u");
        Assert.Equal(InstallKind.LinuxSystemUnknown, info.Kind);
        Assert.Equal(UpdateRoute.Manual, info.Route);
    }

    [Fact]
    public void Look_alike_paths_are_not_mistaken_for_system_folders()
    {
        Assert.Equal(InstallKind.LinuxPortable, InstallDetector.Detect(UpdateOs.Linux, "/optical/syn3updater", "/x/S", "/home/u").Kind);
        Assert.Equal(InstallKind.LinuxPortable, InstallDetector.Detect(UpdateOs.Linux, "/usrfoo/x", "/x/S", "/home/u").Kind);
    }

    [Fact]
    public void A_copy_installed_by_install_sh_before_markers_existed_is_recognised_by_its_path()
    {
        var home = Dir("home"); var d = Path.Combine(home, ".local", "share", "syn3updater"); Directory.CreateDirectory(d);
        var info = InstallDetector.Detect(UpdateOs.Linux, d, "/x/Syn3Updater", home);
        Assert.Equal(InstallKind.LinuxTar, info.Kind);
        Assert.True(info.CanSelfUpdate);
    }

    [Fact]
    public void XDG_DATA_HOME_is_honoured_for_the_user_install()
    {
        var data = Dir("xdg"); var d = Path.Combine(data, "syn3updater"); Directory.CreateDirectory(d);
        Assert.Equal(InstallKind.LinuxTar, InstallDetector.Detect(UpdateOs.Linux, d, "/x/S", "/home/u", dataHome: data).Kind);
    }

    [Fact]
    public void An_extracted_archive_is_portable_and_manual()
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, Dir("Downloads/Syn3Updater-3.0.0-linux-x64"), "/x/Syn3Updater", "/home/u");
        Assert.Equal(InstallKind.LinuxPortable, info.Kind);
        Assert.Equal(UpdateRoute.Manual, info.Route);
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData("C:\\Program Files\\dotnet\\dotnet.exe")]
    public void Running_through_the_dotnet_host_is_a_development_run(string process)
    {
        var info = InstallDetector.Detect(UpdateOs.Linux, Dir("dev", "tar"), process, "/home/u");
        Assert.Equal(InstallKind.Development, info.Kind);
        Assert.Equal(UpdateRoute.None, info.Route);
    }

    [Fact]
    public void Windows_folders_update_in_place_when_writable()
    {
        var info = InstallDetector.Detect(UpdateOs.Windows, Dir("win", "zip"), "C:\\x\\Syn3Updater.exe", null);
        Assert.Equal(InstallKind.WindowsFolder, info.Kind);
        Assert.True(info.CanSelfUpdate);
    }

    [Theory]
    [InlineData("marker")] [InlineData("manifest")] [InlineData("launcher")]
    public void A_Windows_folder_is_an_install_only_with_evidence_of_one(string evidence)
    {
        var d = Dir("win-" + evidence, evidence == "marker" ? "zip" : null);
        if (evidence == "manifest") File.WriteAllText(Path.Combine(d, "install-manifest.json"), "{}");
        if (evidence == "launcher") File.WriteAllText(Path.Combine(d, "Launcher.exe"), "x");        // what the v2 installer and an older Launcher put beside the program
        var info = InstallDetector.Detect(UpdateOs.Windows, d, "C:\\x\\Syn3Updater.exe", null);
        Assert.Equal(InstallKind.WindowsFolder, info.Kind); Assert.True(info.CanSelfUpdate);
    }

    [Fact]
    public void A_lone_Syn3Updater_exe_copied_to_the_Desktop_or_Downloads_is_never_rewritten()
    {
        var d = Dir("Desktop");                                      // writable, no marker, no manifest, no Launcher: nothing installed this
        File.WriteAllText(Path.Combine(d, "Syn3Updater.exe"), "x"); File.WriteAllText(Path.Combine(d, "somebody-elses.dll"), "x");
        var info = InstallDetector.Detect(UpdateOs.Windows, d, "C:\\Users\\me\\Desktop\\Syn3Updater.exe", null);
        Assert.Equal(InstallKind.WindowsPortable, info.Kind); Assert.Equal(UpdateRoute.Manual, info.Route); Assert.False(info.CanSelfUpdate);
        Assert.False(UpdateService.DefaultCanApply(info));
    }

    [Theory]
    [InlineData("x/bin/Release/net10.0", true)] [InlineData("x/bin/Debug/net10.0", true)] [InlineData("x/obj/foo", false)] [InlineData("x/Syn3 Updater.app/Contents/MacOS", false)] [InlineData("x/app", false)]
    public void A_build_output_folder_is_a_development_run_even_when_started_by_dotnet_run_or_an_IDE(string rel, bool dev)
    {
        var d = Dir(rel.Replace('/', Path.DirectorySeparatorChar)); File.WriteAllText(Path.Combine(d, "Syn3Updater.dll"), "x");         // the apphost's own .dll beside it: a published release has none
        var info = InstallDetector.Detect(UpdateOs.Linux, d, Path.Combine(d, "Syn3Updater"), "/home/u");
        Assert.Equal(dev, info.Kind == InstallKind.Development); if (dev) Assert.Equal(UpdateRoute.None, info.Route);
    }

    [Fact]
    public void A_published_release_in_a_folder_called_bin_is_not_mistaken_for_a_build_output()
    {
        var d = Dir("bin/Release/net10.0");                          // looks like a build folder but has no Syn3Updater.dll: a single-file release put there
        Assert.NotEqual(InstallKind.Development, InstallDetector.Detect(UpdateOs.Linux, d, Path.Combine(d, "Syn3Updater"), "/home/u").Kind);
    }

    [Fact]
    public void A_missing_windows_folder_is_manual() =>
        Assert.Equal(UpdateRoute.Manual, InstallDetector.Detect(UpdateOs.Windows, Path.Combine(_root, "nope"), "C:\\x\\S.exe", null).Route);

    [Theory]
    [InlineData("/Applications/Syn3 Updater.app/Contents/MacOS", false, UpdateRoute.Manual, "signed and notarized")]
    [InlineData("/private/var/folders/ab/T/AppTranslocation/ABC/d/Syn3 Updater.app/Contents/MacOS", true, UpdateRoute.Manual, "Applications")]
    [InlineData("/Volumes/Syn3 Updater/Syn3 Updater.app/Contents/MacOS", true, UpdateRoute.Manual, "disk image")]
    [InlineData("/Users/u/Syn3Updater", true, UpdateRoute.Manual, "application bundle")]
    public void Mac_is_manual_unless_supported_and_installed_properly(string dir, bool supported, UpdateRoute route, string reason)
    {
        var info = InstallDetector.Detect(UpdateOs.MacOs, dir, "/x/Syn3Updater", null, macInPlaceSupported: supported);
        Assert.Equal(route, info.Route);
        Assert.Contains(reason, info.Reason);
    }

    [Fact]
    public void Instructions_name_the_right_command()
    {
        Assert.Equal("sudo apt install ./a.deb", InstallDetector.ManualInstructions(InstallKind.LinuxDeb, "a.deb"));
        Assert.StartsWith("sudo dnf install ./a.rpm", InstallDetector.ManualInstructions(InstallKind.LinuxRpm, "a.rpm"));
        Assert.Contains("yay -Syu syn3updater-bin", InstallDetector.ManualInstructions(InstallKind.LinuxPacman, null));
        Assert.Contains("pacman -U", InstallDetector.ManualInstructions(InstallKind.LinuxPacman, "a.pkg.tar.zst"));
        Assert.Contains("github.com/CyanLabs/Syn3Updater/releases", InstallDetector.ManualInstructions(InstallKind.LinuxPortable, null));
    }

    [Fact]
    public void The_running_test_process_is_detected_without_throwing()
    {
        var info = InstallDetector.DetectCurrent();
        Assert.NotNull(info); Assert.False(string.IsNullOrEmpty(info.AppDir));
        if (!OperatingSystem.IsWindows()) Assert.Equal(UpdateRoute.None, info.Route);       // on Linux and macOS tests run under the dotnet host (on Windows they run as testhost.exe)
    }
}
