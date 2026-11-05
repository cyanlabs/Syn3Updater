using System.Net;
using System.Text;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class ThroughputTests
{
    private static long S(double seconds) => (long)(seconds * TimeSpan.TicksPerSecond);

    [Fact]
    public void SteadyTransfer_ReadsTheRealRate_AndTheEta()
    {
        var m = new ThroughputMeter();
        for (int t = 0; t <= 5; t++) m.Add(t * 2_000_000L, S(t));                 // 2 MB/s
        Assert.InRange(m.BytesPerSecond(S(5)), 1_990_000, 2_010_000);
        Assert.InRange(m.Eta(20_000_000, S(5))!.Value.TotalSeconds, 9.9, 10.1);
        Assert.Equal("1.9MB/s", TransferText.Speed(m.BytesPerSecond(S(5))));
    }

    [Fact]
    public void NoSamples_OrOneSample_MeansUnknown()
    {
        var m = new ThroughputMeter();
        Assert.Equal(0, m.BytesPerSecond(0)); Assert.Null(m.Eta(100, 0));
        m.Add(100, S(1)); Assert.Equal(0, m.BytesPerSecond(S(1))); Assert.Null(m.Eta(100, S(1)));
    }

    [Fact]
    public void Window_ForgetsOldSpeed_SoItFollowsChanges()
    {
        var m = new ThroughputMeter(window: TimeSpan.FromSeconds(4));
        long bytes = 0;
        for (int t = 0; t <= 10; t++) { bytes += 1_000_000; m.Add(bytes, S(t)); }                 // 1 MB/s for 10 s
        for (int t = 11; t <= 20; t++) { bytes += 5_000_000; m.Add(bytes, S(t)); }                // then 5 MB/s
        Assert.InRange(m.BytesPerSecond(S(20)), 4_900_000, 5_100_000);
    }

    [Fact]
    public void WhenNothingMoves_SpeedFallsToZero_ForExampleWhileVerifying()
    {
        var m = new ThroughputMeter(idle: TimeSpan.FromSeconds(3));
        m.Add(0, S(0)); m.Add(1_000_000, S(1)); m.Add(2_000_000, S(2));
        Assert.True(m.BytesPerSecond(S(2)) > 0);
        m.Add(2_000_000, S(4)); m.Add(2_000_000, S(6));                                          // stalled: checksum running
        Assert.Equal(0, m.BytesPerSecond(S(6))); Assert.Null(m.Eta(1_000, S(6)));
    }

    [Fact]
    public void CounterRestart_StartsAFreshMeasurement()
    {
        var m = new ThroughputMeter();
        m.Add(0, S(0)); m.Add(10_000_000, S(1)); m.Add(20_000_000, S(2));
        m.Add(0, S(3)); m.Add(1_000_000, S(4));                                                  // copy phase started counting from zero
        Assert.InRange(m.BytesPerSecond(S(4)), 900_000, 1_100_000);
    }

    [Fact] public void Reset_ForgetsEverything() { var m = new ThroughputMeter(); m.Add(0, 0); m.Add(1000, S(1)); m.Reset(); Assert.Equal(0, m.BytesPerSecond(S(1))); }

    [Theory]
    [InlineData(0, "")] [InlineData(0.4, "1 s")] [InlineData(45, "45 s")] [InlineData(59.2, "1 min 0 s")] [InlineData(60, "1 min 0 s")] [InlineData(130, "2 min 10 s")] [InlineData(3600, "1 h 0 min")] [InlineData(3900, "1 h 5 min")]
    public void Eta_Text(double seconds, string expected) => Assert.Equal(expected, TransferText.Eta(seconds == 0 ? null : TimeSpan.FromSeconds(seconds)));

    [Fact] public void Speed_Text() { Assert.Equal("", TransferText.Speed(0)); Assert.Equal("500B/s", TransferText.Speed(500)); Assert.Equal("12.3MB/s", TransferText.Speed(12.3 * 1024 * 1024)); }
}

public class SpaceCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3space").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private static Ivsu I(string f, long size, string? src = null) => new() { FileName = f, FileSize = size, Source = src };
    private static readonly long Mb = 1024 * 1024;

    [Fact]
    public void Margin_IsOnePercentPlus32MB_AndZeroForNothing()
    {
        Assert.Equal(0, SpaceCheck.WithMargin(0)); Assert.Equal(1000 + 10 + 32 * Mb, SpaceCheck.WithMargin(1000));
    }

    [Fact]
    public void DownloadNeed_SkipsFilesAlreadyThere_AndDoublesNaviextrasBundles()
    {
        File.WriteAllBytes(Path.Combine(_dir, "have.bin"), new byte[500]);
        File.WriteAllBytes(Path.Combine(_dir, "wrongsize.bin"), new byte[10]);
        var need = SpaceCheck.DownloadNeeded(new[] { I("have.bin", 500), I("wrongsize.bin", 700), I("new.bin", 300), I("maps.tar.gz", 1000, "naviextras") }, _dir);
        Assert.Equal(SpaceCheck.WithMargin(700 + 300 + 1000 + 1000), need);     // have.bin free; bundle counted twice (download + unpack)
    }

    [Fact]
    public void TargetNeed_SkipsFilesAlreadyOnTheStick_ButCountsNaviextrasContents()
    {
        var sync = Path.Combine(_dir, "SyncMyRide"); Directory.CreateDirectory(sync);
        File.WriteAllBytes(Path.Combine(sync, "on-stick.bin"), new byte[400]);
        var queue = new[] { I("on-stick.bin", 400), I("other.bin", 100), I("maps.tar.gz", 900, "naviextras") };
        Assert.Equal(SpaceCheck.WithMargin(100 + 900), SpaceCheck.TargetNeeded(queue, sync));
        Assert.Equal(SpaceCheck.WithMargin(400 + 100 + 900), SpaceCheck.TargetNeeded(queue, null));       // a formatted stick starts empty
    }

    [Fact]
    public void Verdict_FlagsEachShortfall_AndUnknownSpaceIsNotAFailure()
    {
        var ok = SpaceCheck.Evaluate(100, 200, 100, 200); Assert.True(ok.Ok); Assert.Equal(0, ok.DownloadShortfall);
        var bad = SpaceCheck.Evaluate(500, 200, 900, 100);
        Assert.False(bad.DownloadOk); Assert.False(bad.TargetOk); Assert.False(bad.Ok); Assert.Equal(300, bad.DownloadShortfall); Assert.Equal(800, bad.TargetShortfall);
        Assert.True(SpaceCheck.Evaluate(500, -1, 900, null).Ok);                                        // could not be measured: do not block
        Assert.True(SpaceCheck.Evaluate(100, 100, 100, 100).Ok);                                        // exactly enough
    }

    [Fact]
    public void FreeBytes_WorksForNewFolders_AndFolderSizeCounts()
    {
        Assert.True(SpaceCheck.FreeBytes(_dir) > 0);
        Assert.True(SpaceCheck.FreeBytes(Path.Combine(_dir, "not", "created", "yet")) > 0);                // falls back to the nearest existing parent
        File.WriteAllBytes(Path.Combine(_dir, "a"), new byte[300]); Directory.CreateDirectory(Path.Combine(_dir, "sub")); File.WriteAllBytes(Path.Combine(_dir, "sub", "b"), new byte[200]);
        Assert.Equal(500, SpaceCheck.FolderSize(_dir)); Assert.Equal(0, SpaceCheck.FolderSize(Path.Combine(_dir, "nope")));
    }
}

public class ByteReportingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3bytes").FullName;
    private FileServer? _server;
    public void Dispose() { _server?.Dispose(); Directory.Delete(_dir, true); }

    private sealed class Collect : IProgress<long> { public List<long> Values { get; } = new(); public void Report(long v) { lock (Values) Values.Add(v); } }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(8)]
    public async Task Download_ReportsTheTotalAcrossAllConnections_NotPerConnection(int connections)
    {
        _server = new FileServer(); var name = "big.tar.gz"; var size = FileServer.Blob(name).Length;
        var bytes = new Collect();
        var ok = await new FileService(new HttpClient()).DownloadAsync($"{_server.Url}/{name}", Path.Combine(_dir, name), connections, new Progress<int>(), default, bytes);
        Assert.True(ok);
        Assert.Equal(size, bytes.Values[^1]);                                              // final = file size, whether 1 or 8 connections wrote it
        Assert.True(bytes.Values.Zip(bytes.Values.Skip(1), (a, b) => b >= a).All(x => x));   // never goes backwards
        Assert.All(bytes.Values, v => Assert.InRange(v, 1, size));
    }

    [Fact]
    public async Task RangeFallback_DoesNotDoubleCountBytes()
    {
        var files = new Dictionary<string, byte[]> { ["/a.bin"] = new byte[600_000] };
        var port = new Random().Next(20000, 40000); var l = new HttpListener(); l.Prefixes.Add($"http://127.0.0.1:{port}/"); l.Start();
        _ = Task.Run(async () => { while (l.IsListening) { HttpListenerContext c; try { c = await l.GetContextAsync(); } catch { return; }
            try { c.Response.ContentLength64 = 600_000; if (c.Request.HttpMethod != "HEAD") await c.Response.OutputStream.WriteAsync(files["/a.bin"]); c.Response.Close(); } catch { } } });   // ignores Range
        try
        {
            var bytes = new Collect();
            Assert.True(await new FileService(new HttpClient()).DownloadAsync($"http://127.0.0.1:{port}/a.bin", Path.Combine(_dir, "a.bin"), 4, new Progress<int>(), default, bytes));
            Assert.Equal(600_000, bytes.Values[^1]);
        }
        finally { l.Close(); }
    }

    [Fact]
    public async Task Copy_ReportsBytes()
    {
        var src = Path.Combine(_dir, "s.bin"); await File.WriteAllBytesAsync(src, new byte[3_000_000]);
        var bytes = new Collect();
        Assert.True(await FileService.CopyAsync(src, Path.Combine(_dir, "d.bin"), new Progress<int>(), default, bytes));
        Assert.Equal(3_000_000, bytes.Values[^1]);
    }
}

public class RunnerTransferProgressTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("syn3rp").FullName;
    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Sink : IProgress<RunProgress> { public List<RunProgress> All { get; } = new(); public void Report(RunProgress p) { lock (All) All.Add(p); } }

    [Fact]
    public async Task Progress_CarriesBytesRemainingAndConnections_PerPhase()
    {
        using var server = new FileServer();
        Ivsu I(string n) => new() { Type = "APPS", Name = n, FileName = n + ".tar.gz", Url = $"{server.Url}/{n}.tar.gz", Md5 = FileServer.Md5(n + ".tar.gz"), FileSize = FileServer.Blob(n + ".tar.gz").Length };
        var rw = I("rw"); var a = I("a"); var b = I("b");
        var usb = Path.Combine(_root, "usb"); Directory.CreateDirectory(usb);
        var req = new UpdateRequest
        {
            Queue = new() { rw, a, b }, Tools = new SpecialTools { RwDataCleaner = rw }, Release = "r", Region = "NA", Drive = new DriveInfoModel { IsFolder = true, MountPath = usb }, SkipFormat = true,
            DownloadDir = Path.Combine(_root, "dl"), Connections = 4, AppVersion = "3",
        };
        var sink = new Sink();
        var runner = new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog());
        Assert.True(await runner.RunAsync(req, sink, default), runner.LastError);

        long total = rw.FileSize + a.FileSize + b.FileSize;
        var dl = sink.All.Where(p => p.Phase == "download").ToList(); var copy = sink.All.Where(p => p.Phase == "copy").ToList();
        Assert.NotEmpty(dl); Assert.NotEmpty(copy);
        Assert.Equal(total, dl.Max(p => p.TransferredBytes)); Assert.Equal(total, copy.Max(p => p.TransferredBytes));      // each phase moves every file once
        Assert.True(dl.Where(p => p.TransferredBytes > 0).All(p => p.Connections == 4)); Assert.True(copy.All(p => p.Connections == 1));
        Assert.Equal(total, dl.Max(p => p.TransferredBytes + p.RemainingBytes));                                         // moved + remaining adds up to the plan
        Assert.Equal(0, dl.Last(p => p.TransferredBytes == total).RemainingBytes);
        Assert.True(dl.Where(p => p.TransferredBytes > 0).Zip(dl.Where(p => p.TransferredBytes > 0).Skip(1), (x, y) => y.TransferredBytes >= x.TransferredBytes).All(v => v));
        Assert.Equal(0, copy.First().TransferredBytes);                                                                      // the counter restarts for the copy phase
    }

    [Fact]
    public async Task FilesAlreadyDownloaded_AreNotCountedAsWork()
    {
        using var server = new FileServer();
        var blob = FileServer.Blob("x.tar.gz");
        var dl = Path.Combine(_root, "dl"); Directory.CreateDirectory(dl); File.WriteAllBytes(Path.Combine(dl, "x.tar.gz"), blob);
        var item = new Ivsu { FileName = "x.tar.gz", Url = $"{server.Url}/x.tar.gz", Md5 = FileServer.Md5("x.tar.gz"), FileSize = blob.Length };
        var sink = new Sink();
        var req = new UpdateRequest { Queue = new() { item }, Tools = new SpecialTools(), DownloadOnly = true, DownloadDir = dl, Connections = 8 };
        Assert.True(await new UpdateRunner(DriveServiceFactory.Create(), new FileService(new HttpClient()), new AppLog()).RunAsync(req, sink, default));
        Assert.All(sink.All.Where(p => p.Phase == "download"), p => { Assert.Equal(0, p.TransferredBytes); Assert.Equal(0, p.RemainingBytes); });
    }
}

/// <summary>Records every command a drive service issues and answers from a script, so each platform's eject sequence can be checked without hardware.</summary>
public sealed class ScriptedCommands
{
    public List<string> Calls { get; } = new();
    public Func<string, string[], ProcessResult> Handler { get; set; } = (_, _) => new ProcessResult(0, "", "");
    public CommandRunner Runner => (file, args, stdin, ct, timeout) => { var a = args.ToArray(); lock (Calls) Calls.Add(file + " " + string.Join(" ", a)); return Task.FromResult(Handler(file, a)); };
    public static ProcessResult Ok(string stdout = "") => new(0, stdout, "");
    public static ProcessResult Fail(string stderr, int code = 1) => new(code, "", stderr);
}

public class EjectTests
{
    private const string LsblkJson = """{"blockdevices":[{"name":"nvme0n1","path":"/dev/nvme0n1","type":"disk","size":1000204886016,"tran":"nvme","hotplug":false,"rm":false,"model":"SSD","pttype":"gpt","children":[{"name":"p2","path":"/dev/nvme0n1p2","type":"part","size":1,"fstype":"btrfs","mountpoint":"/"}]},{"name":"sda","path":"/dev/sda","type":"disk","size":31914983424,"tran":"usb","hotplug":true,"rm":true,"model":"Ultra","pttype":"dos","children":[{"name":"sda1","path":"/dev/sda1","type":"part","size":31913934848,"fstype":"exfat","label":"CYANLABS","mountpoint":"/run/media/scott/CYAN LABS"}]}]}""";
    private static readonly DriveInfoModel Stick = new() { DeviceId = "/dev/sda", Name = "Ultra", MountPath = "/run/media/scott/CYANLABS" };

    private static (LinuxDriveService, ScriptedCommands) Linux(Func<string, string[], ProcessResult?>? extra = null, bool udisks = true)
    {
        var cmd = new ScriptedCommands
        {
            Handler = (f, a) => extra?.Invoke(f, a) is { } r ? r
                : f == "lsblk" && a.Contains("-J") ? ScriptedCommands.Ok(LsblkJson)
                : f == "lsblk" ? ScriptedCommands.Ok("/dev/sda disk \n/dev/sda1 part /run/media/scott/CYAN\\x20LABS\n")
                : ScriptedCommands.Ok(),
        };
        return (new LinuxDriveService(cmd.Runner, p => p == "udisksctl" ? udisks : true, (_, _) => Task.CompletedTask), cmd);
    }

    [Fact]
    public async Task Linux_Syncs_UnmountsEveryPartition_ThenPowersOff()
    {
        var (svc, cmd) = Linux();
        var r = await svc.EjectAsync(Stick, "/run/media/scott/CYANLABS");
        Assert.True(r.Ok); Assert.Contains("remove it now", r.Message);
        var steps = cmd.Calls.Where(c => !c.StartsWith("lsblk")).ToList();
        Assert.Equal(new[] { "sync ", "udisksctl unmount -b /dev/sda1", "udisksctl power-off -b /dev/sda" }, steps);
    }

    [Fact]
    public async Task Linux_RefusesDrivesItWouldNeverOffer_AndRunsNothing()
    {
        var (svc, cmd) = Linux();
        foreach (var id in new[] { "/dev/nvme0n1", "/dev/sdz", "/dev/sda; rm -rf /", "sda", "", "/dev/../etc" })
        {
            var r = await svc.EjectAsync(new DriveInfoModel { DeviceId = id }, null);
            Assert.False(r.Ok);
        }
        Assert.DoesNotContain(cmd.Calls, c => c.StartsWith("udisksctl") || c.StartsWith("umount") || c.StartsWith("sync"));
    }

    [Fact]
    public async Task Linux_BusyPartition_StopsBeforePowerOff_WithAHelpfulMessage()
    {
        var (svc, cmd) = Linux((f, a) => f == "udisksctl" && a[0] == "unmount" ? ScriptedCommands.Fail("target is busy") : null);
        var r = await svc.EjectAsync(Stick, null);
        Assert.False(r.Ok); Assert.Contains("target is busy", r.Message); Assert.Contains("Close anything", r.Message);
        Assert.DoesNotContain(cmd.Calls, c => c.Contains("power-off"));
    }

    [Fact]
    public async Task Linux_AlreadyUnmounted_IsFine_AndPowerOffFailureStillMeansSafeToRemove()
    {
        var (svc, _) = Linux((f, a) => f == "udisksctl" ? (a[0] == "unmount" ? ScriptedCommands.Fail("Object /dev/sda1 is not mounted") : ScriptedCommands.Fail("not supported")) : null);
        var r = await svc.EjectAsync(Stick, null);
        Assert.True(r.Ok); Assert.Contains("unmounted", r.Message);
    }

    [Fact]
    public async Task Linux_WithoutUdisks_FallsBackToUmount_WithTheRightMountPoint()
    {
        var (svc, cmd) = Linux(udisks: false);
        Assert.True((await svc.EjectAsync(Stick, null)).Ok);
        Assert.Contains("umount /run/media/scott/CYAN LABS", cmd.Calls);                          // "\x20" from lsblk is decoded back to a space
        var (failing, _) = Linux((f, a) => f == "umount" ? ScriptedCommands.Fail("permission denied") : null, udisks: false);
        var r = await failing.EjectAsync(Stick, null);
        Assert.False(r.Ok); Assert.Contains("udisks2", r.Message);
    }

    private static string MacList(bool present) => present
        ? """<plist version="1.0"><dict><key>AllDisksAndPartitions</key><array><dict><key>DeviceIdentifier</key><string>disk4</string><key>Size</key><integer>32000000000</integer><key>Partitions</key><array/></dict></array></dict></plist>"""
        : """<plist version="1.0"><dict><key>AllDisksAndPartitions</key><array/></dict></plist>""";
    private const string MacInfo = """<plist version="1.0"><dict><key>Internal</key><false/><key>MediaName</key><string>SanDisk</string><key>Content</key><string>FDisk_partition_scheme</string></dict></plist>""";

    [Fact]
    public async Task Mac_EjectsTheExternalDisk_WithDiskutil()
    {
        var cmd = new ScriptedCommands { Handler = (f, a) => a[0] == "list" ? ScriptedCommands.Ok(MacList(true)) : a[0] == "info" ? ScriptedCommands.Ok(MacInfo) : ScriptedCommands.Ok("Disk disk4 ejected") };
        var svc = new MacDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask);
        Assert.True((await svc.EjectAsync(new DriveInfoModel { DeviceId = "/dev/disk4" }, "/Volumes/CYANLABS")).Ok);
        Assert.Contains("diskutil eject /dev/disk4", cmd.Calls);
    }

    [Fact]
    public async Task Mac_RefusesInternalOrUnknownDisks_AndExplainsFailures()
    {
        var cmd = new ScriptedCommands { Handler = (f, a) => a[0] == "list" ? ScriptedCommands.Ok(MacList(true)) : a[0] == "info" ? ScriptedCommands.Ok(MacInfo) : ScriptedCommands.Fail("Volume is busy") };
        var svc = new MacDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask);
        foreach (var id in new[] { "/dev/disk0", "disk4", "/dev/disk4s1", "/dev/disk4; reboot", "" }) Assert.False((await svc.EjectAsync(new DriveInfoModel { DeviceId = id }, null)).Ok);
        Assert.DoesNotContain(cmd.Calls, c => c.StartsWith("diskutil eject"));
        var busy = await svc.EjectAsync(new DriveInfoModel { DeviceId = "/dev/disk4" }, null);
        Assert.False(busy.Ok); Assert.Contains("Volume is busy", busy.Message); Assert.Contains("Finder", busy.Message);
    }

    private const string WinPresent = """[{"Number":2,"Name":"SanDisk","Size":32000000000,"Style":"MBR","Letter":"E","Fs":"exFAT","Label":"CYANLABS","Free":1,"Encrypted":false}]""";

    [Fact]
    public async Task Windows_FlushesThenUsesTheShellEjectVerb_AndConfirmsTheDriveIsGone()
    {
        bool ejected = false;
        var cmd = new ScriptedCommands { Handler = (f, a) => { if (a[^1].Contains("InvokeVerb")) ejected = true; return a[^1].Contains("Get-Disk") ? ScriptedCommands.Ok(ejected ? "[]" : WinPresent) : ScriptedCommands.Ok(); } };
        var svc = new WindowsDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask);
        var r = await svc.EjectAsync(new DriveInfoModel { DeviceId = "2", MountPath = @"E:\" }, @"e:\");
        Assert.True(r.Ok);
        var script = cmd.Calls.First(c => c.Contains("InvokeVerb")); Assert.Contains("Write-VolumeCache -DriveLetter E", script); Assert.Contains("InvokeVerb('Eject')", script); Assert.Contains("$l='E:'", script);
    }

    [Fact]
    public async Task Windows_ReportsWhenTheDriveIsStillThere_BadLetters_AndMissingDrives()
    {
        var stuck = new ScriptedCommands { Handler = (f, a) => a[^1].Contains("Get-Disk") ? ScriptedCommands.Ok(WinPresent) : ScriptedCommands.Ok() };
        var svc = new WindowsDriveService(stuck.Runner, _ => true, (_, _) => Task.CompletedTask);
        var still = await svc.EjectAsync(new DriveInfoModel { MountPath = @"E:\" }, null);
        Assert.False(still.Ok); Assert.Contains("Explorer", still.Message);

        var cmd = new ScriptedCommands();
        var s2 = new WindowsDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask);
        foreach (var bad in new[] { "", "E", "'; Remove-Item C:\\ -Recurse #", "1:\\" }) Assert.False((await s2.EjectAsync(new DriveInfoModel { MountPath = bad }, bad)).Ok);
        Assert.Empty(cmd.Calls);                                                                    // nothing is ever built from an invalid letter

        var system = new ScriptedCommands { Handler = (f, a) => a[^1].Contains("Get-Disk") ? ScriptedCommands.Ok(WinPresent) : ScriptedCommands.Ok() };   // only E: is a USB drive
        var refused = await new WindowsDriveService(system.Runner, _ => true, (_, _) => Task.CompletedTask).EjectAsync(new DriveInfoModel { MountPath = @"C:\" }, null);
        Assert.False(refused.Ok); Assert.Contains("refusing", refused.Message); Assert.DoesNotContain(system.Calls, c => c.Contains("InvokeVerb"));    // C: is never ejected

        var gone = new ScriptedCommands { Handler = (f, a) => a[^1].Contains("InvokeVerb") ? new ProcessResult(3, "", "") : a[^1].Contains("Get-Disk") ? ScriptedCommands.Ok(WinPresent) : ScriptedCommands.Ok() };
        Assert.Contains("does not list", (await new WindowsDriveService(gone.Runner, _ => true, (_, _) => Task.CompletedTask).EjectAsync(new DriveInfoModel { MountPath = @"E:\" }, null)).Message);
        var broken = new ScriptedCommands { Handler = (f, a) => a[^1].Contains("InvokeVerb") ? ScriptedCommands.Fail("boom") : a[^1].Contains("Get-Disk") ? ScriptedCommands.Ok(WinPresent) : ScriptedCommands.Ok() };
        Assert.Contains("boom", (await new WindowsDriveService(broken.Runner, _ => true, (_, _) => Task.CompletedTask).EjectAsync(new DriveInfoModel { MountPath = @"E:\" }, null)).Message);
    }

    [Fact]
    public async Task DefaultRunner_IsTheRealProcessRunner()
    {
        if (!OperatingSystem.IsLinux()) return;       // the Linux service shells out to lsblk
        var svc = new LinuxDriveService();            // no injection: still constructs and answers (guard rejects a bogus device before touching the system)
        Assert.False((await svc.EjectAsync(new DriveInfoModel { DeviceId = "/dev/definitely-not-there" }, null)).Ok);
    }
}
