using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>A drive service that "formats" by relabelling: the stick comes back mounted under a different path (DVD_ROM -> CYANLABS).</summary>
public sealed class RemountingDrives(string remountedAt) : IDriveService
{
    public string OsDescription => "fake";
    public int Formats { get; private set; }
    public Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DriveInfoModel>>(new List<DriveInfoModel>());
    public Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default)
    { Formats++; Directory.CreateDirectory(remountedAt); return Task.FromResult(remountedAt); }
    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.FromResult(drive.MountPath);
    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;

    public List<(string Device, string? Mount)> Ejected { get; } = new();
    public EjectResult EjectAnswer { get; set; } = new(true, "The drive was ejected. You can remove it now.");
    public Task<EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default)
    { Ejected.Add((drive.DeviceId, mountPath)); return Task.FromResult(EjectAnswer); }
}

public class FormatRemountTests
{
    [AvaloniaFact]
    public async Task LogAndFolder_UseThePathAfterFormat_NotTheOldMountPoint()
    {
        var root = Directory.CreateTempSubdirectory("syn3remount").FullName;
        var after = Path.Combine(root, "run", "media", "scott", "CYANLABS");
        var drives = new RemountingDrives(after);
        using var env = new AppEnv(drives: drives);
        var stale = Path.Combine(root, "run", "media", "scott", "DVD_ROM");      // what the drive was called when it was selected
        var usb = new DriveInfoModel { DeviceId = "/dev/sdx", MountPath = stale, Name = "Stick", FileSystem = "ntfs", PartitionType = "MBR" };
        env.Dialogs.Say(m => m.StartsWith("ALL DATA ON THE SELECTED DRIVE"), true);   // confirm the format

        Assert.True(await Workflow.StartAsync(new JobSpec
        {
            Mode = InstallModes.Autoinstall, Release = "r", Region = "NA", Drive = usb, Action = UpdateAction.Main,
            Packages = { new Ivsu { Type = "APPS", Name = "A", FileName = "A.tar.gz", Url = env.Files.Url + "/A.tar.gz", Md5 = FileServer.Md5("A.tar.gz"), FileSize = FileServer.Blob("A.tar.gz").Length } },
        }));
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));

        Assert.Equal(1, drives.Formats);
        Assert.Equal(after, Ctx.Runner.TargetRoot);
        Assert.True(File.Exists(Path.Combine(after, "log.txt")));                 // the bug: this used to throw DirectoryNotFound on .../DVD_ROM/log.txt
        Assert.True(File.Exists(Path.Combine(after, "autoinstall.lst")));
        Assert.True(File.Exists(Path.Combine(after, "SyncMyRide", "A.tar.gz")));
        Assert.False(Directory.Exists(stale));
        Assert.DoesNotContain(env.Dialogs.Messages, m => m.Contains("Could not find a part of the path"));
        Assert.NotEmpty(Directory.GetFiles(Ctx.Main.LogPath!, "log-*.txt"));
        Directory.Delete(root, true);
    }

    [AvaloniaFact]
    public async Task UnavailableStick_DoesNotBlockTheLocalLogOrFinish()
    {
        using var env = new AppEnv();
        var gone = Path.Combine(env.Dir, "unplugged");
        var req = new UpdateRequest { Queue = new(), Tools = new SpecialTools(), Drive = new DriveInfoModel { IsFolder = true, MountPath = gone }, DownloadDir = env.DownloadDir };
        Directory.CreateDirectory(gone);
        var ok = await Workflow.StartAsync(new JobSpec { Drive = req.Drive, Release = "r", Region = "NA", SpecialPackage = SpecialPackages.GraceNotesRemoval, Action = UpdateAction.GracenotesRemoval, NeedRwCleaner = false, Mode = InstallModes.Autoinstall });
        Assert.True(ok);
        Directory.Delete(gone, true);                                              // stick vanishes mid-run: finishing must not throw a path error at the user
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning);
        Assert.DoesNotContain(env.Dialogs.Messages, m => m.Contains("Could not find a part of the path") && m.Contains("log.txt"));
    }
}
