using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.Views;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class AdvancedOptionsTests
{
    [AvaloniaFact]
    public void Ticking_Advanced_ShowsAnInlineWarning_NotAMessageBox()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown(); env.Dialogs.Messages.Clear();
        var view = new SettingsView { DataContext = s }; var window = new Window { Content = view }; window.Show();
        var warning = view.FindControl<TextBlock>("AdvancedWarning")!;
        Assert.False(warning.IsEffectivelyVisible);                                                  // hidden until the box is ticked

        s.Advanced = true;
        Assert.True(warning.IsEffectivelyVisible); Assert.Contains("don't change them unless you know what you are doing", warning.Text);
        Assert.True(s.Advanced);                                                                     // no confirmation that could switch it back off
        Assert.Empty(env.Dialogs.Messages);                                                          // and no popup

        s.Advanced = false; Assert.False(warning.IsEffectivelyVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheWarningIsOrange_LikeTheCarConfigurationOne()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.Advanced = true;
        var view = new SettingsView { DataContext = s }; var window = new Window { Content = view }; window.Show();
        var advanced = view.FindControl<TextBlock>("AdvancedWarning")!;
        var carWarning = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(view).OfType<TextBlock>().First(t => t.Text == s.WarningText);
        Assert.Equal(((Avalonia.Media.ISolidColorBrush)carWarning.Foreground!).Color, ((Avalonia.Media.ISolidColorBrush)advanced.Foreground!).Color);     // same orange token
        window.Close(); await Task.CompletedTask;
    }
}
