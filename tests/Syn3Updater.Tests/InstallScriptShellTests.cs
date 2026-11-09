using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Syn3Updater.Tests;

/// <summary>
/// "sh install.sh" on Debian, Ubuntu and Mint runs dash, which has no "set -o pipefail": a tester got "Illegal option -o pipefail". The scripts in the archive must work whichever shell starts them.
/// </summary>
public class InstallScriptShellTests
{
    private static string Repo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "package-linux.sh"))) return dir.FullName;
        throw new FileNotFoundException("scripts/package-linux.sh");
    }

    /// <summary>The text of the script package-linux.sh writes into the archive (its heredoc), by the heredoc's tag.</summary>
    private static string Shipped(string tag)
    {
        // a Windows checkout turns the script's line endings into CRLF; the archive is built on Linux, where they are LF
        var text = File.ReadAllText(Path.Combine(Repo(), "scripts", "package-linux.sh")).Replace("\r\n", "\n");
        var m = Regex.Match(text, $"<<'{tag}'\n(.*?)\n{tag}\n", RegexOptions.Singleline);
        Assert.True(m.Success, tag);
        return m.Groups[1].Value + "\n";
    }

    [Theory]
    [InlineData("INST")] [InlineData("UNINST")]
    public void The_shipped_script_hands_over_to_bash_before_the_first_bash_only_line(string tag)
    {
        var lines = Shipped(tag).Split('\n');
        var guard = Array.FindIndex(lines, l => l.Contains("BASH_VERSION"));
        var pipefail = Array.FindIndex(lines, l => l.StartsWith("set -", StringComparison.Ordinal));
        Assert.True(guard >= 0, "no bash check");
        Assert.True(guard < pipefail, "the bash check must come before 'set -o pipefail'");
        Assert.Contains(lines, l => l.Contains("exec bash \"$0\""));
    }

    private static bool Has(string tool) { try { using var p = Process.Start(new ProcessStartInfo(tool, "-c true") { RedirectStandardError = true, UseShellExecute = false }); p!.WaitForExit(5000); return p.ExitCode == 0; } catch { return false; } }

    [Fact]
    public void Running_the_script_with_dash_works_instead_of_failing_on_pipefail()
    {
        if (OperatingSystem.IsWindows() || !Has("dash") || !Has("bash")) return;                    // dash is /bin/sh on Debian, Ubuntu and Mint (the CI runners); elsewhere there is nothing to test
        var root = Directory.CreateTempSubdirectory("syn3sh").FullName;
        try
        {
            var script = Path.Combine(root, "uninstall.sh");
            File.WriteAllText(script, Shipped("UNINST"));
            var home = Path.Combine(root, "home"); Directory.CreateDirectory(home);                  // a temporary home: uninstall.sh only removes things under it
            var psi = new ProcessStartInfo("dash", "uninstall.sh") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.Environment["HOME"] = home; psi.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
            using var p = Process.Start(psi)!; var err = p.StandardError.ReadToEnd(); p.WaitForExit(30000);
            Assert.DoesNotContain("Illegal option", err);
            Assert.Equal(0, p.ExitCode);
        }
        finally { Directory.Delete(root, true); }
    }
}
