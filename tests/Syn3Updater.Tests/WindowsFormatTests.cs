using Cyanlabs.Syn3Updater.Core.Drives;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class WindowsFormatTests
{
    private const string Present = """[{"Number":2,"Name":"SanDisk","Size":32000000000,"Style":"RAW","Letter":"E","Fs":"","Label":"","Free":0,"Encrypted":false}]""";

    private static (WindowsDriveService svc, ScriptedCommands cmd) Make(Func<string, string[], ProcessResult> format)
    {
        var cmd = new ScriptedCommands { Handler = (f, a) => a.Length > 0 && a[^1].Contains("Get-Disk") && !a[^1].Contains("Clear-Disk") ? ScriptedCommands.Ok(Present) : f == "diskpart" || a[^1].Contains("Clear-Disk") ? format(f, a) : ScriptedCommands.Ok() };
        return (new WindowsDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask), cmd);
    }

    private static readonly IProgress<string> Log = new Progress<string>();
    private static DriveInfoModel Disk(string id = "2", string mount = @"E:\") => new() { DeviceId = id, MountPath = mount, Name = "SanDisk" };

    /// <summary>Only meaningful on Windows (the CI Windows runner): PowerShell's own parser must accept the script with real values substituted.</summary>
    [Fact]
    public async Task FormatScript_ParsesCleanlyInRealPowerShell_WhenRunOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        var file = Path.Combine(Path.GetTempPath(), "syn3-format-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(file, WindowsDriveService.FormatScript.Replace("{0}", "2").Replace("{1}", "E").Replace("{2}", "32000000000").Replace("{3}", "SanDisk"));
        try
        {
            var check = "$e=$null;$t=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('" + file + "',[ref]$t,[ref]$e);if($e.Count){$e|ForEach-Object{[Console]::Error.WriteLine($_.Message)};exit 1}";
            var r = await ProcessRunner.RunAsync("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", check });
            Assert.True(r.Ok, r.StdErr + r.StdOut);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task Format_UsesThePowerShellCmdlets_NotDiskpart()
    {
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok("LETTER=E"));
        Assert.Equal(@"E:\", await svc.FormatAsync(Disk(), Log));
        Assert.DoesNotContain(cmd.Calls, c => c.StartsWith("diskpart"));
        var script = cmd.Calls.Single(c => c.Contains("Clear-Disk"));
        Assert.Contains("$n = 2;", script); Assert.Contains("$keep = 'E'", script);
        Assert.Contains("Initialize-Disk -Number $n -PartitionStyle MBR", script);
        Assert.Contains("-FileSystem exFAT", script); Assert.Contains("-NewFileSystemLabel 'CYANLABS'", script);
        Assert.Contains("BusType -ne 'USB'", script); Assert.Contains("IsSystem", script);          // the script re-checks it is a removable disk itself
    }

    [Fact]
    public void FormatScript_UsesOnlySingleQuotes_SoCommandLineQuotingCannotMangleIt() =>
        Assert.DoesNotContain('"', WindowsDriveService.FormatScript);

    [Fact]
    public async Task Format_WithoutAKnownLetter_LetsWindowsAssignOne_AndReportsIt()
    {
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok("LETTER=G"));
        var path = await svc.FormatAsync(Disk(mount: ""), Log);
        Assert.Contains("$keep = ''", cmd.Calls.Single(c => c.Contains("Clear-Disk")));
        Assert.Equal(@"G:\", path);                       // the letter the script reports is used: no second listing is needed to find it
        Assert.Single(cmd.Calls);                          // and nothing else ran: no listing before the format, none after
    }

    [Theory]
    [InlineData("2; Remove-Item C:\\ -Recurse")]
    [InlineData("-1")]
    [InlineData("")]
    public async Task Format_RejectsAnythingButAPlainDiskNumber_BeforeRunningAnything(string id)
    {
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok());
        await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => svc.FormatAsync(Disk(id), Log));
        Assert.DoesNotContain(cmd.Calls, c => c.Contains("Clear-Disk") || c.StartsWith("diskpart"));
    }

    [Fact]
    public async Task Format_NeverPutsAnUntrustedDriveLetterInTheScript()
    {
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok("LETTER=E"));
        await svc.FormatAsync(Disk(mount: "';calc;'"), Log);
        var script = cmd.Calls.Single(c => c.Contains("Clear-Disk"));
        Assert.Contains("$keep = ''", script); Assert.DoesNotContain("calc", script);
    }

    [Fact]
    public async Task Format_ExplainsFailures_AndPointsAtAdministratorWhenAccessIsDenied()
    {
        var (svc, _) = Make((_, _) => new ProcessResult(1, "", "Access is denied"));
        var e = await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => svc.FormatAsync(Disk(), Log));
        Assert.Contains("Access is denied", e.Message); Assert.Contains("Administrator", e.Message);
        var (svc2, _) = Make((_, _) => new ProcessResult(1, "", "The disk is in use"));
        var e2 = await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => svc2.FormatAsync(Disk(), Log));
        Assert.Contains("in use", e2.Message); Assert.DoesNotContain("Administrator", e2.Message);
    }

    [Fact]
    public async Task Format_FallsBackToDiskpart_OnlyWhenTheStorageCmdletsAreMissing()
    {
        var (svc, cmd) = Make((f, _) => f == "diskpart" ? ScriptedCommands.Ok() : new ProcessResult(3, "", "The Windows storage cmdlets are not available"));
        Assert.Equal(@"E:\", await svc.FormatAsync(Disk(), Log));
        var dp = cmd.Stdin.Single(s => s.Contains("CLEAN"));
        Assert.Contains("SELECT DISK=2", dp); Assert.Contains("FORMAT FS=EXFAT LABEL=\"CYANLABS\" QUICK", dp); Assert.Contains("ASSIGN LETTER=E", dp);
    }

    [Fact]
    public async Task Format_RunsNoListingFirst_TheScriptChecksTheDiskItselfAtTheMomentOfTheFormat()
    {
        // Before: a separate PowerShell listing ran first, and one odd answer made it refuse a good drive ("Disk 1 is no longer a USB drive").
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok("LETTER=E"));
        await svc.FormatAsync(new DriveInfoModel { DeviceId = "2", MountPath = @"E:\", Name = "SanDisk", SizeBytes = 32000000000 }, Log);
        Assert.Contains("Clear-Disk", cmd.Calls[0]);                                              // the very first thing that ran is the format script
        Assert.DoesNotContain(cmd.Calls, c => c.Contains("Get-Disk | Where-Object"));            // no listing of the USB drives at all
        var script = cmd.Calls[0];
        Assert.Contains("$size = 32000000000;", script); Assert.Contains("$name = 'SanDisk'", script);
        Assert.Contains("[int64]$d.Size -ne $size", script); Assert.Contains("$d.FriendlyName -ne $name", script); Assert.Contains("refusing to format it", script);
        Assert.True(script.IndexOf("$d.FriendlyName -ne $name", StringComparison.Ordinal) < script.IndexOf("Clear-Disk -Number", StringComparison.Ordinal));   // checked before anything is wiped
    }

    [Fact]
    public async Task Format_ASwappedDisk_IsRefusedByTheScript_AndTheMessageSaysSo()
    {
        var (svc, _) = Make((_, _) => new ProcessResult(1, "", "Disk 2 is no longer the drive you selected (it is now Samsung SSD); refusing to format it."));
        var e = await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => svc.FormatAsync(new DriveInfoModel { DeviceId = "2", Name = "SanDisk", SizeBytes = 32000000000 }, Log));
        Assert.Contains("no longer the drive you selected", e.Message); Assert.Contains("Samsung SSD", e.Message);
    }

    [Theory]
    [InlineData("SanDisk Ultra", "SanDisk Ultra")]
    [InlineData("O'Brien Stick", "O''Brien Stick")]            // a quote is doubled, the PowerShell way
    [InlineData("Odd \"name\"", "")]                            // a double quote would break the command line: skip the name check
    [InlineData("Line\nBreak", "")]
    [InlineData("", "")]
    public async Task Format_PutsOnlyASafeNameIntoTheScript(string name, string inScript)
    {
        var (svc, cmd) = Make((_, _) => ScriptedCommands.Ok("LETTER=E"));
        await svc.FormatAsync(new DriveInfoModel { DeviceId = "2", MountPath = @"E:\", Name = name, SizeBytes = 1 }, Log);
        var script = cmd.Calls[0];
        Assert.Contains($"$name = '{inScript}'", script); Assert.DoesNotContain('"', script);
    }

    [Fact]
    public async Task Format_WaitsForTheNewLetterToBeReady_ButNotForever()
    {
        var checks = 0; var waits = 0;
        var cmd = new ScriptedCommands { Handler = (_, _) => ScriptedCommands.Ok("LETTER=E") };
        var svc = new WindowsDriveService(cmd.Runner, _ => true, (ms, _) => { if (ms == 500) waits++; return Task.CompletedTask; }, root => ++checks > 3);   // ready on the 4th look
        Assert.Equal(@"E:\", await svc.FormatAsync(Disk(), Log));
        Assert.Equal(4, checks); Assert.Equal(3, waits);
        var never = new WindowsDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask, _ => false);
        Assert.Equal(@"E:\", await never.FormatAsync(Disk(), Log));                                  // never ready: still returns the letter after a bounded wait
    }
}
