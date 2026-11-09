using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Models;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>Each package row shows the first line of the package's notes.</summary>
public class PackageNoteLineTests
{
    [Theory]
    [InlineData("First line\nSecond line", "First line")]
    [InlineData("First line", "First line")]
    [InlineData("\n\n  \nFirst real line\nmore", "First real line")]            // blank lines before the text are skipped
    [InlineData("First\r\nSecond", "First")]                                    // Windows line endings
    [InlineData("First\rSecond", "First")]
    [InlineData("   padded   \nx", "padded")]
    [InlineData("## Heading\ntext", "Heading")]                                 // Markdown markers are not shown
    [InlineData("- Fixes a bug\n- Fixes another", "Fixes a bug")]
    [InlineData("* Bullet", "Bullet")]
    [InlineData("> Quoted", "Quoted")]
    [InlineData("**Bold** and `code` and __under__", "Bold and code and under")]
    [InlineData("- **Fixes** the thing", "Fixes the thing")]
    [InlineData("1. numbered keeps its number", "1. numbered keeps its number")]
    [InlineData("a - b stays", "a - b stays")]                                  // only a leading marker goes
    [InlineData("-\n  -  \nReal", "Real")]                                      // a line that is only markers is empty
    [InlineData("", "")]
    [InlineData("   \n \n", "")]
    [InlineData(null, "")]
    public void The_first_line_with_text_is_shown_as_plain_text(string? notes, string expected) => Assert.Equal(expected, PackageRow.FirstLine(notes));

    [Fact]
    public void A_row_exposes_the_line_the_flag_and_the_full_notes_for_the_tooltip()
    {
        var row = new PackageRow(new Ivsu { Type = "APPS", Name = "APP1", Notes = "Faster maps.\nAlso fixes the voice.\n" });
        Assert.Equal("Faster maps.", row.NoteLine); Assert.True(row.HasNoteLine);
        Assert.Equal("Faster maps.\nAlso fixes the voice.", row.FullNotes);
    }

    [Fact]
    public void A_package_without_notes_shows_no_extra_line()
    {
        var row = new PackageRow(new Ivsu { Type = "APPS", Name = "APP1", Notes = "" });
        Assert.Equal("", row.NoteLine); Assert.False(row.HasNoteLine); Assert.Equal("", row.FullNotes);
    }

    [Fact]
    public void A_long_first_line_is_kept_whole_and_trimmed_by_the_view()
    {
        var line = new string('x', 400);
        Assert.Equal(line, new PackageRow(new Ivsu { Notes = line + "\nnext" }).NoteLine);
    }

    [AvaloniaFact]
    public async Task A_tool_row_shows_its_notes_line_too()
    {
        using var env = new AppEnv(); var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 3);
        var tool = home.Packages.Single(r => r.IsRequiredTool);
        tool.Ivsu.Notes = "Removes old data first.\nSecond line";                      // what the backend sends for a special package
        Assert.Equal("Removes old data first.", tool.NoteLine); Assert.Equal("Required tool", tool.Subtitle);
    }
}
