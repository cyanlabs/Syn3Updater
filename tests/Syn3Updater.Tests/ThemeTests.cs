using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>Dark and Light are fixed; anything else (the System choice, nothing saved yet, an unknown value) follows the operating system.</summary>
public class ThemeTests
{
    [AvaloniaTheory]
    [InlineData("Dark", "Dark")] [InlineData("dark", "Dark")] [InlineData("Light", "Light")] [InlineData("LIGHT", "Light")]
    [InlineData("Default", "Default")] [InlineData("System", "Default")] [InlineData(null, "Default")] [InlineData("", "Default")] [InlineData("garbage", "Default")]
    public void ApplyTheme_OnlyDarkAndLightAreFixed_EverythingElseFollowsTheOs(string? saved, string expected)
    {
        Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;          // start from something else so a no-op would show
        if (expected == "Default") Application.Current.RequestedThemeVariant = ThemeVariant.Light;
        SettingsViewModel.ApplyTheme(saved);
        var want = expected switch { "Dark" => ThemeVariant.Dark, "Light" => ThemeVariant.Light, _ => ThemeVariant.Default };
        Assert.Equal(want, Application.Current.RequestedThemeVariant);
    }

    [AvaloniaTheory]
    [InlineData(null, "Default")] [InlineData("", "Default")] [InlineData("Default", "Default")] [InlineData("Auto", "Default")] [InlineData("Theme.Dark", "Default")]
    [InlineData("Dark", "Dark")] [InlineData("dark", "Dark")] [InlineData("Light", "Light")]
    public void TheSettingsPage_ShowsSystem_UnlessDarkOrLightWasChosen(string? saved, string shown)
    {
        using var env = new AppEnv(); Ctx.Main.Theme = saved;
        env.Main.Current = env.Main.Settings; env.Main.Settings.OnShown();
        Assert.Equal(shown, env.Main.Settings.Theme!.Value);
    }

    [AvaloniaFact]
    public async Task ChoosingSystem_IsSavedAndFollowsTheOs_NotDark()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; env.Main.Current = s; s.OnShown();
        s.Theme = s.ThemeOptions.Single(o => o.Value == "Dark"); await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal("Dark", Ctx.Main.Theme); Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        s.Theme = s.ThemeOptions.Single(o => o.Value == "Default"); Assert.Equal(ThemeVariant.Default, Application.Current.RequestedThemeVariant);   // applies as soon as it is picked
        await s.ApplyClickCommand.ExecuteAsync(null);
        Assert.Equal("Default", Ctx.Main.Theme);
    }
}
