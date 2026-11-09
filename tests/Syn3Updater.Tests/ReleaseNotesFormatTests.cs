using System.Diagnostics;
using Xunit;

namespace Syn3Updater.Tests;

/// <summary>
/// The Release workflow's "notes" input is typed on one line (gh workflow run -f notes="..."), so a new line is typed as \n. scripts/format-release-notes.sh turns that into real lines for the
/// update dialogs and tidies the spacing.
/// </summary>
public class ReleaseNotesFormatTests
{
    private static string Script()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "scripts", "format-release-notes.sh"))) return Path.Combine(dir.FullName, "scripts", "format-release-notes.sh");
        throw new FileNotFoundException("scripts/format-release-notes.sh");
    }

    private static string? Run(string input)
    {
        if (OperatingSystem.IsWindows()) return null;                      // a bash script run by the workflow on Linux; Windows checkouts also change its line endings
        var psi = new ProcessStartInfo("bash", new[] { Script() }) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(input); p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit(30000);
        Assert.True(p.ExitCode == 0, error);
        return output;
    }

    [Theory]
    [InlineData("- one\\n- two", "- one\n- two\n")]                                         // a typed \n is a new line
    [InlineData("- one\n- two", "- one\n- two\n")]                                           // real new lines are kept
    [InlineData("Fixes a bug.", "Fixes a bug.\n")]                                           // one plain line is unchanged
    [InlineData("\\n\\n- one\\n\\n", "- one\n")]                                              // leading and trailing blank lines are dropped
    [InlineData("- one\\n\\n\\n\\n- two", "- one\n\n- two\n")]                                 // any number of blank lines becomes one
    [InlineData("- one   \\n- two\t", "- one\n- two\n")]                                      // trailing spaces and tabs go
    [InlineData("- one\r\n- two\r\n", "- one\n- two\n")]                                     // Windows line endings go
    [InlineData("Heading\\n\\n- a\\n- b", "Heading\n\n- a\n- b\n")]                           // a paragraph, then a list
    [InlineData("", "")]                                                                     // no notes: nothing is written, and the manifest gets an empty value
    [InlineData("   \\n  ", "")]                                                             // nothing but blanks: empty too
    public void Typed_and_real_new_lines_become_tidy_lines(string input, string expected)
    {
        var output = Run(input);
        if (output == null) return;
        Assert.Equal(expected, output);
    }

    [Fact]
    public void The_example_from_the_docs_gives_one_bullet_per_line()
    {
        var output = Run("- Fixes duplicated packages\\n- Windows 11 is shown correctly\\n- install.sh works when run with sh");
        if (output == null) return;
        Assert.Equal(new[] { "- Fixes duplicated packages", "- Windows 11 is shown correctly", "- install.sh works when run with sh" }, output.TrimEnd('\n').Split('\n'));
    }

    [Fact]
    public void The_Release_workflow_runs_the_notes_through_the_formatter()
    {
        string? yaml = null;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null && yaml == null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, ".github", "workflows", "release.yml"))) yaml = File.ReadAllText(Path.Combine(dir.FullName, ".github", "workflows", "release.yml"));
        Assert.NotNull(yaml);
        Assert.Contains("INPUT_NOTES\" | scripts/format-release-notes.sh", yaml);
    }
}
