using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class FolderStartAndWindowsNameTests
{
    [Theory]
    [InlineData("10.0.19045.0", "Windows 10 (10.0.19045)")]
    [InlineData("10.0.22000.0", "Windows 11 (10.0.22000)")]        // the first Windows 11 build
    [InlineData("10.0.21999.0", "Windows 10 (10.0.21999)")]
    [InlineData("10.0.26100.1", "Windows 11 (10.0.26100)")]
    [InlineData("10.0.26200.0", "Windows 11 (10.0.26200)")]        // what the tester's PC reported as "Windows 10.0.26200"
    [InlineData("6.3.9600.0", "Windows 6.3.9600")]                 // Windows 8.1: not renamed
    public void Windows_11_is_called_Windows_11_even_though_it_reports_version_10(string version, string expected) =>
        Assert.Equal(expected, SystemInfo.WindowsName(Version.Parse(version)));

    [Fact]
    public void A_version_with_no_build_number_does_not_throw() =>
        Assert.Equal("Windows 10 (10.0.0)", SystemInfo.WindowsName(new Version(10, 0)));

    [Fact]
    public void The_OS_name_on_this_machine_is_never_empty() => Assert.False(string.IsNullOrWhiteSpace(SystemInfo.OsName()));

    private static string Temp() => Directory.CreateTempSubdirectory("syn3start").FullName;

    [Fact]
    public void An_existing_folder_is_the_start_location_itself()
    {
        var d = Temp(); try { Assert.Equal(d, DialogsImpl.ExistingFolder(d)); Assert.Equal(d, DialogsImpl.ExistingFolder("  " + d + "  ")); } finally { Directory.Delete(d, true); }
    }

    [Fact]
    public void A_folder_that_is_gone_starts_at_its_nearest_existing_parent()
    {
        var d = Temp(); try { Assert.Equal(d, DialogsImpl.ExistingFolder(Path.Combine(d, "unplugged", "downloads", "syn3"))); } finally { Directory.Delete(d, true); }
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public void No_path_means_the_OS_decides_where_the_picker_opens(string? path) => Assert.Null(DialogsImpl.ExistingFolder(path));

    [AvaloniaFact]
    public async Task The_download_location_picker_opens_where_the_text_box_points_not_at_Documents()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown();
        s.DownloadPath = "/typed/in/the/box"; await s.PickDownloadPathCommand.ExecuteAsync(null);
        s.LogPath = "/some/log/folder"; await s.PickLogPathCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "/typed/in/the/box", "/some/log/folder" }, env.Dialogs.FolderStarts);
    }

    [AvaloniaFact]
    public async Task Choosing_a_folder_on_Home_opens_at_the_download_folder_then_at_the_one_chosen_last()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        await home.ChooseFolderCommand.ExecuteAsync(null);                                    // nothing chosen yet
        Assert.Equal(env.DownloadDir, env.Dialogs.FolderStarts[0]);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        await home.ChooseFolderCommand.ExecuteAsync(null);                                    // now a folder is selected
        Assert.Equal(env.UsbDir, env.Dialogs.FolderStarts[^1]);
    }
}
