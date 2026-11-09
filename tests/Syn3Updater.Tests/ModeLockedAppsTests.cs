using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Models;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// Reformat and Downgrade install from the packages, so the APPS package must be one of them (without it the unit would not boot). The row is ticked and locked from the start, instead of letting the
/// user untick it and then stopping them at Start with "no APPS package has been selected... Aborting!".
/// </summary>
public class ModeLockedAppsTests
{
    private static async Task Pick(AppEnv env, int rows)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == rows);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
    }

    private static void Force(string mode) { Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = mode; }
    private static PackageRow Apps(HomeViewModel h) => h.Packages.Single(r => r.Ivsu.Type == "APPS");
    private static PackageRow Voice(HomeViewModel h) => h.Packages.Single(r => r.Ivsu.Type == "VOICE");

    [AvaloniaTheory]
    [InlineData(InstallModes.Reformat, 4)]
    [InlineData(InstallModes.Downgrade, 6)]
    public async Task In_reformat_and_downgrade_the_APPS_row_is_ticked_and_locked_and_the_others_are_not(string mode, int rows)
    {
        using var env = new AppEnv(); Force(mode); await Pick(env, rows); var home = env.Main.Home;
        Assert.True(Apps(home).RequiredByMode); Assert.True(Apps(home).IsLocked); Assert.False(Apps(home).IsOptional); Assert.True(Apps(home).Selected);
        Assert.False(Apps(home).IsRequiredTool);                                              // a package, not a tool: it is downloaded as one
        Assert.False(Voice(home).RequiredByMode); Assert.True(Voice(home).IsOptional);
        Assert.Contains("Required for this install mode", Apps(home).Subtitle); Assert.DoesNotContain("Required for this install mode", Voice(home).Subtitle);
    }

    [AvaloniaFact]
    public async Task A_plain_install_leaves_the_APPS_row_free()
    {
        using var env = new AppEnv(); await Pick(env, 3); var home = env.Main.Home;
        Assert.False(Apps(home).RequiredByMode); Assert.True(Apps(home).IsOptional);
        Apps(home).ToggleCommand.Execute(null); Assert.False(Apps(home).Selected);
    }

    [AvaloniaFact]
    public async Task The_locked_APPS_row_cannot_be_unticked_by_a_click_by_setting_it_or_by_Select_none()
    {
        using var env = new AppEnv(); Force(InstallModes.Reformat); await Pick(env, 4); var home = env.Main.Home; var apps = Apps(home);
        apps.ToggleCommand.Execute(null); Assert.True(apps.Selected);
        apps.Selected = false; Assert.True(apps.Selected);
        home.SelectNoneCommand.Execute(null);
        Assert.True(apps.Selected); Assert.False(Voice(home).Selected);                       // the free package is unticked, the locked ones stay
        Assert.StartsWith("3 of 4 selected", home.SelectionSummary);                         // the APPS package and the two tools
        home.SelectAllCommand.Execute(null); Assert.StartsWith("4 of 4 selected", home.SelectionSummary);
        Voice(home).ToggleCommand.Execute(null); Assert.False(Voice(home).Selected);          // an ordinary row still toggles
    }

    [AvaloniaFact]
    public async Task If_the_mode_changes_under_a_loaded_list_the_lock_follows_it()
    {
        using var env = new AppEnv(); await Pick(env, 3); var home = env.Main.Home;
        Apps(home).Selected = false; Assert.False(Apps(home).Selected);                       // plain install: free to untick
        home.InstallMode = InstallModes.Reformat;
        Assert.True(Apps(home).RequiredByMode); Assert.True(Apps(home).Selected);             // now needed, and ticked again for the user
        home.InstallMode = InstallModes.Autoinstall;
        Assert.False(Apps(home).RequiredByMode); Assert.True(Apps(home).IsOptional);
    }

    [AvaloniaFact]
    public async Task Select_none_then_start_in_reformat_now_installs_the_APPS_package_and_does_not_abort()
    {
        using var env = new AppEnv(); Force(InstallModes.Reformat); await Pick(env, 4); var home = env.Main.Home;
        home.SelectNoneCommand.Execute(null);
        await home.StartCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        Assert.DoesNotContain(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.CancelNoApps")); Assert.DoesNotContain(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.NoPackagesSelected"));
        var reformat = File.ReadAllText(Path.Combine(env.UsbDir, "reformat.lst"));
        Assert.Contains("APPS=APP1.tar.gz", reformat); Assert.DoesNotContain("VOICE1", reformat);          // only the locked package was installed
    }

    [AvaloniaFact]
    public async Task A_release_with_no_APPS_package_is_still_stopped_with_the_old_message_as_a_safety_net()
    {
        using var env = new AppEnv(); Force(InstallModes.Reformat); await Pick(env, 4); var home = env.Main.Home;
        home.Packages.Remove(Apps(home));                                                     // a list that somehow has no APPS package at all
        await home.StartCommand.ExecuteAsync(null);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.CancelNoApps"));
        Assert.False(env.Main.Downloads.IsRunning); Assert.False(Workflow.Busy);
    }

    [Fact]
    public void A_locked_row_stays_ticked_whichever_way_it_became_locked()
    {
        var row = new PackageRow(new Ivsu { Type = "APPS", Name = "APP1" });
        row.Selected = false; row.RequiredByMode = true;                                      // locked while unticked: it is ticked at once
        Assert.True(row.Selected);
        row.RequiredByMode = false; row.Selected = false; Assert.False(row.Selected);          // unlocked: free again
    }
}

public class LockIconTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void A_locked_row_shows_a_lock_instead_of_the_checkbox_and_a_free_row_shows_the_checkbox()
    {
        var view = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/HomeView.axaml"));
        Assert.Matches("<CheckBox[^>]*IsVisible=\"\\{Binding IsOptional\\}\"", view);                          // only a row that can be unticked has the checkbox
        Assert.Matches("<PathIcon Classes=\"lock\"[^>]*IsVisible=\"\\{Binding IsLocked\\}\"", view);          // only a locked row has the lock
        Assert.Matches("<PathIcon Classes=\"lock\"[^>]*ToolTip.Tip=\"\\{Binding LockHint\\}\"", view);
        Assert.Matches("<PathIcon Classes=\"lock\"[^>]*AutomationProperties.Name=\"\\{Binding LockHint\\}\"", view);       // screen readers get the reason too
    }

    [Fact]
    public void The_lock_is_teal_on_a_package_the_mode_needs_and_orange_on_a_required_tool()
    {
        var app = File.ReadAllText(RepoFile("src/Syn3Updater.App/App.axaml"));
        Assert.Matches("PathIcon\\.lock\"><Setter Property=\"Foreground\" Value=\"\\{DynamicResource Brand\\}\"", app);
        Assert.Matches("Button\\.tile\\.tool PathIcon\\.lock\"><Setter Property=\"Foreground\" Value=\"\\{DynamicResource ToolBorder\\}\"", app);
    }

    [AvaloniaFact]
    public void The_lock_tooltip_says_why_the_row_is_locked()
    {
        using var env = new AppEnv();
        var tool = new PackageRow(new Ivsu { Name = "c", SpecialPackage = "rwdatacleaner" });
        var apps = new PackageRow(new Ivsu { Type = "APPS", Name = "APP1" }) { RequiredByMode = true };
        Assert.Equal("Required tool", tool.LockHint); Assert.Equal("Required for this install mode", apps.LockHint);
        Assert.True(tool.IsLocked); Assert.True(apps.IsLocked); Assert.False(new PackageRow(new Ivsu { Type = "VOICE" }).IsLocked);
    }
}
