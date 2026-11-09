using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.Views;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class AdvancedOptionsTests
{
    [AvaloniaFact]
    public void TheAdvancedWarning_IsAlwaysVisible_AndTickingNeverShowsAMessageBox()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown(); env.Dialogs.Messages.Clear();
        var view = new SettingsView { DataContext = s }; var window = new Window { Content = view }; window.Show();
        var warning = view.FindControl<TextBlock>("AdvancedWarning")!;
        Assert.True(warning.IsEffectivelyVisible);                                                   // there before the box is ticked, so it is read first
        Assert.Contains("incompatible with your car", warning.Text); Assert.Contains("may also brick your SYNC 3 unit", warning.Text);
        Assert.Contains("don't change them unless you know what you are doing", warning.Text);

        s.Advanced = true;
        Assert.True(warning.IsEffectivelyVisible); Assert.True(s.Advanced);                          // no confirmation that could switch it back off
        Assert.Empty(env.Dialogs.Messages);                                                          // and no popup
        s.Advanced = false; Assert.True(warning.IsEffectivelyVisible);
        window.Close();
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    /// <summary>The headless test app has no theme, so the styling is verified where it is defined: in the XAML.</summary>
    [Fact]
    public void BothOrangeWarnings_ShareOneStyle_WithNoLocalOverrides()
    {
        var app = System.Xml.Linq.XDocument.Load(RepoFile("src/Syn3Updater.App/App.axaml"));
        var style = app.Descendants().Single(e => e.Name.LocalName == "Style" && (string?)e.Attribute("Selector") == "TextBlock.warning");
        string Setter(string prop) => style.Elements().Single(e => (string?)e.Attribute("Property") == prop).Attribute("Value")!.Value;
        Assert.Equal("12.5", Setter("FontSize")); Assert.Equal("SemiBold", Setter("FontWeight")); Assert.Equal("{DynamicResource WarnFg}", Setter("Foreground")); Assert.Equal("Wrap", Setter("TextWrapping"));

        var settings = System.Xml.Linq.XDocument.Load(RepoFile("src/Syn3Updater.App/Views/SettingsView.axaml"));
        foreach (var name in new[] { "CarWarning", "AdvancedWarning" })
        {
            var tb = settings.Descendants().Single(e => e.Name.LocalName == "TextBlock" && e.Attributes().Any(a => a.Name.LocalName == "Name" && a.Value == name));
            Assert.Equal("warning", (string?)tb.Attribute("Classes"));
            foreach (var local in new[] { "FontSize", "FontWeight", "Foreground", "TextWrapping" }) Assert.Null(tb.Attribute(local));      // nothing here can make the two drift apart again
        }
    }

    [AvaloniaFact]
    public void TheCarWarning_ShowsTheLocalisedTextOnOneLine()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown();
        var view = new SettingsView { DataContext = s }; var window = new Window { Content = view }; window.Show();
        var car = view.FindControl<TextBlock>("CarWarning")!;
        Assert.Equal(s.WarningText, car.Text); Assert.DoesNotContain("\n", car.Text);
        window.Close();
    }
}

public class AdvancedOptionsRememberedTests
{
    [Avalonia.Headless.XUnit.AvaloniaFact]
    public void A_forced_install_mode_is_shown_when_Settings_is_opened_again_instead_of_being_hidden_behind_an_unticked_box()
    {
        using var env = new AppEnv(); var s = env.Main.Settings;
        Ctx.Profile.My20v2 = false; Ctx.Profile.InstallMode = Cyanlabs.Syn3Updater.Core.Models.InstallModes.Reformat;
        s.OnShown();
        Assert.True(s.Advanced); Assert.Equal("reformat", s.Mode!.Value); Assert.Equal("off", s.My20!.Value);      // ticked, so both are visible, and still as saved
    }

    [Avalonia.Headless.XUnit.AvaloniaTheory]
    [InlineData(null)] [InlineData(true)] [InlineData(false)]
    public void Without_a_forced_mode_the_box_stays_unticked_whatever_the_MY20_check_decided(bool? my20)
    {
        using var env = new AppEnv(); var s = env.Main.Settings;
        Ctx.Profile.My20v2 = my20; Ctx.Profile.InstallMode = Cyanlabs.Syn3Updater.Core.Models.InstallModes.AutoDetect;      // the log reader sets My20 on its own
        s.OnShown();
        Assert.False(s.Advanced);
    }
}
