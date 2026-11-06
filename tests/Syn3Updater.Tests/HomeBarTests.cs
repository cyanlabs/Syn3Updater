using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>The Home bottom bar (what will happen + MY20 + buttons), the MY20 badge, the format confirmation text and the Settings order.</summary>
public class HomeBarTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    [AvaloniaTheory]
    [InlineData(null, "MY20 Protection: Auto-Detect", "#5b9de6")]
    [InlineData(true, "MY20 Protection: Enabled", "#3fb97a")]
    [InlineData(false, "MY20 Protection: Disabled / Not MY20", "#e8a23a")]
    public void TheMy20Badge_SaysWhichStateYouAreIn_WithItsOwnColourAndShield(bool? my20, string label, string colour)
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        Ctx.Profile.My20v2 = my20; home.ReloadSummary();
        Assert.Equal(label, home.My20Label);
        Assert.Equal(Color.Parse(colour), ((SolidColorBrush)home.My20Brush!).Color);
        Assert.False(string.IsNullOrEmpty(home.My20Icon));
        Assert.DoesNotContain(home.ConfigItems, c => c.Label == "MY20 Protection");               // it moved out of the grey header chips
    }

    [Fact]
    public void TheThreeShieldsAreDifferent_SoTheStatesLookDifferent()
    {
        using var env = new AppEnv(); var seen = new HashSet<string>();
        foreach (bool? v in new bool?[] { null, true, false }) { Ctx.Profile.My20v2 = v; env.Main.Home.ReloadSummary(); seen.Add(env.Main.Home.My20Icon); }
        Assert.Equal(3, seen.Count);
    }

    [Fact]
    public void TheBottomBar_StacksOnNarrowWindows_SoTheModeTextIsNeverCutOff()
    {
        var xaml = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/HomeView.axaml"));
        Assert.Contains("Grid.bar.compact > Grid#ModeInfo", xaml);                                // narrow: the mode text gets the whole first row...
        Assert.Contains("Grid.bar.compact > Border#My20Badge", xaml); Assert.Contains("Grid.bar.compact > Button#StartButton", xaml);   // ...MY20 and the buttons share the second
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml.Substring(xaml.IndexOf("ModeDescription", StringComparison.Ordinal) - 80, 400));     // and anything still too long ends in "..." instead of vanishing
        foreach (var name in new[] { "ModeInfo", "My20Badge", "DownloadOnlyButton", "StartButton" })                                    // placement only through styles, or a local value would beat the compact one
        {
            var tag = Regex.Match(xaml, $@"<[A-Za-z]+ x:Name=""{name}""[^>]*>").Value;
            Assert.DoesNotContain("Grid.Column=", tag); Assert.DoesNotContain("Grid.Row=", tag);
        }
        Assert.Contains("CompactBelow = 880", File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/HomeView.axaml.cs")));
    }

    [Fact]
    public void Settings_PutsMy20First_BecauseTheInstallModeNextToItDependsOnIt()
    {
        var xaml = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/SettingsView.axaml"));
        Assert.True(xaml.IndexOf("Settings.My20Mode", StringComparison.Ordinal) < xaml.IndexOf("Settings.InstallMode", StringComparison.Ordinal), "the dropdown that decides must come first");
    }

    // ---- the confirmation before a drive is wiped ----
    private static Task<bool> StartWith(AppEnv env, DriveInfoModel drive) => Workflow.StartAsync(new JobSpec
    {
        Mode = InstallModes.Autoinstall, Release = "r", Region = "NA", Drive = drive, Action = UpdateAction.Main,
        Packages = { new Ivsu { Type = "APPS", Name = "A", FileName = "A.tar.gz", Url = env.Files.Url + "/A.tar.gz", Md5 = FileServer.Md5("A.tar.gz"), FileSize = FileServer.Blob("A.tar.gz").Length } },
    });

    [AvaloniaTheory]
    [InlineData("", "-")]                  // a drive with no letter yet (new, or Windows has not mounted it): a dash, not an empty "Drive Letter:"
    [InlineData(@"E:\", "E:")]
    public async Task TheFormatWarning_NamesTheDriveOnItsOwnLine_WithoutTheDiskNumber(string mount, string shownLetter)
    {
        var root = Directory.CreateTempSubdirectory("syn3fmt").FullName;
        using var env = new AppEnv(drives: new RemountingDrives(Path.Combine(root, "after")));
        env.Dialogs.Say(m => m.StartsWith("ALL DATA ON THE SELECTED DRIVE"), true);
        var usb = new DriveInfoModel { DeviceId = "1", MountPath = mount, Name = "SSK Port able SSD 128", SizeBytes = 128035676160, FileSystem = "ntfs", PartitionType = "MBR" };
        await StartWith(env, usb);
        var msg = env.Dialogs.Messages.First(m => m.StartsWith("ALL DATA ON THE SELECTED DRIVE"));
        Assert.Equal($"ALL DATA ON THE SELECTED DRIVE WILL BE LOST\n\nSSK Port able SSD 128 (119.2GB)\nDrive Letter: {shownLetter}\n\nClick YES to format and continue.", msg);
        Assert.DoesNotContain("[1", msg); Assert.DoesNotContain("1,", msg);                            // the Windows disk number (and its odd "[1, 119.2GB]") is gone
    }

    [Fact]
    public void EveryLanguagesFormatWarning_HasItsLineBreaks_NotALiteralN()
    {
        // five languages (hu, no, sv, tr, el) had "  n" where a newline belongs, so the popup showed a stray "n"
        var dir = Path.GetDirectoryName(RepoFile("src/Syn3Updater.Core/Languages/en-US.json"))!;
        foreach (var f in Directory.GetFiles(dir, "*.json"))
        {
            var v = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!["MessageBox.CancelFormatUSB"];
            Assert.True(v.Contains("{0}") && v.Contains("{1}"), Path.GetFileName(f));
            Assert.True(v.Count(c => c == '\n') >= 2, $"{Path.GetFileName(f)}: lost its line breaks: {v}");
            Assert.False(Regex.IsMatch(v, @"\s{2}n"), $"{Path.GetFileName(f)}: still has a literal 'n' for a newline: {v}");
            Assert.DoesNotMatch(@"[ \t]\{0\}", v);                                                       // {0} brings its own blank line
        }
    }
}
