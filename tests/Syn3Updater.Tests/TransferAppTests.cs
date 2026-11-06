using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class FreeSpaceWorkflowTests
{
    private static Ivsu Pkg(AppEnv env, string name, long? size = null) => new()
    {
        Type = "APPS", Name = name, FileName = name + ".tar.gz", Url = env.Files.Url + "/" + name + ".tar.gz", Md5 = FileServer.Md5(name + ".tar.gz"),
        FileSize = size ?? FileServer.Blob(name + ".tar.gz").Length,
    };

    [AvaloniaFact]
    public async Task DriveTooSmall_StopsBeforeAnythingIsErased_OrDownloaded()
    {
        var drives = new RemountingDrives(Path.Combine(Path.GetTempPath(), "never-used"));
        using var env = new AppEnv(drives: drives);
        var tiny = new DriveInfoModel { DeviceId = "/dev/sdx", MountPath = "/m", Name = "Tiny", SizeBytes = 8L << 20, FileSystem = "exFAT", PartitionType = "MBR" };   // 8 MB stick
        env.Dialogs.Say(m => m.Contains("/dev/sdx"), true);                                    // the user would have confirmed the format
        var ok = await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = tiny, Packages = { Pkg(env, "A", 40L << 20) } });
        Assert.False(ok);
        Assert.Contains(env.Dialogs.Messages, m => m.Contains("too small") && m.Contains("Needed:") && m.Contains("Available:"));
        Assert.Equal(0, drives.Formats);                                                       // nothing was formatted
        Assert.False(env.Main.Downloads.IsRunning); Assert.False(Directory.Exists(env.DownloadDir) && Directory.GetFiles(env.DownloadDir).Length > 0);
    }

    [AvaloniaFact]
    public async Task DownloadFolderTooSmall_IsReported_WithWhatToDo()
    {
        using var env = new AppEnv();
        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        var ok = await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = usb, DownloadOnly = true, Packages = { Pkg(env, "HUGE", 900L << 40) } });     // 900 TB
        Assert.False(ok);
        var msg = Assert.Single(env.Dialogs.Messages, m => m.Contains("download folder"));
        Assert.Contains("Needed:", msg); Assert.Contains("Free:", msg); Assert.Contains(env.DownloadDir, msg); Assert.Contains("Settings", msg);
        Assert.DoesNotContain(env.Dialogs.Messages, m => m.Contains("too small"));              // download-only: the drive is not part of the check
    }

    [AvaloniaFact]
    public async Task BothShortfalls_AreShownTogether_AndLogged()
    {
        using var env = new AppEnv();
        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        Assert.False(await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = usb, Packages = { Pkg(env, "HUGE", 900L << 40) } }));
        var msg = Assert.Single(env.Dialogs.Messages, m => m.Contains("download folder") && m.Contains("too small"));
        Assert.Contains("Not enough free space", string.Join("\n", Ctx.Log.Lines));
    }

    [AvaloniaFact]
    public async Task EnoughSpace_LetsTheRunStart()
    {
        using var env = new AppEnv();
        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        Assert.True(await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = usb, SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false, Mode = InstallModes.Autoinstall }));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
    }

    [AvaloniaFact]
    public async Task ReusedStick_DoesNotCountFilesItAlreadyHolds()
    {
        using var env = new AppEnv();
        var sync = Path.Combine(env.UsbDir, "SyncMyRide"); Directory.CreateDirectory(sync);
        var have = Pkg(env, "HAVE", 500L << 20); File.WriteAllBytes(Path.Combine(sync, have.FileName), new byte[1]);              // wrong size => counts
        Assert.True(SpaceCheck.TargetNeeded(new[] { have }, sync) > 500L << 20);
        File.Delete(Path.Combine(sync, have.FileName)); using (var f = File.Create(Path.Combine(sync, have.FileName))) f.SetLength(have.FileSize);   // sparse file of the right size
        Assert.Equal(0, SpaceCheck.TargetNeeded(new[] { have }, sync));
        await Task.CompletedTask;
    }
}

public class SpeedAndEtaTests
{
    private static long S(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);
    private static RunProgress P(string phase, long moved, long remaining, int connections) => new("", 0, 0, "f", phase) { TransferredBytes = moved, RemainingBytes = remaining, Connections = connections };

    [AvaloniaFact]
    public void Download_ShowsOverallSpeed_Eta_AndConnections()
    {
        using var env = new AppEnv(); var dl = env.Main.Downloads;
        const long Mb = 1024 * 1024;
        for (int t = 0; t <= 5; t++) dl.ApplyTransfer(P("download", t * 5 * Mb, 100 * Mb - t * 5 * Mb, 8), S(t));               // 5 MB/s in total, whatever the connection count
        Assert.Equal("5MB/s", dl.SpeedText);                                      // the real overall rate: not 8x and not 1/8
        Assert.Equal("8 connections", dl.ConnectionsText);
        Assert.Equal("15 s left", dl.EtaText); Assert.True(dl.HasStats);          // 75 MB still to go at 5 MB/s
    }

    [AvaloniaFact]
    public void SingleConnection_AndCopyPhase_Wording_AndPhaseResetsTheMeter()
    {
        using var env = new AppEnv(); var dl = env.Main.Downloads;
        const long Mb = 1024 * 1024;
        for (int t = 0; t <= 3; t++) dl.ApplyTransfer(P("download", t * Mb, 9 * Mb, 1), S(t));
        Assert.Equal("1 connection", dl.ConnectionsText); Assert.Equal("1MB/s", dl.SpeedText);
        dl.ApplyTransfer(P("copy", 0, 50_000_000, 1), S(4));                      // new phase: counter restarts, so the old speed is dropped
        Assert.Equal("", dl.SpeedText); Assert.Equal("", dl.EtaText);
        dl.ApplyTransfer(P("copy", 20 * Mb, 30 * Mb, 1), S(5));
        Assert.Equal("20MB/s", dl.SpeedText); Assert.Equal("2 s left", dl.EtaText);     // 30 MB at 20 MB/s = 1.5 s, rounded up
    }

    [AvaloniaFact]
    public void StatsFadeWhenNothingMoves_ForExampleWhileVerifying()
    {
        using var env = new AppEnv(); var dl = env.Main.Downloads;
        for (int t = 0; t <= 3; t++) dl.ApplyTransfer(P("download", t * 2_000_000L, 10_000_000, 4), S(t));
        Assert.NotEqual("", dl.SpeedText);
        dl.RefreshStats(S(10));                                                   // the timer tick, seven idle seconds later
        Assert.Equal("", dl.SpeedText); Assert.Equal("", dl.EtaText); Assert.Equal("4 connections", dl.ConnectionsText);
    }

    [AvaloniaFact]
    public async Task EndToEndRun_PublishesConnectionsAndClearsStatsAtTheEnd()
    {
        using var env = new AppEnv(); Ctx.Main.DownloadConnections = 3;
        var seen = new List<string>(); var dl = env.Main.Downloads;
        dl.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(DownloadViewModel.ConnectionsText) && dl.ConnectionsText.Length > 0) seen.Add(dl.ConnectionsText); };
        var usb = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir, Name = "f" };
        Assert.True(await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = usb, SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false, Mode = InstallModes.Autoinstall }));
        await AppEnv.Until(() => !dl.IsRunning && dl.Status == Ctx.T("String.Completed"));
        Assert.Contains("3 connections", seen);
        Assert.False(dl.HasStats); Assert.Equal("", dl.SpeedText);
    }
}

public class EjectUiTests
{
    private static JobSpec StickJob(AppEnv env, DriveInfoModel stick) => new()
    {
        Mode = InstallModes.Autoinstall, Release = "r", Region = "NA", Drive = stick, Action = UpdateAction.Main,
        Packages = { new Ivsu { Type = "APPS", Name = "A", FileName = "A.tar.gz", Url = env.Files.Url + "/A.tar.gz", Md5 = FileServer.Md5("A.tar.gz"), FileSize = FileServer.Blob("A.tar.gz").Length } },
    };

    [AvaloniaFact]
    public async Task FinishedRun_OffersEject_AndChoosingItEjectsTheRemountedPath()
    {
        var root = Directory.CreateTempSubdirectory("syn3eject").FullName; var after = Path.Combine(root, "CYANLABS");
        var drives = new RemountingDrives(after); using var env = new AppEnv(drives: drives);
        var stick = new DriveInfoModel { DeviceId = "/dev/sdx", MountPath = Path.Combine(root, "OLD"), Name = "Stick", SizeBytes = 32L << 30 };
        env.Dialogs.Say(m => m.Contains("/dev/sdx"), true);                                         // confirm the format
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.Completed"), true);                             // choose the second button on the completion dialog: Eject drive
        Assert.True(await Workflow.StartAsync(StickJob(env, stick)));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && drives.Ejected.Count == 1);

        Assert.Equal(("/dev/sdx", after), drives.Ejected[0]);                                       // uses the path AFTER the format, not the stale one
        Assert.Contains(env.Dialogs.Shown, s => s.Buttons.SequenceEqual(new[] { "OK", "Eject drive" }));
        Assert.Contains(env.Dialogs.Messages, m => m.Contains("remove it now"));
        Assert.False(env.Main.Downloads.CanEject);                                                  // done: the button goes away
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task DownloadsPageButton_RemainsAvailable_WhenTheDialogWasDismissed_AndFailuresAreShown()
    {
        var root = Directory.CreateTempSubdirectory("syn3eject2").FullName;
        var drives = new RemountingDrives(Path.Combine(root, "CYANLABS")) { EjectAnswer = new EjectResult(false, "Could not unmount /dev/sdx1: target is busy") };
        using var env = new AppEnv(drives: drives);
        env.Dialogs.Say(m => m.Contains("/dev/sdx"), true);                                         // the completion dialog's default answer (OK) is kept
        Assert.True(await Workflow.StartAsync(StickJob(env, new DriveInfoModel { DeviceId = "/dev/sdx", MountPath = "/old", Name = "S", SizeBytes = 32L << 30 })));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.CanEject);
        Assert.Empty(drives.Ejected); Assert.True(env.Main.Downloads.CanPressEject);

        await env.Main.Downloads.EjectCommand.ExecuteAsync(null);
        Assert.Single(drives.Ejected); Assert.Contains(env.Dialogs.Messages, m => m.Contains("target is busy"));
        Assert.True(env.Main.Downloads.CanEject);                                                   // still there so the user can retry
        drives.EjectAnswer = new EjectResult(true, "The drive was ejected. You can remove it now.");
        await env.Main.Downloads.EjectCommand.ExecuteAsync(null); Assert.False(env.Main.Downloads.CanEject);
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task FolderTargets_HaveNothingToEject()
    {
        using var env = new AppEnv();
        Assert.True(await Workflow.StartAsync(new JobSpec { Release = "r", Region = "NA", Drive = new DriveInfoModel { IsFolder = true, MountPath = env.UsbDir }, SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false, Mode = InstallModes.Autoinstall }));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        Assert.False(env.Main.Downloads.CanEject);
        Assert.DoesNotContain(env.Dialogs.Shown, s => s.Buttons.Contains("Eject drive"));
        Assert.False(await EjectFlow.RunAsync(new DriveInfoModel { IsFolder = true }, "/x")); Assert.False(await EjectFlow.RunAsync(new DriveInfoModel { IsChooser = true }, "/x"));
    }

    [AvaloniaFact]
    public async Task HomeAndUtilityButtons_EjectTheSelectedDrive_AndRefresh()
    {
        var drives = new RemountingDrives("/unused"); using var env = new AppEnv(drives: drives);
        var stick = new DriveInfoModel { DeviceId = "/dev/sdx", MountPath = "/run/media/scott/CYANLABS", Name = "S" };
        var home = env.Main.Home; Assert.False(home.CanEjectSelected);
        home.Drives.Insert(0, stick); home.SelectedDrive = stick; Assert.True(home.CanEjectSelected);
        await home.EjectSelectedCommand.ExecuteAsync(null);
        Assert.Equal(("/dev/sdx", "/run/media/scott/CYANLABS"), drives.Ejected[0]); Assert.Null(home.SelectedDrive); Assert.False(home.CanEjectSelected);

        var util = env.Main.Utility; util.SelectedDrive = stick; Assert.True(util.CanEjectSelected);
        await util.EjectSelectedCommand.ExecuteAsync(null);
        Assert.Equal(2, drives.Ejected.Count); Assert.Null(util.SelectedDrive);

        drives.EjectAnswer = new EjectResult(false, "busy"); home.Drives.Insert(0, stick); home.SelectedDrive = stick;
        await home.EjectSelectedCommand.ExecuteAsync(null);
        Assert.Same(stick, home.SelectedDrive); Assert.Contains(env.Dialogs.Messages, m => m == "busy");   // failure: selection kept so the user can retry
    }
}

public class DownloadChipLayoutTests
{
    private static (Avalonia.Controls.Border speed, Avalonia.Controls.Border eta, Avalonia.Controls.Border conns) Chips(Avalonia.Controls.Window w)
    {
        var v = (Cyanlabs.Syn3Updater.App.Views.DownloadView)w.Content!;
        Avalonia.Controls.Border Find(string name) => Avalonia.Controls.ControlExtensions.FindControl<Avalonia.Controls.Border>(v, name)!;
        return (Find("SpeedChip"), Find("EtaChip"), Find("ConnectionsChip"));
    }

    [AvaloniaFact]
    public void TheChips_StayPut_WhateverTheNumbersSay()
    {
        using var env = new AppEnv(); var dl = env.Main.Downloads;
        var window = new Avalonia.Controls.Window { Width = 900, Height = 700, Content = new Cyanlabs.Syn3Updater.App.Views.DownloadView { DataContext = dl } }; window.Show();
        dl.IsRunning = true; dl.ConnectionsText = "1 connection"; dl.SpeedText = "1MB/s"; dl.EtaText = "5 s left";
        window.UpdateLayout(); var (s, e, c) = Chips(window);
        var before = (sx: s.Bounds.X, sw: s.Bounds.Width, ex: e.Bounds.X, ew: e.Bounds.Width, cx: c.Bounds.X, cw: c.Bounds.Width);

        dl.SpeedText = "999.9MB/s"; dl.EtaText = "59 min 59 s left"; dl.ConnectionsText = "8 connections"; window.UpdateLayout();
        Assert.Equal(before.sx, s.Bounds.X); Assert.Equal(before.ex, e.Bounds.X); Assert.Equal(before.cx, c.Bounds.X);          // nothing slides sideways
        Assert.Equal(before.sw, s.Bounds.Width); Assert.Equal(before.ew, e.Bounds.Width); Assert.Equal(before.cw, c.Bounds.Width);

        dl.SpeedText = ""; dl.EtaText = ""; window.UpdateLayout();                                                               // e.g. while verifying: values go, the chips stay
        Assert.Equal("–", dl.SpeedDisplay); Assert.Equal("–", dl.EtaDisplay);
        Assert.Equal(before.ex, e.Bounds.X); Assert.Equal(before.cx, c.Bounds.X); Assert.Equal(before.ew, e.Bounds.Width);
        Assert.True(c.IsVisible && s.IsVisible && e.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Connections_SitOnTheRightEdge_AndLeaveTheRowWhenNothingRuns()
    {
        using var env = new AppEnv(); var dl = env.Main.Downloads;
        var window = new Avalonia.Controls.Window { Width = 900, Height = 700, Content = new Cyanlabs.Syn3Updater.App.Views.DownloadView { DataContext = dl } }; window.Show();
        dl.IsRunning = true; dl.ConnectionsText = "3 connections"; window.UpdateLayout(); var (s, e, c) = Chips(window);
        Assert.True(c.Bounds.X > e.Bounds.X + e.Bounds.Width + 50);                       // far to the right of the other two, not next to them
        var row = (Avalonia.Controls.Grid)c.Parent!; Assert.Equal(row.Bounds.Width, c.Bounds.X + c.Bounds.Width, 1);   // flush with the row's right edge
        dl.IsRunning = false; window.UpdateLayout(); Assert.False(row.IsEffectivelyVisible);
        window.Close();
    }
}
