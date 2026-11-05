using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cyanlabs.Syn3Updater.App.ViewModels;

namespace Cyanlabs.Syn3Updater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainWindowViewModel vm) vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MainWindowViewModel.Current)) UpdateActive(); };
            UpdateActive();
        };
        Loaded += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateActive, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    private void OnNavClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm || sender is not Button b) return;
        vm.Current = b == SettingsButton ? vm.Settings : (b.DataContext as PageViewModel) ?? vm.Current;
    }

    /// <summary>Highlight the sidebar button of the current page.</summary>
    private void UpdateActive()
    {
        if (DataContext is not MainWindowViewModel vm) return;
        SettingsButton.Classes.Set("active", vm.Current == vm.Settings);
        foreach (var b in this.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("nav") && b != SettingsButton))
            b.Classes.Set("active", b.DataContext == vm.Current);
    }
}
