using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Cyanlabs.Syn3Updater.Tests;
using Xunit;

namespace Syn3Updater.Tests;

public class InstallMethodDisplayTests
{
    [AvaloniaFact]
    public void Every_kind_of_install_has_its_own_readable_text_and_a_new_kind_cannot_go_unmapped()
    {
        using var env = new AppEnv();
        var texts = new Dictionary<InstallKind, string>();
        foreach (var k in Enum.GetValues<InstallKind>()) texts[k] = InstallText.Kind(k);
        Assert.All(texts.Values, t => { Assert.False(string.IsNullOrWhiteSpace(t)); Assert.DoesNotContain("Install.Kind.", t); });          // a missing string would show its key
        Assert.Equal(texts.Count, texts.Values.Distinct().Count());                                                                          // no two kinds share a description
        foreach (var k in Enum.GetValues<InstallKind>().Where(k => k != InstallKind.Unknown))
            Assert.NotEqual(texts[InstallKind.Unknown], texts[k]);                                                                           // so none falls through to "Unknown" by accident
    }

    [AvaloniaFact]
    public void Every_update_route_has_its_own_readable_text()
    {
        using var env = new AppEnv();
        var texts = Enum.GetValues<UpdateRoute>().Select(InstallText.Route).ToList();
        Assert.All(texts, t => { Assert.False(string.IsNullOrWhiteSpace(t)); Assert.DoesNotContain("Install.Route.", t); });
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [AvaloniaTheory]
    [InlineData(InstallKind.LinuxTar, UpdateRoute.InPlace, "installed with install.sh", "updates itself")]
    [InlineData(InstallKind.LinuxDeb, UpdateRoute.Manual, ".deb package", "You update it")]
    [InlineData(InstallKind.LinuxPacman, UpdateRoute.Manual, "Arch or AUR package", "You update it")]
    [InlineData(InstallKind.WindowsFolder, UpdateRoute.InPlace, "Windows install", "updates itself")]
    [InlineData(InstallKind.WindowsPortable, UpdateRoute.Manual, "copied file", "You update it")]
    [InlineData(InstallKind.Development, UpdateRoute.None, "Development build", "Not updated by the app")]
    public void The_About_page_says_how_this_copy_was_installed_and_how_it_updates(InstallKind kind, UpdateRoute route, string kindText, string routeText)
    {
        using var env = new AppEnv();
        Ctx.Install = new InstallInfo(kind, "/some/install/folder", route, "test");
        var about = env.Main.About; about.OnShown();
        Assert.Contains(kindText, about.InstalledAs); Assert.Contains(routeText, about.UpdateMethod); Assert.Equal("/some/install/folder", about.InstallFolder);
    }

    [AvaloniaFact]
    public void The_text_follows_the_language_and_is_really_translated()
    {
        using var env = new AppEnv();
        Ctx.Install = new InstallInfo(InstallKind.LinuxDeb, "/opt/syn3updater", UpdateRoute.Manual, "test");
        var about = env.Main.About; var english = about.InstalledAs; var changed = new List<string?>();
        about.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        Ctx.Loc.SetLanguage("de");
        try
        {
            Assert.Equal("Linux, .deb-Paket", about.InstalledAs); Assert.NotEqual(english, about.InstalledAs);
            Assert.Contains(nameof(Cyanlabs.Syn3Updater.App.ViewModels.AboutViewModel.InstalledAs), changed); Assert.Contains("UpdateMethod", changed);          // the page is told to re-read them
        }
        finally { Ctx.Loc.SetLanguage("en-US"); }
    }

    [Fact]
    public void The_install_description_names_the_kind_the_reason_and_the_update_route()
    {
        var info = new InstallInfo(InstallKind.LinuxTar, "/home/u/.local/share/syn3updater", UpdateRoute.InPlace, "installed by install.sh in your own folder");
        Assert.Equal("LinuxTar: installed by install.sh in your own folder (updates: InPlace)", info.Describe());
    }

    [Fact]
    public void The_system_facts_for_Diagnostics_and_bug_reports_include_the_install_method_only_when_known()
    {
        var with = SystemInfo.Collect("3.0.0", "Stable", Path.GetTempPath(), Path.GetTempPath(), exists: _ => true, installMethod: "LinuxDeb: installed from a .deb package (updates: Manual)");
        Assert.Contains(with, f => f.Key == "Install method" && f.Value.StartsWith("LinuxDeb:"));
        Assert.DoesNotContain(SystemInfo.Collect("3.0.0", "Stable", Path.GetTempPath(), Path.GetTempPath(), exists: _ => true), f => f.Key == "Install method");
    }

    [AvaloniaFact]
    public void The_Diagnostics_page_and_the_app_log_carry_the_install_method()
    {
        using var env = new AppEnv();
        Ctx.Install = new InstallInfo(InstallKind.LinuxRpm, "/opt/syn3updater", UpdateRoute.Manual, "installed from an .rpm package");
        Assert.Contains(Ctx.SystemFacts(), f => f.Key == "Install method" && f.Value == "LinuxRpm: installed from an .rpm package (updates: Manual)");
    }
}
