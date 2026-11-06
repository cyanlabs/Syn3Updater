using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

public class DriveListMergerTests
{
    private static DriveInfoModel D(string id, string name = "Stick", long free = 100, string mount = "/mnt/x") => new() { DeviceId = id, Name = name, FreeBytes = free, MountPath = mount, SizeBytes = 1000 };
    private static DriveInfoModel Folder() => new() { IsFolder = true, MountPath = "/home/me/dl", Name = "Folder" };
    private static DriveInfoModel Chooser() => new() { IsChooser = true, Name = "Select A Directory..." };
    private static string Ids(ObservableCollection<DriveInfoModel> l) => string.Join(",", l.Select(d => d.IsChooser ? "<choose>" : d.IsFolder ? "<folder>" : d.DeviceId));

    [Fact]
    public void AddsANewDrive_BeforeTheFolderAndChooserEntries_WithoutAReset()
    {
        var list = new ObservableCollection<DriveInfoModel> { D("a"), Folder(), Chooser() };
        var actions = new List<NotifyCollectionChangedAction>(); list.CollectionChanged += (_, e) => actions.Add(e.Action);
        var r = DriveListMerger.Merge(list, new[] { D("a"), D("b") });
        Assert.Equal("a,b,<folder>,<choose>", Ids(list)); Assert.Single(r.Added); Assert.Equal(new[] { NotifyCollectionChangedAction.Add }, actions);
    }

    [Fact]
    public void RemovesAPulledDrive_AndReportsIt()
    {
        var a = D("a"); var b = D("b"); var list = new ObservableCollection<DriveInfoModel> { a, b, Chooser() };
        var r = DriveListMerger.Merge(list, new[] { D("a") });
        Assert.Equal("a,<choose>", Ids(list)); Assert.Equal(new[] { b }, r.Removed); Assert.Null(r.Current(b)); Assert.Same(a, r.Current(a));
    }

    [Fact]
    public void UnchangedDrives_KeepTheirObjects_AndRaiseNoEvents()
    {
        var a = D("a"); var list = new ObservableCollection<DriveInfoModel> { a, Chooser() };
        var raised = 0; list.CollectionChanged += (_, _) => raised++;
        var r = DriveListMerger.Merge(list, new[] { D("a") });                        // a different object with identical details
        Assert.Equal(0, raised); Assert.False(r.Changed); Assert.Same(a, list[0]);
    }

    [Fact]
    public void ChangedDetails_ReplaceTheDrive_AndTheOldOneMapsToTheNew()
    {
        var a = D("a", free: 100); var list = new ObservableCollection<DriveInfoModel> { a, Chooser() };
        var fresh = D("a", free: 40);
        var r = DriveListMerger.Merge(list, new[] { fresh });
        Assert.Same(fresh, list[0]); Assert.Same(fresh, r.Current(a)); Assert.Single(r.Replaced);
    }

    [Fact]
    public void FollowsTheNewOrder_AndNeverTouchesThePinnedEntries()
    {
        var folder = Folder(); var chooser = Chooser(); var list = new ObservableCollection<DriveInfoModel> { D("a"), D("b"), D("c"), folder, chooser };
        DriveListMerger.Merge(list, new[] { D("c"), D("a") });
        Assert.Equal("c,a,<folder>,<choose>", Ids(list)); Assert.Same(folder, list[2]); Assert.Same(chooser, list[3]);
    }

    [Fact]
    public void AnEmptyResult_LeavesOnlyThePinnedEntries()
    {
        var list = new ObservableCollection<DriveInfoModel> { D("a"), Folder(), Chooser() };
        DriveListMerger.Merge(list, new DriveInfoModel[0]); Assert.Equal("<folder>,<choose>", Ids(list));
    }

    [Fact]
    public void DuplicateDeviceIds_AreListedOnce()
    {
        var list = new ObservableCollection<DriveInfoModel> { Chooser() };
        DriveListMerger.Merge(list, new[] { D("a"), D("a", "Other"), D("b") }); Assert.Equal("a,b,<choose>", Ids(list));
    }

    [Fact]
    public void ManyChangesAtOnce_EndInTheRightState()
    {
        var list = new ObservableCollection<DriveInfoModel> { D("a"), D("b"), D("c"), D("d"), Chooser() };
        DriveListMerger.Merge(list, new[] { D("d"), D("e"), D("b", free: 1), D("f") });
        Assert.Equal("d,e,b,f,<choose>", Ids(list)); Assert.Equal(1, list.Single(d => d.DeviceId == "b").FreeBytes);
    }
}

public class DriveChangeDetectorTests
{
    private sealed class Source { public string? Value = "s1"; public bool Throw; public string? Read() => Throw ? throw new IOException("busy") : Value; }

    [Fact]
    public void TheFirstPoll_OnlyLearnsTheStartingState() { var s = new Source(); var d = new DriveChangeDetector(s.Read); Assert.False(d.Poll()); Assert.False(d.Poll()); }

    [Fact]
    public void AChange_CountsOnlyOnceItHasSettled()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "s2"; Assert.False(d.Poll());                // seen once: might still be changing
        Assert.True(d.Poll());                                   // same again: settled
        Assert.False(d.Poll());                                  // and reported only once
    }

    [Fact]
    public void ABurstOfChanges_GivesOneRefresh_AfterTheLastChange()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "detected"; Assert.False(d.Poll()); s.Value = "partition"; Assert.False(d.Poll()); s.Value = "mounted"; Assert.False(d.Poll());
        Assert.True(d.Poll()); Assert.False(d.Poll());
    }

    [Fact]
    public void AChangeThatGoesBack_IsIgnored()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "blip"; Assert.False(d.Poll()); s.Value = "s1"; Assert.False(d.Poll()); Assert.False(d.Poll());
    }

    [Fact]
    public void Prime_AcceptsTheCurrentStateAsKnown()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "s2"; d.Poll(); d.Prime();                     // the user pressed Refresh in between
        Assert.False(d.Poll());
    }

    [Fact]
    public void AFailingLook_IsNoNews_NotACrash()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Throw = true; Assert.False(d.Poll()); Assert.False(d.Poll());
        s.Throw = false; s.Value = "s2"; Assert.False(d.Poll()); Assert.True(d.Poll());
    }

    [Fact]
    public void Retry_ReportsTheSameChangeAgain()
    {
        var s = new Source(); var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "s2"; d.Poll(); Assert.True(d.Poll());
        d.Retry();                                              // the listing failed
        Assert.False(d.Poll()); Assert.True(d.Poll());          // seen again, settles again
    }

    [Fact]
    public void ARemoval_IsDetectedLikeAnInsertion()
    {
        var s = new Source { Value = "a\nb" }; var d = new DriveChangeDetector(s.Read); d.Prime();
        s.Value = "a"; Assert.False(d.Poll()); Assert.True(d.Poll());
    }
}

public class DriveFingerprintTests
{
    [Fact]
    public void MountInfo_KeepsOnlyWherePeoplePlugDrivesIn_AndDecodesEscapes()
    {
        var lines = new[]
        {
            "36 35 98:0 / /run/media/scott/CYANLABS rw,nosuid shared:1 - exfat /dev/sdb1 rw",
            "37 35 0:5 / /proc rw - proc proc rw",
            "38 35 8:1 / /media/usb\\040stick rw - vfat /dev/sdc1 rw",
            "39 35 8:2 / /mnt/data rw - ext4 /dev/sdd1 rw",
            "40 35 8:3 / /home rw - ext4 /dev/nvme0n1p2 rw",
            "bad line",
        };
        Assert.Equal(new[] { "m:/run/media/scott/CYANLABS", "m:/media/usb stick", "m:/mnt/data" }, DriveFingerprint.ParseMountInfo(lines).ToArray());
    }

    [Fact]
    public void TheRealFingerprint_CanBeReadOnThisMachine_AndIsStableBetweenReads()
    {
        Assert.NotNull(DriveFingerprint.Current());
        Assert.Equal(DriveFingerprint.Current(), DriveFingerprint.Current());
    }
}

/// <summary>A machine whose drives the test can plug in and pull out.</summary>
public sealed class MutableDrives : IDriveService
{
    public List<DriveInfoModel> Present { get; } = new();
    public bool FailListing { get; set; }
    public int Listings { get; private set; }
    public string Fingerprint => string.Join(",", Present.Select(d => d.DeviceId + d.FreeBytes));
    public string OsDescription => "test";
    public Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default)
    {
        Listings++;
        if (FailListing) throw new IOException("lsblk is busy");
        return Task.FromResult<IReadOnlyList<DriveInfoModel>>(Present.Select(d => new DriveInfoModel
        { DeviceId = d.DeviceId, Name = d.Name, MountPath = d.MountPath, SizeBytes = d.SizeBytes, FreeBytes = d.FreeBytes, FileSystem = d.FileSystem }).ToList());
    }
    public Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.FromResult(drive.MountPath);
    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;
    public Task<Cyanlabs.Syn3Updater.Core.Drives.EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default) => Task.FromResult(new Cyanlabs.Syn3Updater.Core.Drives.EjectResult(true, "ok"));
}

public class DriveAutoRefreshAppTests
{
    private static DriveInfoModel Stick(string id, long free = 5000) => new() { DeviceId = id, Name = "Stick " + id, MountPath = "/run/media/x/" + id, SizeBytes = 32_000, FreeBytes = free, FileSystem = "exFAT" };

    private static (AppEnv env, MutableDrives drives) Setup(params DriveInfoModel[] initial)
    {
        var drives = new MutableDrives(); drives.Present.AddRange(initial);
        var env = new AppEnv(drives: drives, fingerprint: () => drives.Fingerprint);
        return (env, drives);
    }

    /// <summary>Two polls: the change has to be seen twice in a row before it counts.</summary>
    private static async Task Settle() { await Ctx.DriveMonitor.PollAsync(); await Ctx.DriveMonitor.PollAsync(); }

    private static IEnumerable<string> Real(System.Collections.ObjectModel.ObservableCollection<DriveInfoModel> l) => l.Where(d => !d.IsChooser && !d.IsFolder).Select(d => d.DeviceId);

    [AvaloniaFact]
    public async Task APluggedInStick_AppearsInBothLists_WithoutPressingRefresh()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env;
        await Ctx.DriveMonitor.PollAsync();                                    // learns the starting state
        await env.Main.Home.RefreshDrivesAsync(); await env.Main.Utility.RefreshCommand.ExecuteAsync(null);
        drives.Present.Add(Stick("b"));
        await Settle();
        Assert.Equal(new[] { "a", "b" }, Real(env.Main.Home.Drives)); Assert.Equal(new[] { "a", "b" }, Real(env.Main.Utility.Drives));
        Assert.True(env.Main.Home.Drives[^1].IsChooser);                       // "Select A Directory..." stays last
    }

    [AvaloniaFact]
    public async Task NothingChanged_MeansNoListingAtAll()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env;
        await Settle(); await Settle(); await Settle();
        Assert.Equal(0, drives.Listings);                                       // the cheap fingerprint is all that ran
    }

    [AvaloniaFact]
    public async Task AChange_IsListedOnce_NotOnEveryPoll()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env;
        await Ctx.DriveMonitor.PollAsync(); drives.Present.Add(Stick("b"));
        await Settle(); await Settle(); await Settle();
        Assert.Equal(1, drives.Listings);
    }

    [AvaloniaFact]
    public async Task TheSelectedStick_StaysSelected_WhenAnotherIsPlugged_AndRegionAndVersionAreKept()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env; var home = env.Main.Home;
        await Ctx.DriveMonitor.PollAsync(); await home.RefreshDrivesAsync(); home.SelectedDrive = home.Drives.First(d => d.DeviceId == "a");
        home.SelectedRegion = home.Regions.First();
        drives.Present.Add(Stick("b")); await Settle();
        Assert.Equal("a", home.SelectedDrive?.DeviceId); Assert.NotNull(home.SelectedRegion);
    }

    [AvaloniaFact]
    public async Task PullingTheSelectedStick_ClearsTheSelection_AndSaysSo()
    {
        var (env, drives) = Setup(Stick("a"), Stick("b")); using var _ = env; var home = env.Main.Home;
        await Ctx.DriveMonitor.PollAsync(); await home.RefreshDrivesAsync(); home.SelectedDrive = home.Drives.First(d => d.DeviceId == "a");
        drives.Present.RemoveAll(d => d.DeviceId == "a"); await Settle();
        Assert.Null(home.SelectedDrive); Assert.Equal(new[] { "b" }, Real(home.Drives)); Assert.Equal(Ctx.T("App.DriveRemoved"), home.DriveInfo);
        drives.Present.Clear(); await Settle(); Assert.True(home.NoDrives);
    }

    [AvaloniaFact]
    public async Task PullingAnotherStick_DoesNotDisturbTheSelection()
    {
        var (env, drives) = Setup(Stick("a"), Stick("b")); using var _ = env; var home = env.Main.Home;
        await Ctx.DriveMonitor.PollAsync(); await home.RefreshDrivesAsync(); home.SelectedDrive = home.Drives.First(d => d.DeviceId == "a");
        drives.Present.RemoveAll(d => d.DeviceId == "b"); await Settle();
        Assert.Equal("a", home.SelectedDrive?.DeviceId);
    }

    [AvaloniaFact]
    public async Task TheChosenFolder_SurvivesAnAutoRefresh_AndStaysSelected()
    {
        var (env, drives) = Setup(); using var _ = env; var home = env.Main.Home;
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
        var folder = home.SelectedDrive; Assert.True(folder!.IsFolder);
        await Ctx.DriveMonitor.PollAsync(); drives.Present.Add(Stick("a")); await Settle();
        Assert.Same(folder, home.SelectedDrive); Assert.Equal(new[] { "a" }, Real(home.Drives)); Assert.False(home.NoDrives); Assert.True(home.Drives[^1].IsChooser);
    }

    [AvaloniaFact]
    public async Task ChangedDetails_OfTheSelectedStick_FollowTheSelectionToTheNewEntry()
    {
        var (env, drives) = Setup(Stick("a", free: 5000)); using var _ = env; var home = env.Main.Home;
        await Ctx.DriveMonitor.PollAsync(); await home.RefreshDrivesAsync(); home.SelectedDrive = home.Drives.First(d => d.DeviceId == "a");
        drives.Present[0].FreeBytes = 100; await Settle();                      // someone copied files onto it
        Assert.Equal("a", home.SelectedDrive?.DeviceId); Assert.Equal(100, home.SelectedDrive!.FreeBytes);
    }

    [AvaloniaFact]
    public async Task WhileARunIsActive_NothingIsPolled_AndTheChangeIsPickedUpAfterwards()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env;
        await Ctx.DriveMonitor.PollAsync(); Workflow.Busy = true;
        drives.Present.Add(Stick("b")); await Settle(); await Settle();
        Assert.Equal(0, drives.Listings);
        Workflow.Busy = false; await Settle();
        Assert.Equal(1, drives.Listings); Assert.Contains("b", Real(env.Main.Home.Drives));
    }

    [AvaloniaFact]
    public async Task AFailingListing_IsSilent_AndRetriedUntilItWorks()
    {
        var (env, drives) = Setup(Stick("a")); using var _ = env;
        await Ctx.DriveMonitor.PollAsync(); drives.Present.Add(Stick("b")); drives.FailListing = true;
        await Settle();
        Assert.Empty(env.Dialogs.Messages); Assert.DoesNotContain("b", Real(env.Main.Home.Drives));      // no error box, and nothing wrongly listed
        drives.FailListing = false; await Settle();
        Assert.Contains("b", Real(env.Main.Home.Drives)); Assert.Empty(env.Dialogs.Messages);
    }

    [AvaloniaFact]
    public void StartingAndStoppingTheTimer_IsHarmless()
    {
        using var env = new AppEnv();
        Ctx.DriveMonitor.Start(TimeSpan.FromMilliseconds(50)); Ctx.DriveMonitor.Start(TimeSpan.FromMilliseconds(50)); Ctx.DriveMonitor.Stop(); Ctx.DriveMonitor.Stop();
    }
}
