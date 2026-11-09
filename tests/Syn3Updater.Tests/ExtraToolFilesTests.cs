using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// Reformat and Downgrade add tool files to the download that are not in the package list. A tester saw "4 of 4 selected" and then 5 downloads (the RW data cleaner) and found it confusing:
/// Home now says the mode adds tool files, and the download page tags each file the app added.
/// </summary>
public class ExtraToolFilesTests
{
    private static async Task Pick(AppEnv env)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 2);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
    }

    private static string Note => Ctx.T("App.SelectedExtraTools");

    [AvaloniaFact]
    public async Task Home_says_the_reformat_mode_adds_tool_files_to_the_download()
    {
        using var env = new AppEnv();
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = InstallModes.Reformat;           // a forced Reformat
        await Pick(env);
        Assert.Contains("2 of 2 selected", env.Main.Home.SelectionSummary);
        Assert.EndsWith(Note, env.Main.Home.SelectionSummary); Assert.Equal("plus the tool files this install mode needs", Note);
    }

    [AvaloniaFact]
    public async Task Home_says_the_same_for_downgrade_and_nothing_for_a_plain_install()
    {
        using var env = new AppEnv();
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = InstallModes.Downgrade;
        await Pick(env);
        Assert.EndsWith(Note, env.Main.Home.SelectionSummary);

        using var plain = new AppEnv();
        await Pick(plain);                                                                   // the normal automatic mode: only the ticked packages are downloaded
        Assert.DoesNotContain(Note, plain.Main.Home.SelectionSummary); Assert.Contains("2 of 2 selected", plain.Main.Home.SelectionSummary);
    }

    [AvaloniaFact]
    public async Task The_note_follows_the_selection_count_and_goes_away_when_the_mode_does()
    {
        using var env = new AppEnv();
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = InstallModes.Reformat;
        await Pick(env); var home = env.Main.Home;
        home.Packages[0].Selected = false;                                                    // untick one: the count changes and the note stays
        Assert.Contains("1 of 2 selected", home.SelectionSummary); Assert.EndsWith(Note, home.SelectionSummary);
    }

    [AvaloniaFact]
    public async Task On_the_download_page_the_files_the_app_added_are_tagged_and_the_ticked_packages_are_not()
    {
        using var env = new AppEnv();
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = InstallModes.Reformat;
        await Pick(env);
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.DownloadOnlyMode"), true);
        await env.Main.Home.DownloadOnlyCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Dialogs.Messages.Contains(Ctx.T("MessageBox.DownloadOnlyComplete")));
        var done = env.Main.Downloads.Completed;
        Assert.Equal(4, done.Count);                                                          // the two ticked packages, plus the reformat tool and the RW data cleaner the app adds
        Assert.All(done.Where(q => q.Name.StartsWith("APP1") || q.Name.StartsWith("VOICE1")), q => Assert.False(q.IsTool));
        Assert.Equal(new[] { "reformat.tar.gz", "rwdatacleaner.tar.gz" }, done.Where(q => q.IsTool).Select(q => q.Name).OrderBy(n => n));
        Assert.Equal("Required tool", Ctx.T("Download.RequiredTool"));
    }

    [AvaloniaFact]
    public void A_utility_with_no_ticked_packages_tags_nothing()
    {
        var q = new Cyanlabs.Syn3Updater.App.ViewModels.QueueItem("x.tar.gz", "1 MB");
        Assert.False(q.IsTool);                                                               // the default: a utility's own files are not "extra"
    }
}
