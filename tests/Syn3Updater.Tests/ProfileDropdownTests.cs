using Avalonia.Controls; using Avalonia.Headless.XUnit; using Avalonia.VisualTree;
using Cyanlabs.Syn3Updater.App.Services; using Cyanlabs.Syn3Updater.App.Views; using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

public class ProfileDropdownTests
{
    private static ComboBox SidebarBox(Window w) => w.GetVisualDescendants().OfType<ComboBox>().First(c => ReferenceEquals(c.ItemsSource, Cyanlabs.Syn3Updater.App.ViewModels.MainWindowViewModel.Instance.Profiles));

    [AvaloniaFact]
    public async Task NewProfile_BecomesTheSelectedValueOfTheDropdown()
    {
        using var env = new AppEnv(); var m = env.Main;
        Avalonia.Application.Current!.Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
        var window = new MainWindow { DataContext = m }; window.Show(); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var box = SidebarBox(window); Assert.Equal("default", box.SelectedItem);
        env.Dialogs.PromptAnswer = "Wife"; await m.AddProfileCommand.ExecuteAsync(null); Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Same(m.Settings, m.Current);                         // creating a profile opens Settings
        Assert.Equal("Wife", m.SelectedProfile); Assert.Equal("Wife", box.SelectedItem);
        Assert.Equal("Wife", box.SelectionBoxItem);                 // what the box actually displays, not just the view model
        box.SelectedItem = "default"; Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Equal("default", Ctx.Main.Profile); Assert.Equal("default", box.SelectedItem); Assert.Equal("default", box.SelectionBoxItem);
        window.Close();
    }
}
