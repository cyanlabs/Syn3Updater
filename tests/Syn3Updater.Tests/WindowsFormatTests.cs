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
        File.WriteAllText(file, WindowsDriveService.FormatScript.Replace("{0}", "2").Replace("{1}", "E"));
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
        Assert.Equal(@"E:\", path);                       // the post-format drive list wins when it shows the drive
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
    public async Task Format_RefusesADiskThatIsNoLongerListedAsUsb()
    {
        var cmd = new ScriptedCommands { Handler = (f, a) => ScriptedCommands.Ok("[]") };
        var svc = new WindowsDriveService(cmd.Runner, _ => true, (_, _) => Task.CompletedTask);
        await Assert.ThrowsAsync<Cyanlabs.Syn3Updater.Core.Drives.FormatException>(() => svc.FormatAsync(Disk(), Log));
        Assert.DoesNotContain(cmd.Calls, c => c.Contains("Clear-Disk"));
    }
}
