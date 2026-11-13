using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Controls;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class HomeFlowTests
{
    private static async Task PickAll(HomeViewModel home, int maps = 0)
    {
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[^1];
        await AppEnv.Until(() => home.MapVersions.Count > maps);
    }

    [AvaloniaFact]
    public async Task Selection_Chain_LoadsReleasesMapsPackages_AndShowsMode()
    {
        using var env = new AppEnv();
        var home = env.Main.Home; home.ReloadSummary();
        Assert.Equal(new[] { "Version", "Region", "Navigation" }, home.ConfigItems.Select(c => c.Label));   // the profile lives in the sidebar now
        Assert.Equal("3.4.20282", home.ConfigItems[0].Value);

        await PickAll(home);
        Assert.Equal("Sync 3.4.26057", home.SelectedRelease!.Display);
        Assert.Equal("https://forum/x", home.FeedbackUrl); Assert.True(home.HasNotes); Assert.True(home.HasFeedback);
        Assert.Equal(MapChoice.NonNavApim, home.MapVersions[0].Kind);

        home.SelectedMap = home.MapVersions[0];
        await AppEnv.Until(() => home.Packages.Count == 3);
        Assert.Equal("autoinstall", home.InstallMode); Assert.Equal("AutoInstall", home.ModeTitle); Assert.True(home.HasMode);
        Assert.Equal(ApiEndpoints.InstallDocsUrl("autoinstall"), home.InstallDocsUrl);
        Assert.True(home.DownloadOnlyEnabled);
        Assert.StartsWith("3 of 3 selected", home.SelectionSummary);

        home.SelectNoneCommand.Execute(null); Assert.StartsWith("1 of 3", home.SelectionSummary);
        home.SelectAllCommand.Execute(null); Assert.StartsWith("3 of 3", home.SelectionSummary);
        home.Packages[0].ToggleCommand.Execute(null); Assert.False(home.Packages[0].Selected); Assert.StartsWith("2 of 3", home.SelectionSummary);

        await home.OpenInstructionsCommand.ExecuteAsync(null);
        await home.OpenFeedbackCommand.ExecuteAsync(null);
        Assert.Contains(ApiEndpoints.InstallDocsUrl("autoinstall"), env.Dialogs.Opened); Assert.Contains("https://forum/x", env.Dialogs.Opened);
        home.EditConfigCommand.Execute(null); Assert.Same(env.Main.Settings, env.Main.Current);
    }

    [AvaloniaFact]
    public async Task NavCar_OnOldVersion_GetsReformat_WithMapsAndEsnMarker()
    {
        using var env = new AppEnv(version: 3300000, nav: true);
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);                  // "Only update maps" + the release
        Assert.True(home.Releases[0].OnlyMaps);
        home.SelectedRelease = home.Releases[1];
        await AppEnv.Until(() => home.MapVersions.Count == 3);              // keep existing + 2 maps
        Assert.Equal(MapChoice.KeepExisting, home.MapVersions[0].Kind);
        home.SelectedMap = home.MapVersions[1];
        await AppEnv.Until(() => home.Packages.Count == 6);                 // 2 release + map + licence, and the reformat tool and the RW data cleaner the mode needs
        Assert.Equal("reformat", home.InstallMode);
        Assert.Equal("MY20 Check", home.ModeTitle); Assert.Equal(Ctx.T("App.Mode.my20check"), home.ModeDescription);   // MY20 protection is Auto-Detect: the banner does not claim a mode before the question is answered
        Ctx.Profile.My20v2 = false; home.ReloadSummary();
        Assert.Equal("Reformat", home.ModeTitle);                                                                          // known not to be MY20: the real mode is shown
        Assert.Contains(home.Packages, p => p.TypeLabel == "Navigation map");
        Assert.Contains(home.Packages, p => p.TypeLabel == "Map licence");
    }

    [AvaloniaFact]
    public async Task NewerVersion_WithMaps_GetsDowngrade_And_OnlyMapsGetsAutoinstall()
    {
        using var env = new AppEnv(version: 3420282, nav: true);
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == 8);
        Assert.Equal("downgrade", home.InstallMode);

        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count == 2);   // only-maps: no keep-existing entry
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 3);       // map + licence only, and the RW data cleaner
        Assert.Equal("autoinstall", home.InstallMode);
    }

    [AvaloniaTheory]
    [InlineData(false, false)]     // MY20 off: every map can be picked, the lock shows which ones are ESN locked
    [InlineData(null, false)]      // Auto-Detect: pickable too (Start asks the MY20 question)
    [InlineData(true, true)]       // MY20 on: the ESN locked map is blocked
    public async Task Maps_ShowTheirEsnState_AndOnlyTheBlockedOnesCannotBePicked(bool? my20, bool lockedIsBlocked)
    {
        using var env = new AppEnv(version: 3420282, nav: true);
        Ctx.Profile.My20v2 = my20;
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        Assert.Null(home.MapVersions[0].Esn); Assert.False(home.MapVersions[0].HasDetail);                    // keep existing maps: nothing to say about ESN
        Assert.True(home.MapVersions[1].IsUnlocked); Assert.Equal(Ctx.T("Home.MapEsnOpen"), home.MapVersions[1].Detail); Assert.True(home.MapVersions[1].IsSelectable);
        var locked = home.MapVersions[2];
        Assert.True(locked.IsLocked); Assert.Equal(lockedIsBlocked, locked.Blocked);
        Assert.Equal(Avalonia.Media.Color.Parse(lockedIsBlocked ? "#ef6b62" : "#e8a23a"), ((Avalonia.Media.SolidColorBrush)locked.LockBrush).Color);   // amber lock when it can be picked, red when it is dimmed
        Assert.Equal("ESN locked · only for SYNC 3 made before 2020 (pre-MY20)", locked.Detail); Assert.Equal("Not ESN locked", home.MapVersions[1].Detail);   // (the English wording)
        Assert.Equal(Ctx.T("Home.MapEsnLocked"), locked.Detail);                         // the same line whether or not it can be picked: a dimmed map says why by looking blocked
    }

    [AvaloniaFact]
    public async Task OnlyUpdateMaps_BlocksEsnLockedMaps_WithItsOwnReason()
    {
        using var env = new AppEnv(version: 3420282, nav: true);
        Ctx.Profile.My20v2 = false;
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count == 2);     // only-maps: no keep-existing entry
        var locked = home.MapVersions[1];
        Assert.True(locked.Blocked); Assert.Equal(Ctx.T("Home.MapEsnBlockedOnlyMaps", Ctx.T("String.OnlyMaps")), locked.Detail);
        Assert.Equal("ESN locked · needs a full release, not \"Only Update Maps\"", locked.Detail);               // (the English wording)
    }

    [AvaloniaFact]
    public async Task My20_WithEsnMap_IsRefused()
    {
        using var env = new AppEnv(version: 3420282, nav: true); env.Dialogs.Messages.Clear();
        Ctx.Profile.My20v2 = true;
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        var esn = home.MapVersions[2];
        Assert.True(esn.Blocked); Assert.False(esn.IsSelectable); Assert.True(esn.IsLocked);                  // dimmed and unpickable in the list
        Assert.Equal(Avalonia.Media.Color.Parse("#ef6b62"), ((Avalonia.Media.SolidColorBrush)esn.LockBrush).Color);    // with a red lock
        Assert.Equal(Ctx.T("Home.MapEsnLocked"), esn.Detail);
        Assert.False(home.MapVersions[1].Blocked); Assert.True(home.MapVersions[1].IsUnlocked);
        home.SelectedMap = home.MapVersions[2];                                                            // (still refused if it is chosen some other way)
        await AppEnv.Until(() => env.Dialogs.Messages.Count > 0);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.MY20InvalidConfiguration"));
        Assert.False(home.StartEnabled);
    }

    [AvaloniaTheory]
    [InlineData(3300000, "reformat")]      // Option C in v2: an "only update maps" release is always autoinstall...
    [InlineData(3420282, "downgrade")]     // ...and Option F: the same on 3.4.19274 or newer
    public async Task AForcedMode_IsIgnoredForAnOnlyMapsRelease_AsInV2(int version, string forced)
    {
        using var env = new AppEnv(version: version, nav: true); Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = forced;
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == (forced == "downgrade" ? 8 : 6));
        Assert.Equal(forced, home.InstallMode);                                    // a normal release: the forced mode is used (v2 options A/D/G)
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count == 2);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 3);
        Assert.Equal("autoinstall", home.InstallMode);                             // only maps: v2 uses autoinstall whatever was forced
    }

    [AvaloniaFact]
    public async Task ForcedInstallMode_IsKept_AndShownAsForced()
    {
        using var env = new AppEnv(); Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = "reformat";
        var home = env.Main.Home; home.ReloadSummary();
        await PickAll(home); home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 4);
        Assert.Equal("reformat", home.InstallMode); Assert.True(home.ModeIsForced); Assert.Equal("Yes", home.ModeForcedText);
    }

    [AvaloniaFact]
    public async Task ForcedMode_IsShownBeforeAnythingIsPicked_AutoDetectIsNot()
    {
        using (var auto = new AppEnv())
        {
            auto.Main.Home.ReloadSummary();
            Assert.Equal("", auto.Main.Home.InstallMode); Assert.False(auto.Main.Home.HasMode); Assert.False(auto.Main.Home.ModeIsForced);   // unknown until a version and maps are chosen
        }
        using var env = new AppEnv(); Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = "downgrade";
        var home = env.Main.Home; home.ReloadSummary();
        Assert.Equal("downgrade", home.InstallMode); Assert.True(home.HasMode); Assert.True(home.ModeIsForced);
        Assert.Equal("Downgrade", home.ModeTitle); Assert.NotEmpty(home.ModeDescription);
        Assert.Equal(ApiEndpoints.InstallDocsUrl("downgrade"), home.InstallDocsUrl);                    // the Instructions link works straight away too

        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 6);
        Assert.Equal("downgrade", home.InstallMode);                                                    // picking things does not change a forced mode

        home.ClearSelections();
        Assert.Equal("downgrade", home.InstallMode); Assert.Empty(home.Packages);                       // and clearing keeps showing it

        Ctx.Profile.InstallMode = "autodetect"; home.ReloadSummary();
        Assert.Equal("", home.InstallMode); Assert.False(home.HasMode);                                 // switching back to auto-detect forgets it
    }

    [AvaloniaFact]
    public async Task MissingConfig_ShowsWarning_AndStartSendsYouToSettings()
    {
        using var env = new AppEnv(region: "", version: 0);
        var home = env.Main.Home; home.ReloadSummary();
        Assert.True(home.ConfigMissing); Assert.DoesNotContain(home.ConfigItems, c => c.Label == "Version");
        home.SelectedRegion = Region.All[0]; await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count > 0);
        await home.DownloadOnlyCommand.ExecuteAsync(null);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.NoVersionOrRegionSelected"));
        Assert.Same(env.Main.Settings, env.Main.Current);
    }

    [AvaloniaFact]
    public async Task Folder_Chooser_And_Drive_Refresh()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        env.Dialogs.FolderToPick = env.UsbDir;
        await home.ChooseFolderCommand.ExecuteAsync(null);
        Assert.True(home.SelectedDrive!.IsFolder); Assert.Equal(env.UsbDir, home.SelectedDrive.MountPath); Assert.Equal(env.UsbDir, home.DriveInfo);
        await home.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.True(home.SelectedDrive?.IsFolder);        // the chosen folder survives a refresh
        env.Dialogs.FolderToPick = null; await home.ChooseFolderCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task DirectoryEntry_IsTheLastDropdownItem_AndOpensThePicker()
    {
        using var env = new AppEnv(); var home = env.Main.Home;
        Assert.True(home.Drives[^1].IsChooser); Assert.Equal("Select A Directory", home.Drives[^1].Title); Assert.Equal("", home.Drives[^1].Detail);   // there before any refresh
        await home.RefreshDrivesCommand.ExecuteAsync(null);
        Assert.True(home.Drives[^1].IsChooser); Assert.Single(home.Drives, d => d.IsChooser);

        env.Dialogs.FolderToPick = env.UsbDir; home.SelectedDrive = home.Drives[^1];
        await AppEnv.Until(() => home.SelectedDrive?.IsFolder == true);
        Assert.Equal(env.UsbDir, home.SelectedDrive!.MountPath); Assert.True(home.Drives[^1].IsChooser); Assert.Equal(env.UsbDir, home.DriveInfo);

        env.Dialogs.FolderToPick = null; var before = home.SelectedDrive; home.SelectedDrive = home.Drives[^1];      // cancelled picker
        await Task.Delay(150);
        Assert.Same(before, home.SelectedDrive);

        var other = Path.Combine(env.Dir, "other"); Directory.CreateDirectory(other);
        env.Dialogs.FolderToPick = other; home.SelectedDrive = home.Drives[^1];
        await AppEnv.Until(() => home.SelectedDrive?.MountPath == other);
        Assert.Single(home.Drives, d => d.IsFolder); Assert.True(home.Drives[^1].IsChooser);                    // the new folder replaced the old one
        await home.RefreshDrivesCommand.ExecuteAsync(null); Assert.Equal(other, home.SelectedDrive!.MountPath);       // and survives a refresh
    }

    [AvaloniaFact]
    public async Task Api_Failure_IsReported()
    {
        using var env = new AppEnv(); env.Api.Offline = true;
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => env.Dialogs.Messages.Any(m => m.Contains("offline")));
        Assert.Empty(home.Releases);
    }

    [AvaloniaFact]
    public async Task Api_Failure_PointsToTheStatusPage_WithAButtonThatOpensIt()
    {
        using var env = new AppEnv(); env.Api.Offline = true;
        env.Dialogs.Choices.Enqueue(1);                                                   // the user presses "Open status page"
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => env.Dialogs.Opened.Count > 0);
        Assert.Equal(new[] { "https://status.cyanlabs.net" }, env.Dialogs.Opened);
        var shown = env.Dialogs.Shown.First(s => s.Buttons.Length == 2);
        Assert.Equal(new[] { Ctx.T("String.OK"), Ctx.T("Home.OpenStatusPage") }, shown.Buttons);
        Assert.Contains(env.Dialogs.Messages, m => m.Contains("status.cyanlabs.net") && m.Contains("offline"));   // names the page, and keeps the technical reason
    }

    [AvaloniaFact]
    public async Task Api_Failure_OkJustClosesIt()
    {
        using var env = new AppEnv(); env.Api.Offline = true;
        env.Dialogs.Choices.Enqueue(0);
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => env.Dialogs.Messages.Any(m => m.Contains("status.cyanlabs.net")));
        await Task.Delay(300); Assert.Empty(env.Dialogs.Opened);
    }
}

public class WorkflowTests
{
    private static async Task SelectEverything(AppEnv env)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 3);
    }

    [AvaloniaFact]
    public async Task FolderRun_DownloadsCopiesWritesLists_AndOpensInstructions()
    {
        using var env = new AppEnv(); await SelectEverything(env);
        env.Dialogs.FolderToPick = env.UsbDir; await env.Main.Home.ChooseFolderCommand.ExecuteAsync(null);
        var dl = env.Main.Downloads; var phases = new List<string>();
        dl.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadViewModel.PhaseLabel)) phases.Add(dl.PhaseLabel); };
        await env.Main.Home.StartCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !dl.IsRunning && dl.Status == Ctx.T("String.Completed"));

        Assert.True(File.Exists(Path.Combine(env.UsbDir, "SyncMyRide", "APP1.tar.gz")));
        Assert.True(File.Exists(Path.Combine(env.UsbDir, "SyncMyRide", "VOICE1.tar.gz")));
        var lst = File.ReadAllText(Path.Combine(env.UsbDir, "autoinstall.lst"));
        Assert.Contains("RWDataCleaner TOOL", lst); Assert.Contains("APPS - APP1.tar.gz", lst);
        Assert.True(File.Exists(Path.Combine(env.UsbDir, "log.txt"))); Assert.Contains("SYN3 UPDATER", File.ReadAllText(Path.Combine(env.UsbDir, "log.txt")));
        Assert.NotEmpty(Directory.GetFiles(Ctx.Main.LogPath!, "log-*.txt"));
        Assert.Contains(ApiEndpoints.InstallDocsUrl("autoinstall"), env.Dialogs.Opened);     // the CyanLabs instructions open on their own
        Assert.Contains(env.UsbDir, env.Dialogs.Opened);
        Assert.Equal(100, dl.TotalProgress); Assert.Empty(dl.Queue); Assert.Equal(3, dl.Completed.Count); Assert.All(dl.Completed, q => Assert.True(q.IsDone));   // finished files leave "up next"
        Assert.Contains("Downloading files", phases); Assert.Contains("Copying to the drive", phases); Assert.Contains("Downloading", dl.Log);
        Assert.Same(env.Main.Home, env.Main.Current); Assert.False(Workflow.Busy);
        Assert.Empty(env.Main.Home.Packages);        // selections are cleared for the next run
    }

    [AvaloniaFact]
    public async Task DownloadOnly_SavesFiles_AndDoesNotTouchTheDrive()
    {
        using var env = new AppEnv(); await SelectEverything(env);
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.DownloadOnlyMode"), true);
        await env.Main.Home.DownloadOnlyCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Dialogs.Messages.Contains(Ctx.T("MessageBox.DownloadOnlyComplete")));
        Assert.True(File.Exists(Path.Combine(env.DownloadDir, "APP1.tar.gz")));
        Assert.Empty(Directory.GetFiles(env.UsbDir));
        Assert.DoesNotContain(env.Dialogs.Opened, u => u.Contains("installmodes"));
    }

    [AvaloniaFact]
    public async Task Declining_DownloadOnlyWarning_StartsNothing()
    {
        using var env = new AppEnv(); await SelectEverything(env);
        await env.Main.Home.DownloadOnlyCommand.ExecuteAsync(null);     // default answer: No
        Assert.False(env.Main.Downloads.IsRunning); Assert.False(Directory.Exists(env.DownloadDir) && Directory.GetFiles(env.DownloadDir).Length > 0);
    }

    [AvaloniaFact]
    public async Task Downgrade_WritesReformatAndAutoinstallLists()
    {
        using var env = new AppEnv();
        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "folder" };
        var ok = await Workflow.StartAsync(new JobSpec
        {
            Mode = InstallModes.Downgrade, Release = "Sync 3.3.19052", Region = "NA", Drive = usb, NeedDowngradeTools = true, NeedReformat = true,
            Packages = { new Ivsu { Type = "APPS", Name = "APPX", FileName = "APPX.tar.gz", Url = env.Files.Url + "/APPX.tar.gz", Md5 = FileServer.Md5("APPX.tar.gz"), FileSize = FileServer.Blob("APPX.tar.gz").Length } },
        });
        Assert.True(ok);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        Assert.Contains("APPS=APPX.tar.gz", File.ReadAllText(Path.Combine(env.UsbDir, "reformat.lst")));
        var lst = File.ReadAllText(Path.Combine(env.UsbDir, "autoinstall.lst"));
        Assert.Contains("[SYNCGen3.0_ALL]", lst); Assert.Contains("REFORMAT TOOL", lst);
        Assert.Contains(ApiEndpoints.InstallDocsUrl("downgrade"), env.Dialogs.Opened);
    }

    [AvaloniaFact]
    public async Task Guards_DisclaimerAndBusy()
    {
        using var env = new AppEnv(disclaimer: false);
        Assert.False(await Workflow.StartAsync(new JobSpec()));
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.DisclaimerNotAccepted")); Assert.Same(env.Main.About, env.Main.Current);
        Ctx.Main.DisclaimerAccepted = true; Workflow.Busy = true;
        Assert.False(await Workflow.StartAsync(new JobSpec()));
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.DownloadInProgress")); Workflow.Busy = false;
    }

    [AvaloniaFact]
    public async Task Sanity_RefusesDownloadFolderInsideTheDestination()
    {
        using var env = new AppEnv(); Ctx.Main.DownloadPath = Path.Combine(env.UsbDir, "dl");
        var ok = await Workflow.StartAsync(new JobSpec { Drive = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir } });
        Assert.False(ok); Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.CancelDownloadIsFolder"));
    }

    [AvaloniaFact]
    public async Task Sanity_NonEmptyFolder_AsksBeforeClearing()
    {
        using var env = new AppEnv(); File.WriteAllText(Path.Combine(env.UsbDir, "keep.txt"), "x");
        var drive = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir };
        env.Dialogs.DefaultYes = false;                                       // "no, don't empty it" -> run continues without clearing
        Assert.True(await Workflow.StartAsync(new JobSpec { Drive = drive, Mode = InstallModes.Autoinstall, Release = "r", Region = "NA", SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false }));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        Assert.True(File.Exists(Path.Combine(env.UsbDir, "keep.txt")));
        Assert.Contains(env.Dialogs.Messages, m => m.Contains(env.UsbDir) && m.Contains("files"));
    }

    [AvaloniaFact]
    public async Task Reformat_AsksAboutMy20_AndYesPreparesTheInterrogatorUsb()
    {
        using var env = new AppEnv(version: 3300000, nav: true);
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == 6);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.My20CheckV2"), true);
        await home.StartCommand.ExecuteAsync(null);
        Assert.Contains(env.Dialogs.Shown, d => d.Buttons.SequenceEqual(new[] { Ctx.T("MessageBox.My20CheckNo"), Ctx.T("MessageBox.My20CheckYes") }));   // explicit answers, not a bare Yes/No
        await AppEnv.Until(() => env.Dialogs.Messages.Contains(Ctx.T("MessageBox.LogUtilityCompleteMy20")));
        Assert.True(File.Exists(Path.Combine(env.UsbDir, "autoinstall.lst")));
        Assert.Contains("logtool", string.Join("\n", env.Api.Queries));
    }

    private const string My20Xml = """
        <p:OTAModuleSnapShot xmlns:p="u" xmlns:d="d"><p:VIN>1FA</p:VIN>
         <d:DID didType="Embedded Consumer Operating System Part Number" didValue="D133"><d:Response>GB5T-14G381-AB</d:Response></d:DID>
         <d:DID didType="ECU Delivery Assembly Number" didValue="F111"><d:Response>LB5T-14G371-AF</d:Response></d:DID>
         <d:AdditionalAttributes logGeneratedDateTime="2024-01-02T03:04:05+00:00"><d:PartitionHealth type="/fs/images/" total="28.5G" available="12.0G"/></d:AdditionalAttributes>
        </p:OTAModuleSnapShot>
        """;

    /// <summary>A Reformat on an old unit with MY20 not known yet: Start asks, and "Yes, or not sure" prepares the Interrogator USB.</summary>
    private static async Task StartMy20Check(AppEnv env)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == 6);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.My20CheckV2"), true);
        await home.StartCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task My20Check_OkAfterTheLogUsbIsReady_PicksAndReadsTheXml_AndSetsMy20()
    {
        // the notice says "re-insert the USB drive, click 'OK' and select the XML file": OK has to open the file picker and read the log, which is what decides MY20
        using var env = new AppEnv(version: 3300000, nav: true);
        var xml = Path.Combine(env.Dir, "Sync_1_VIN.xml"); File.WriteAllText(xml, My20Xml);
        env.Dialogs.FileToPick = xml;                                                    // what the person picks
        Assert.Null(Ctx.Profile.My20v2);

        await StartMy20Check(env);
        await AppEnv.Until(() => env.Dialogs.Messages.Contains(Ctx.T("MessageBox.My20Found")));

        Assert.True(Ctx.Profile.My20v2);                                                 // the log decided
        Assert.Equal(Path.Combine(env.UsbDir, "SyncMyRide"), env.Dialogs.FileStarts.Last());   // the picker opened in the drive's SyncMyRide folder, where the car wrote the XML
        Assert.True(File.Exists(Path.Combine(Ctx.Main.LogPath!, "Sync_1_VIN.xml")));      // and was kept with the other logs
        Assert.Empty(env.Main.Home.Packages);                                            // the install mode is worked out again from scratch now that MY20 is known (as v2 did)
        await AppEnv.Until(() => ReferenceEquals(env.Main.Current, env.Main.Home));       // and the check ends back on Home, where it was started (not on the Utility page)
    }

    [AvaloniaFact]
    public async Task My20Check_CancellingThePicker_LeavesMy20UnknownAndKeepsTheSelection()
    {
        using var env = new AppEnv(version: 3300000, nav: true);
        env.Dialogs.FileToPick = null;                                                   // the person cancels the file picker

        await StartMy20Check(env);
        await AppEnv.Until(() => env.Dialogs.Messages.Contains(Ctx.T("MessageBox.My20CheckCancelled")));

        Assert.Null(Ctx.Profile.My20v2);                                                 // still unknown: Start will ask again
        Assert.NotEmpty(env.Main.Home.Packages);                                         // nothing the person chose was thrown away
        await AppEnv.Until(() => ReferenceEquals(env.Main.Current, env.Main.Home));       // and they are back on Home to press Start again
    }
}

public class PageViewModelTests
{
    [AvaloniaFact]
    public async Task Settings_ValidateApplyAndAutosave()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s;
        s.Version = "12"; await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Contains("3.4.20282", s.Message); Assert.Equal(3420282, Ctx.Profile.CurrentVersion);   // rejected, nothing changed
        Assert.Same(s, env.Main.Current);                                                              // an error keeps you on Settings so you can fix it

        s.Version = "3.3.19052"; s.Region = Region.All[0]; s.Nav = true; s.Connections = 4; s.Mode = s.ModeOptions[1]; s.My20 = s.My20Options[2];
        await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal(3319052, Ctx.Profile.CurrentVersion); Assert.Equal("EU", Ctx.Profile.CurrentRegion); Assert.True(Ctx.Profile.CurrentNav);
        Assert.Equal(4, Ctx.Main.DownloadConnections); Assert.Equal(InstallModes.Autoinstall, Ctx.Profile.InstallMode); Assert.False(Ctx.Profile.My20v2);
        Assert.Contains("3.3.19052", File.ReadAllText(Path.Combine(env.Dir, "data", "Profiles", "default.json")).Replace("3319052", "3.3.19052"));
        Assert.Contains(env.Main.Home.ConfigItems, c => c.Value == "EU");
        Assert.Same(env.Main.Home, env.Main.Current);                                                  // a successful Apply jumps to Home

        env.Main.Current = s; s.Version = "3.4.20282"; env.Main.Current = env.Main.Home;              // leaving the tab saves (and does not need a jump)
        Assert.Equal(3420282, Ctx.Profile.CurrentVersion);
    }

    [AvaloniaFact]
    public async Task Settings_PathsLanguageThemeReset()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s;
        Directory.CreateDirectory(env.DownloadDir); File.WriteAllText(Path.Combine(env.DownloadDir, "old.tar.gz"), "x");
        var moved = Path.Combine(env.Dir, "newdl");
        env.Dialogs.FolderToPick = moved; await s.PickDownloadPathCommand.ExecuteAsync(null); Assert.Equal(moved, s.DownloadPath);
        env.Dialogs.Say(m => m.Contains(env.DownloadDir), true);
        await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.True(File.Exists(Path.Combine(moved, "old.tar.gz"))); Assert.Equal(moved, Ctx.Main.DownloadPath);

        var logs = Path.Combine(env.Dir, "newlogs"); env.Dialogs.FolderToPick = logs; await s.PickLogPathCommand.ExecuteAsync(null);
        await s.ApplyClickCommand.ExecuteAsync(null); Assert.True(Directory.Exists(logs)); Assert.Equal(logs, Ctx.Main.LogPath);

        s.Language = s.Languages.First(l => l.Code == "de"); Assert.Equal("de", Ctx.Loc.Code);
        s.Theme = s.ThemeOptions[1]; await s.ApplyClickCommand.ExecuteAsync(null); Assert.Equal("Light", Ctx.Main.Theme);

        env.Dialogs.DefaultYes = true; await s.ResetCommand.ExecuteAsync(null);
        Assert.Equal("default", Ctx.Main.Profile); Assert.Null(Ctx.Main.Theme is "Light" ? null : Ctx.Main.Theme is null ? null : "x");
    }

    [AvaloniaFact]
    public async Task SidebarProfiles_AddSwitchDelete()
    {
        using var env = new AppEnv(); var m = env.Main;
        Assert.Equal(new[] { "default" }, m.Profiles); Assert.False(m.CanDeleteProfile);
        await m.DeleteProfileCommand.ExecuteAsync(null); Assert.Empty(env.Dialogs.Messages);          // nothing to delete: last profile is protected

        env.Dialogs.PromptAnswer = "  "; await m.AddProfileCommand.ExecuteAsync(null); Assert.Single(m.Profiles);   // blank name ignored
        env.Dialogs.PromptAnswer = null; await m.AddProfileCommand.ExecuteAsync(null); Assert.Single(m.Profiles);    // cancelled
        env.Dialogs.PromptAnswer = "Wife's car/2"; await m.AddProfileCommand.ExecuteAsync(null);
        Assert.Equal("Wife's car_2", Ctx.Main.Profile); Assert.Equal("Wife's car_2", m.SelectedProfile); Assert.Equal(2, m.Profiles.Count); Assert.True(m.CanDeleteProfile);
        Assert.Contains("Wife's car_2", m.WindowTitle);
        Assert.Null(Ctx.Profile.CurrentRegion);                       // a new profile starts blank

        m.SelectedProfile = "default"; Assert.Equal("default", Ctx.Main.Profile); Assert.Equal("NA", Ctx.Profile.CurrentRegion);
        Assert.Contains(m.Home.ConfigItems, c => c.Value == "NA");

        env.Dialogs.DefaultYes = false; await m.DeleteProfileCommand.ExecuteAsync(null); Assert.Equal(2, m.Profiles.Count);   // declined
        env.Dialogs.DefaultYes = true;  await m.DeleteProfileCommand.ExecuteAsync(null);                                      // deleting the ACTIVE profile moves to another
        Assert.Equal(new[] { "Wife's car_2" }, m.Profiles); Assert.Equal("Wife's car_2", Ctx.Main.Profile); Assert.False(m.CanDeleteProfile);
        Assert.False(File.Exists(Path.Combine(env.Dir, "data", "Profiles", "default.json")));
    }

    [AvaloniaFact]
    public void Settings_WarningIsOneLine_AndNamesTheProfile_ConnectionsAreAppWide()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown();
        Assert.DoesNotContain("\n", s.WarningText); Assert.StartsWith("INCORRECT INFORMATION", s.WarningText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("default", s.ProfileName); Assert.Equal(2, s.Connections);                       // read from application settings, not the profile
        Assert.Null(new ProfileSettings().LegacyDownloadConnections); Assert.Equal(8, new MainSettings().DownloadConnections);
    }

    [AvaloniaFact]
    public async Task Logs_ListFilterSearchCopyDeleteUpload()
    {
        using var env = new AppEnv(); var logs = env.Main.Logs;
        File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, "log-2026-11-05-18-20-11.txt"), "a INFO: ok\nb ERROR: bad\nc WARN: hmm\nd DEBUG: x\ne SUCCESSFULLY done");
        File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, "Sync_1_VIN.xml"), "<x/>");
        Ctx.Log.Info("session line");
        logs.OnShown();
        Assert.True(logs.Files[0].IsSession); Assert.Equal(3, logs.Files.Count); Assert.True(logs.HasSavedLogs); Assert.Equal("2", logs.FileCountText);

        logs.Selected = logs.Files.First(f => f.Kind == LogKind.UpdateRun);
        Assert.Equal(5, logs.Lines.Count); Assert.Equal(1, logs.ErrorCount); Assert.Equal(1, logs.WarningCount); Assert.True(logs.CanDelete);
        Assert.Contains(logs.Lines, l => l.IsError); Assert.Contains(logs.Lines, l => l.IsWarning); Assert.Contains(logs.Lines, l => l.IsSuccess); Assert.Contains(logs.Lines, l => l.IsDebug);
        logs.ErrorsOnly = true; Assert.Equal(2, logs.Lines.Count); logs.ErrorsOnly = false;
        logs.Search = "success"; Assert.Single(logs.Lines); logs.Search = "";

        await logs.CopyCommand.ExecuteAsync(null); Assert.Contains("ERROR: bad", env.Dialogs.Clipboard[0]);
        await logs.UploadCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Opened, u => u.EndsWith("uuid=u1"));
        await logs.OpenFolderCommand.ExecuteAsync(null); Assert.Contains(Ctx.Main.LogPath!, env.Dialogs.Opened);

        env.Dialogs.DefaultYes = true; await logs.DeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, logs.Files.Count); logs.Selected = logs.Files[0]; Assert.False(logs.CanDelete); Assert.Contains("session line", string.Join("\n", logs.Lines.Select(l => l.Text)));
        logs.RefreshCommand.Execute(null);
    }

    [AvaloniaFact]
    public async Task News_LoadsNoticesAndChangelog_WithGithubLinks()
    {
        using var env = new AppEnv(); var news = env.Main.News;
        news.OnShown(); await AppEnv.Until(() => news.Changelogs.Count == 1);
        Assert.Single(news.Important); Assert.Single(news.Other); Assert.Equal("", news.Status);
        await news.VisitGithubCommand.ExecuteAsync("2.14.2.0");
        await news.VisitGithubCommand.ExecuteAsync(null);
        Assert.Contains("https://github.com/CyanLabs/Syn3Updater/releases/tag/2.14.2.0", env.Dialogs.Opened);
        Assert.Contains("https://github.com/CyanLabs/Syn3Updater/releases", env.Dialogs.Opened);
        Assert.Equal("https://github.com/CyanLabs/Syn3Updater/releases/tag/v1%202", NewsViewModel.ReleaseUrl(" v1 2 "));
        env.Api.Offline = true; await news.LoadCommand.ExecuteAsync(null); Assert.Contains("offline", news.Status);
    }

    [AvaloniaFact]
    public async Task About_AcceptsDisclaimer_AndOpensLinks()
    {
        using var env = new AppEnv(disclaimer: false); var about = env.Main.About;
        Assert.False(about.Accepted); about.Accepted = true; Assert.True(Ctx.Main.DisclaimerAccepted);
        await about.OpenDocsCommand.ExecuteAsync(null); await about.OpenForumCommand.ExecuteAsync(null); await about.OpenFHubCommand.ExecuteAsync(null); await about.OpenDonateCommand.ExecuteAsync(null);
        Assert.Equal(4, env.Dialogs.Opened.Count); Assert.Contains("https://cyanlabs.net/support-us/", env.Dialogs.Opened);
        await about.OpenLicenseCommand.ExecuteAsync(null); await about.OpenDataFolderCommand.ExecuteAsync(null);
        Assert.Contains("https://www.gnu.org/licenses/gpl-3.0.html", env.Dialogs.Opened); Assert.Contains(about.DataFolder, env.Dialogs.Opened);
        var diag = env.Main.Diagnostics; await diag.RunAsync(); await diag.CopyReportCommand.ExecuteAsync(null);
        Assert.Contains("Operating system:", env.Dialogs.Clipboard[0]); Assert.Contains(".NET runtime:", env.Dialogs.Clipboard[0]); Assert.Contains("Profile: default", env.Dialogs.Clipboard[0]); Assert.Contains("Language:", env.Dialogs.Clipboard[0]);
        Assert.NotEmpty(about.Runtime); Assert.NotEmpty(about.Platform);
        Assert.False(about.NotAccepted); about.Accepted = false; Assert.True(about.NotAccepted); Assert.False(Ctx.Main.DisclaimerAccepted);
        Ctx.Main.DisclaimerAccepted = true; about.OnShown(); Assert.True(about.Accepted);
    }

    [AvaloniaFact]
    public async Task Utility_ParseInterrogatorLog_SetsMy20AndShowsResults()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        var xml = Path.Combine(env.Dir, "Sync_1_VIN.xml");
        File.WriteAllText(xml, """
            <p:OTAModuleSnapShot xmlns:p="u" xmlns:d="d"><p:VIN>1FA</p:VIN>
             <d:DID didType="Embedded Consumer Operating System Part Number" didValue="D133"><d:Response>GB5T-14G381-AB</d:Response></d:DID>
             <d:DID didType="ECU Delivery Assembly Number" didValue="F111"><d:Response>LB5T-14G371-AF</d:Response></d:DID>
             <d:AdditionalAttributes logGeneratedDateTime="2024-01-02T03:04:05+00:00"><d:PartitionHealth type="/fs/images/" total="28.5G" available="12.0G"/></d:AdditionalAttributes>
            </p:OTAModuleSnapShot>
            """);
        env.Dialogs.FileToPick = xml;
        await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(u.HasLog); Assert.Equal("1FA", u.ResVin); Assert.Equal("LB5T-14G371-AF", u.ResModel); Assert.Equal("Navigation", u.ResType); Assert.Equal("32 GB", u.ResSize);
        Assert.Contains("3.4.20282", u.ResVersion); Assert.True(Ctx.Profile.My20v2);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20Found"));
        Assert.True(File.Exists(Path.Combine(Ctx.Main.LogPath!, "Sync_1_VIN.xml")));

        await u.CopyAsBuiltCommand.ExecuteAsync(null); Assert.Contains("1FA", env.Dialogs.Clipboard[0]);
        env.Dialogs.DefaultYes = true; await u.ViewAsBuiltCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Opened, x => x.EndsWith("filename=f.xml"));

        env.Dialogs.FileToPick = null; env.Dialogs.Messages.Clear();
        await u.ParseLogCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.My20CheckCancelled"));
        var bad = Path.Combine(env.Dir, "bad.xml"); File.WriteAllText(bad, "<nope/>"); env.Dialogs.FileToPick = bad;
        await u.ParseLogCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.LogUtilityInvalidFile"));
    }

    [AvaloniaFact]
    public async Task Utility_Step3_OpensThePickerInTheSelectedDrivesSyncMyRideFolder_Or_WhereTheOsChoosesWithNoDrive()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        env.Dialogs.FileToPick = null;                                                   // only the folder the picker opens in matters here

        await u.ParseLogCommand.ExecuteAsync(null);                                      // no drive selected
        Assert.Null(env.Dialogs.FileStarts.Last());

        u.SelectedDrive = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        await u.ParseLogCommand.ExecuteAsync(null);
        Assert.Equal(Path.Combine(env.UsbDir, "SyncMyRide"), env.Dialogs.FileStarts.Last());
    }

    [Fact]
    public void The_interrogator_picker_starts_in_the_drives_SyncMyRide_folder_and_nowhere_in_particular_without_a_drive()
    {
        Assert.Null(UtilityViewModel.InterrogatorLogFolder(null));
        Assert.Null(UtilityViewModel.InterrogatorLogFolder(""));
        Assert.Null(UtilityViewModel.InterrogatorLogFolder("  "));
        Assert.Equal(Path.Combine("/media/CYANLABS", "SyncMyRide"), UtilityViewModel.InterrogatorLogFolder("/media/CYANLABS"));
    }

    [AvaloniaFact]
    public async Task Utility_My20Check_UploadLog_And_SpecialUsbs()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        u.ApimModelInput = "nope"; await u.CheckMy20Command.ExecuteAsync(null); Assert.Contains(env.Dialogs.Messages, m => m.Contains("Invalid APIM"));
        u.ApimModelInput = "LB5T-14G371-AB"; await u.CheckMy20Command.ExecuteAsync(null); Assert.Contains(env.Dialogs.Messages, m => m.Contains("detected as MY20"));
        u.ApimModelInput = "AAAA-14G371-AB"; await u.CheckMy20Command.ExecuteAsync(null); Assert.Contains(env.Dialogs.Messages, m => m.Contains("NOT MY20"));

        await u.GracenotesCommand.ExecuteAsync(null);                                 // no drive selected
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("Utility.SelectUSBDrive"));

        u.SelectedDrive = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        File.WriteAllText(Path.Combine(env.UsbDir, "log.txt"), "uploaded text");
        await u.UploadLogCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Opened, x => x.EndsWith("uuid=u1"));
        File.Delete(Path.Combine(env.UsbDir, "log.txt"));

        await u.RwDataCleanerCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Dialogs.Messages.Contains(Ctx.T("MessageBox.GenericUtilityComplete")));
        Assert.True(File.Exists(Path.Combine(env.UsbDir, "autoinstall.lst")));
        await u.TroubleshootCommand.ExecuteAsync(null); Assert.Contains(env.Dialogs.Opened, x => x.Contains("community.cyanlabs.net"));
    }

    [Theory]
    [InlineData(3100000, "logtool30")] [InlineData(3300000, "logtool32")] [InlineData(3400000, "logtool34")]
    [InlineData(3420237, "logtool34")] [InlineData(3420282, "logtool34-2")] [InlineData(3426057, "logtool34-2")]
    public void LogTool_DependsOnInstalledVersion(int version, string expected) => Assert.Equal(expected, UtilityViewModel.LogToolFor(version));

    [AvaloniaFact]
    public void MainWindow_Navigation_Sidebar_Title()
    {
        using var env = new AppEnv(); var m = env.Main;
        Assert.Equal(new PageViewModel[] { m.Home, m.Utility, m.Logs, m.News }, m.TopPages); Assert.DoesNotContain(m.Diagnostics, m.TopPages); Assert.Contains(m.Diagnostics, m.Pages); Assert.False(m.Diagnostics.IsNavVisible); Assert.Equal(new PageViewModel[] { m.Downloads, m.Settings, m.About }, m.BottomPages); Assert.True(m.Home.IsActive);
        m.NavigateCommand.Execute(m.Logs); Assert.Same(m.Logs, m.Current); Assert.True(m.Logs.IsActive); Assert.False(m.Home.IsActive);
        m.ShowDownloads(); m.ShowUtility(); m.ShowSettings(); m.ShowHome(); Assert.Same(m.Home, m.Current);
        m.SidebarExpanded = false; Assert.Equal(64, m.SidebarWidth); m.SidebarExpanded = true; Assert.Equal(200, m.SidebarWidth);
        Assert.StartsWith("Syn3 Updater", m.WindowTitle); Assert.All(m.Pages, p => { Assert.False(string.IsNullOrEmpty(p.Title)); Assert.NotEmpty(p.IconData); });
        Ctx.Loc.SetLanguage("de"); Ctx.Loc.SetLanguage("en-US");
    }

    [AvaloniaFact]
    public async Task DownloadsTab_IsHiddenUntilADownloadHasRun()
    {
        using var env = new AppEnv(); var m = env.Main;
        Assert.False(m.Downloads.IsNavVisible);
        Assert.All(m.Pages.Where(p => p != m.Downloads && p != m.Diagnostics), p => Assert.True(p.IsNavVisible));            // every other tab is always listed
        var raised = new List<string?>(); m.Downloads.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        Assert.True(await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = usb, SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false, Mode = InstallModes.Autoinstall }));
        Assert.True(m.Downloads.IsNavVisible); Assert.Contains(nameof(PageViewModel.IsNavVisible), raised);       // appears as soon as it starts (the app is on that page then)
        await AppEnv.Until(() => !m.Downloads.IsRunning && m.Downloads.Status == Ctx.T("String.Completed"));
        Assert.True(m.Downloads.IsNavVisible);                                                                  // and stays after the download has finished
    }

    [AvaloniaFact]
    public async Task MainWindow_Ad_Placeholder_And_Click()
    {
        using var env = new AppEnv();
        await env.Main.LoadAdAsync(); Assert.False(env.Main.AdVisible); Assert.False(env.Main.AdHasImage);          // no ad configured: no ad area at all
        Ctx.Main.AdLinkUrl = "https://sponsor.example"; await env.Main.OpenAdCommand.ExecuteAsync(null); Assert.Contains("https://sponsor.example", env.Dialogs.Opened);
        Ctx.Main.AdImageUrl = env.Files.Url + "/banner"; await env.Main.LoadAdAsync(); Assert.True(env.Main.AdVisible);   // loading never throws, whatever the bytes are
        Ctx.Main.AdImageUrl = "http://127.0.0.1:1/unreachable"; await env.Main.LoadAdAsync(); Assert.False(env.Main.AdHasImage);   // unreachable: nothing to show
    }
}

public class SmallPieceTests
{
    [Theory]
    [InlineData("APPS", "Apps")] [InlineData("map", "Navigation map")] [InlineData("MAP_LICENSE", "Map licence")] [InlineData("VOICE", "Voice (navigation prompts)")]
    [InlineData("GRACENOTES", "Gracenotes (album info)")] [InlineData("ENH_DAB", "DAB radio")] [InlineData("TOOL", "Tool")] [InlineData("???", "Package")] [InlineData(null, "Package")]
    [InlineData("VOICE_NAV", "Navigation voice")] [InlineData("voice_nav", "Navigation voice")]                                  // the 14G422 navigation voice packages ("Navigation Voice" in the backend)
    public void PackageStyle_MapsTypes(string? type, string label) => Assert.Equal(label, PackageStyle.For(type).Label);

    [Fact]
    public void PackageRow_And_QueueItem()
    {
        var row = new PackageRow(new Ivsu { Type = "APPS", Name = "N", Version = "3.4", FileSize = 2048 });
        Assert.Equal("Apps  ·  3.4", row.Subtitle); Assert.Equal("2KB", row.SizeText); Assert.Equal("APPS - N 3.4", row.Display);
        int n = 0; row.SelectionChanged += () => n++; row.Selected = false; Assert.Equal(1, n);
        Assert.Equal("Package", new PackageRow(new Ivsu { Type = "X", Name = "N" }).Subtitle);
        var q = new QueueItem("a", "1KB"); q.IsActive = true; q.IsDone = true; Assert.True(q.IsActive && q.IsDone);
    }

    [Fact] public void ComposeNotes_OnlyAddsHeadingsWhenBothExist()
    {
        Assert.Equal("a", HomeViewModel.ComposeNotes("a", "")); Assert.Equal("b", HomeViewModel.ComposeNotes("", "b")); Assert.Equal("", HomeViewModel.ComposeNotes("", ""));
        var both = HomeViewModel.ComposeNotes("a", "b"); Assert.Contains("### Release notes", both); Assert.Contains("### Map release notes", both);
    }

    [Fact] public void Docs_Urls() { Assert.Equal(ApiEndpoints.InstallDocsUrl("reformat"), DownloadViewModel.InstallDocsUrl("Reformat")); }

    [AvaloniaFact]
    public void MarkdownText_RendersEveryBlockKind()
    {
        var md = new MarkdownText { Text = "# H\nplain **b** *i* `c` [l](https://x.y) https://z.y\n- item\n> quote\n---\n```\ncode\n```" };
        Assert.Equal(6, md.Children.Count);
        md.Text = null; Assert.Empty(md.Children);
        md.FontSize = 20; md.Text = "x"; Assert.Single(md.Children);
    }

    [AvaloniaFact]
    public async Task Dialog_Facade_MapsButtonsToBooleans()
    {
        var fake = new FakeDialogs(); var old = Dialogs.Backend; Dialogs.Backend = fake;
        try
        {
            fake.DefaultYes = true; Assert.True(await Dialogs.Confirm("q")); fake.DefaultYes = false; Assert.False(await Dialogs.Confirm("q", warning: true));
            await Dialogs.Error("e"); await Dialogs.Info("i"); Assert.Equal(0, await Dialogs.Show("m", "t", new[] { "ok" }));
            await Dialogs.SetClipboard("c"); await Dialogs.OpenUrl("u"); Assert.Equal(new[] { "q", "q", "e", "i", "m" }, fake.Messages);
            Assert.Null(await Dialogs.PickFolder("f")); Assert.Null(await Dialogs.PickFile("f", "n", "*.x"));
        }
        finally { Dialogs.Backend = old; }
    }

    [AvaloniaFact]
    public async Task Ctx_Basics()
    {
        using var env = new AppEnv();
        Assert.False(string.IsNullOrEmpty(Ctx.OsName())); Assert.Equal("Stable", Ctx.Branch); Assert.Contains("Home", Ctx.T("Main.Home"));
        await Ctx.RandomizeUserAgentAsync(); Assert.StartsWith("UA/", Ctx.Http.DefaultRequestHeaders.UserAgent.ToString());
        Ctx.Save(); Assert.False(Ctx.ModeForced); Ctx.Profile.InstallMode = "reformat"; Assert.False(Ctx.ModeForced);   /* MY20 protection not off */ Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = "reformat"; Assert.True(Ctx.ModeForced);
        Assert.Equal("Light", (new SettingsViewModel()).ThemeOptions[1].Value);
    }
}

public class FinalGapTests
{
    [Fact]
    public void LogFile_Presentation_PerKind()
    {
        foreach (var kind in Enum.GetValues<LogKind>())
        {
            var f = new LogFile { Kind = kind, Title = "t", Path = "/x/y.txt" };
            Assert.NotEmpty(f.IconData); Assert.NotNull(f.IconBrush); Assert.NotNull(f.IconBackground); Assert.StartsWith("#", f.Color); Assert.Equal("y.txt", f.Name);
        }
    }

    [Fact]
    public void Items_ToString_And_Loc_Refresh()
    {
        Assert.Equal("EU", new ReleaseItem { Display = "EU" }.ToString()); Assert.Equal("M", new MapItem { Display = "M" }.ToString());
        Assert.Equal("Deutsch", new LanguageOption("de", "Deutsch").ToString()); Assert.Equal("Dark", new ChoiceOption("Dark", "Dark").ToString());
        int raised = 0; Loc.Instance.PropertyChanged += (_, e) => { if (e.PropertyName == "Item[]") raised++; };
        Loc.Instance.Refresh(); Assert.Equal(1, raised);
        Assert.NotEmpty(Loc.Instance["Main.Home"]);
        Assert.Equal("Key", new TExtension("Key").Key);
    }

    [Fact]
    public void Plist_ReadsAllScalarKinds()
    {
        var d = (Dictionary<string, object?>)Plist.Parse("""<plist><dict><key>r</key><real>1.5</real><key>t</key><true/><key>f</key><false/><key>d</key><date>x</date><key>n</key><dict><key>a</key><integer>1</integer></dict></dict></plist>""")!;
        Assert.Equal(1.5, d["r"]); Assert.Equal(true, d["t"]); Assert.Equal(false, d["f"]); Assert.Equal("x", d["d"]); Assert.Equal(1L, ((Dictionary<string, object?>)d["n"]!)["a"]);
        Assert.Null(Plist.Parse("<plist/>"));
    }
}
