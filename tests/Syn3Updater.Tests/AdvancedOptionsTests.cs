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

    [AvaloniaFact]
    public void BothOrangeWarnings_ShareOneStyle()
    {
        using var env = new AppEnv(); var s = env.Main.Settings; s.OnShown();
        var view = new SettingsView { DataContext = s }; var window = new Window { Content = view }; window.Show();
        var car = view.FindControl<TextBlock>("CarWarning")!; var advanced = view.FindControl<TextBlock>("AdvancedWarning")!;
        Assert.Equal(s.WarningText, car.Text);
        Assert.All(new[] { car, advanced }, t => Assert.Contains("warning", t.Classes));
        Assert.Equal(car.FontSize, advanced.FontSize); Assert.Equal(car.FontWeight, advanced.FontWeight); Assert.Equal(car.TextWrapping, advanced.TextWrapping);
        Assert.Equal(12.5, advanced.FontSize); Assert.Equal(Avalonia.Media.FontWeight.SemiBold, advanced.FontWeight);                 // the Advanced options font, now used by both
        Assert.Equal(((Avalonia.Media.ISolidColorBrush)car.Foreground!).Color, ((Avalonia.Media.ISolidColorBrush)advanced.Foreground!).Color);
        window.Close();
    }
}
