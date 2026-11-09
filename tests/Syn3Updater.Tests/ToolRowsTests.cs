using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>
/// The tool packages a mode needs (RW data cleaner always, reformat tool for Reformat and Downgrade, the downgrade app and tool for Downgrade) are listed in the package list as locked rows, so the
/// counts and sizes include them and the list matches what is downloaded. A tester had seen "4 of 4 selected" and then 5 downloads.
/// </summary>
public class ToolRowsTests
{
    private static async Task Pick(AppEnv env, int rows)
    {
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == rows);
        env.Dialogs.FolderToPick = env.UsbDir; await home.ChooseFolderCommand.ExecuteAsync(null);
    }

    private static void Force(string mode) { Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = mode; }

    [Theory]
    [InlineData(InstallModes.AutoDetect, new[] { "rwdatacleaner" })]
    [InlineData(InstallModes.Autoinstall, new[] { "rwdatacleaner" })]
    [InlineData(InstallModes.Reformat, new[] { "reformat", "rwdatacleaner" })]
    [InlineData(InstallModes.Downgrade, new[] { "downgradeapp", "gracenotesremoval", "reformat", "rwdatacleaner" })]
    [InlineData("", new[] { "rwdatacleaner" })]
    public void Each_mode_has_its_tools_in_the_order_they_are_downloaded(string mode, string[] expected) => Assert.Equal(expected, ToolPlan.For(mode));

    [Fact]
    public void Tools_are_sorted_into_their_slots_by_the_packages_own_special_package_field_and_ordinary_packages_are_ignored()
    {
        Ivsu P(string n, string? special) => new() { Name = n, SpecialPackage = special };
        var t = ToolPlan.Collect(new[] { P("r", "reformat"), P("c", "rwdatacleaner"), P("a", "downgradeapp"), P("g", "gracenotesremoval"), P("x", "nonsense"), P("ordinary", null) });
        Assert.Equal(new[] { "r", "c", "a", "g" }, new[] { t.ReformatTool!.Name, t.RwDataCleaner!.Name, t.DowngradeApp!.Name, t.DowngradeTool!.Name });
    }

    [Fact]
    public void The_backends_specialpackage_field_is_kept_on_the_package_and_empty_means_an_ordinary_package()
    {
        var api = System.Text.Json.JsonSerializer.Deserialize<Cyanlabs.Syn3Updater.Core.Models.ApiIvsu>("""{"id":1,"name":"n","type":"TOOL","url":"https://x/f.tar.gz","specialpackage":"rwdatacleaner"}""")!;
        Assert.Equal("rwdatacleaner", api.ToIvsu().SpecialPackage);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<Cyanlabs.Syn3Updater.Core.Models.ApiIvsu>("""{"id":1,"name":"n","type":"APPS","url":"https://x/f.tar.gz"}""")!.ToIvsu().SpecialPackage);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<Cyanlabs.Syn3Updater.Core.Models.ApiIvsu>("""{"id":1,"name":"n","type":"APPS","url":"https://x/f.tar.gz","specialpackage":"  "}""")!.ToIvsu().SpecialPackage);
    }

    [AvaloniaFact]
    public async Task A_special_package_looked_up_by_name_records_that_name_even_if_the_backend_left_the_field_empty()
    {
        using var env = new AppEnv();
        Assert.Equal("reformat", (await Ctx.Api.GetSpecialPackage("reformat")).SpecialPackage);          // the test backend does not send the field
    }

    [Fact]
    public void A_row_is_a_locked_tool_because_its_package_has_a_special_package_not_because_of_where_it_came_from()
    {
        var tool = new PackageRow(new Ivsu { Name = "c", SpecialPackage = "rwdatacleaner" });
        var ordinary = new PackageRow(new Ivsu { Name = "APP1" });
        Assert.True(tool.IsRequiredTool); Assert.False(tool.IsOptional); Assert.Equal("rwdatacleaner", tool.ToolName);
        Assert.False(ordinary.IsRequiredTool); Assert.True(ordinary.IsOptional); Assert.Null(ordinary.ToolName);
    }

    [AvaloniaFact]
    public async Task A_plain_install_lists_the_RW_data_cleaner_after_the_packages_and_counts_it()
    {
        using var env = new AppEnv(); await Pick(env, 3); var home = env.Main.Home;
        Assert.Equal(new[] { "APP1", "VOICE1", "rwdatacleaner" }, home.Packages.Select(r => r.Ivsu.Name));
        Assert.Equal(new[] { false, false, true }, home.Packages.Select(r => r.IsRequiredTool));
        Assert.StartsWith("3 of 3 selected", home.SelectionSummary);
        Assert.Equal("Required tool", home.Packages[2].Subtitle); Assert.All(home.Packages, r => Assert.True(r.Selected));
    }

    [AvaloniaFact]
    public async Task Reformat_lists_the_reformat_tool_and_the_cleaner()
    {
        using var env = new AppEnv(); Force(InstallModes.Reformat); await Pick(env, 4);
        Assert.Equal(new[] { "APP1", "VOICE1", "reformat", "rwdatacleaner" }, env.Main.Home.Packages.Select(r => r.Ivsu.Name));
        Assert.StartsWith("4 of 4 selected", env.Main.Home.SelectionSummary);
    }

    [AvaloniaFact]
    public async Task Downgrade_lists_all_four_tools()
    {
        using var env = new AppEnv(); Force(InstallModes.Downgrade); await Pick(env, 6);
        Assert.Equal(new[] { "APP1", "VOICE1", "downgradeapp", "gracenotesremoval", "reformat", "rwdatacleaner" }, env.Main.Home.Packages.Select(r => r.Ivsu.Name));
        Assert.Equal(4, env.Main.Home.Packages.Count(r => r.IsRequiredTool));
    }

    [AvaloniaFact]
    public async Task A_tool_row_cannot_be_unticked_by_a_click_by_Select_none_or_by_setting_it()
    {
        using var env = new AppEnv(); await Pick(env, 3); var home = env.Main.Home; var tool = home.Packages[2];
        tool.ToggleCommand.Execute(null); Assert.True(tool.Selected);                       // clicking does nothing
        tool.Selected = false; Assert.True(tool.Selected);                                  // and so does anything else that tries
        home.SelectNoneCommand.Execute(null);
        Assert.False(home.Packages[0].Selected); Assert.False(home.Packages[1].Selected); Assert.True(tool.Selected);
        Assert.StartsWith("1 of 3 selected", home.SelectionSummary);
        home.SelectAllCommand.Execute(null); Assert.StartsWith("3 of 3 selected", home.SelectionSummary);
        home.Packages[0].ToggleCommand.Execute(null); Assert.False(home.Packages[0].Selected); Assert.StartsWith("2 of 3 selected", home.SelectionSummary);       // an ordinary row still toggles
    }

    [AvaloniaFact]
    public async Task With_every_package_unticked_nothing_starts_even_though_a_locked_tool_is_still_selected()
    {
        using var env = new AppEnv(); await Pick(env, 3); var home = env.Main.Home;
        home.SelectNoneCommand.Execute(null);
        await home.StartCommand.ExecuteAsync(null);
        Assert.Contains(env.Dialogs.Messages, m => m == Ctx.T("MessageBox.NoPackagesSelected"));
        Assert.False(env.Main.Downloads.IsRunning); Assert.False(Workflow.Busy);
    }

    [AvaloniaFact]
    public async Task Download_only_downloads_each_listed_file_once_and_does_not_look_the_tools_up_again()
    {
        using var env = new AppEnv(); await Pick(env, 3);
        int Lookups() => env.Api.Queries.Count(q => q.Contains("specialpackage"));
        var before = Lookups();
        env.Dialogs.Say(m => m == Ctx.T("MessageBox.DownloadOnlyMode"), true);
        await env.Main.Home.DownloadOnlyCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Dialogs.Messages.Contains(Ctx.T("MessageBox.DownloadOnlyComplete")));
        var done = env.Main.Downloads.Completed;
        Assert.Equal(new[] { "APP1.tar.gz", "VOICE1.tar.gz", "rwdatacleaner.tar.gz" }, done.Select(q => q.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Single(done, q => q.IsTool);
        Assert.Equal(before, Lookups());                                                      // what was shown is what is downloaded: no second lookup at Start
        Assert.True(File.Exists(Path.Combine(env.DownloadDir, "rwdatacleaner.tar.gz")));
    }

    [AvaloniaFact]
    public async Task A_reformat_run_writes_both_lists_correctly_with_the_listed_tools()
    {
        using var env = new AppEnv(); Force(InstallModes.Reformat); await Pick(env, 4);
        await env.Main.Home.StartCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !env.Main.Downloads.IsRunning && env.Main.Downloads.Status == Ctx.T("String.Completed"));
        var reformat = File.ReadAllText(Path.Combine(env.UsbDir, "reformat.lst"));
        Assert.Contains("APPS=APP1.tar.gz", reformat); Assert.DoesNotContain("rwdatacleaner", reformat); Assert.DoesNotContain("reformat.tar.gz", reformat);       // the tools are not part of what gets restored
        var lst = File.ReadAllText(Path.Combine(env.UsbDir, "autoinstall.lst"));
        Assert.Contains("RWDataCleaner TOOL - rwdatacleaner.tar.gz", lst); Assert.Contains("REFORMAT TOOL", lst);
        foreach (var f in new[] { "APP1", "VOICE1", "reformat", "rwdatacleaner" }) Assert.True(File.Exists(Path.Combine(env.UsbDir, "SyncMyRide", f + ".tar.gz")), f);
    }

    [AvaloniaFact]
    public async Task If_a_tool_cannot_be_looked_up_the_list_is_not_shown_and_the_error_says_so()
    {
        using var env = new AppEnv(); var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        env.Api.Gate = q => q.Contains("specialpackage") ? throw new HttpRequestException("backend down") : Task.CompletedTask;
        home.SelectedMap = home.MapVersions[0];
        await AppEnv.Until(() => env.Dialogs.Messages.Count > 0);
        Assert.Empty(home.Packages); Assert.False(home.StartEnabled); Assert.False(home.Loading);        // no half list that would start a job without its tools
    }

    [AvaloniaFact]
    public async Task Changing_the_selection_replaces_the_tool_rows_with_the_new_modes_tools()
    {
        using var env = new AppEnv(version: 3420282, nav: true); Force(InstallModes.Downgrade);
        var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count == 2);
        home.SelectedRelease = home.Releases[1]; await AppEnv.Until(() => home.MapVersions.Count == 3);
        home.SelectedMap = home.MapVersions[1]; await AppEnv.Until(() => home.Packages.Count == 8);
        Assert.Equal(4, home.Packages.Count(r => r.IsRequiredTool));
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count == 2);          // only maps: always a plain autoinstall
        home.SelectedMap = home.MapVersions[0]; await AppEnv.Until(() => home.Packages.Count == 3);
        Assert.Equal(new[] { "rwdatacleaner" }, home.Packages.Where(r => r.IsRequiredTool).Select(r => r.Ivsu.Name));
    }
}

public class ToolRowColourTests
{
    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    private static readonly System.Xml.Linq.XNamespace Ax = "https://github.com/avaloniaui", X = "http://schemas.microsoft.com/winfx/2006/xaml";

    /// <summary>The colour of brush <paramref name="key"/> in the Dark or Light theme dictionary of App.axaml.</summary>
    private static string Brush(string theme, string key)
    {
        var doc = System.Xml.Linq.XDocument.Load(RepoFile("src/Syn3Updater.App/App.axaml"));
        var dict = doc.Descendants(Ax + "ResourceDictionary").Single(d => (string?)d.Attribute(X + "Key") == theme && d.Elements(Ax + "SolidColorBrush").Any());
        return dict.Elements(Ax + "SolidColorBrush").Single(b => (string?)b.Attribute(X + "Key") == key).Value.Trim();
    }

    [Theory]
    [InlineData("Dark")] [InlineData("Light")]
    public void A_required_tool_has_its_own_outline_and_background_in_both_themes_different_from_a_ticked_package(string theme)
    {
        var border = Brush(theme, "ToolBorder"); var bg = Brush(theme, "ToolBg");
        Assert.Matches("^#[0-9a-fA-F]{6,8}$", border); Assert.Matches("^#[0-9a-fA-F]{6,8}$", bg);
        Assert.NotEqual(Brush(theme, "Brand"), border); Assert.NotEqual(Brush(theme, "BrandSoft"), bg);          // not the teal of a ticked package
        Assert.NotEqual(Brush(theme, "CardBorder"), border);                                                       // and not the plain outline either
    }

    [Fact]
    public void The_style_for_tool_tiles_comes_after_the_selected_style_and_also_covers_the_pointer_over_state()
    {
        var app = File.ReadAllText(RepoFile("src/Syn3Updater.App/App.axaml"));
        var selected = app.IndexOf("Button.tile.selected /template/", StringComparison.Ordinal);
        var tool = app.IndexOf("Button.tile.tool /template/", StringComparison.Ordinal);
        Assert.True(selected > 0 && tool > selected, "the tool style must come after the selected style or the teal wins");
        Assert.Contains("Button.tile.tool:pointerover /template/", app);
        var line = app[tool..app.IndexOf("</Style>", tool, StringComparison.Ordinal)];
        Assert.Contains("ToolBg", line); Assert.Contains("ToolBorder", line);
    }

    [Fact]
    public void The_package_tile_gets_the_tool_class_from_the_row_flag()
    {
        var view = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/HomeView.axaml"));
        Assert.Contains("Classes.tool=\"{Binding IsRequiredTool}\"", view);
        Assert.Contains("Classes.selected=\"{Binding Selected}\"", view);                                          // the existing ticked look is still there
    }
}
