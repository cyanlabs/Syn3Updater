using Avalonia;
using Avalonia.Controls;
using Cyanlabs.Syn3Updater.App.ViewModels;

namespace Cyanlabs.Syn3Updater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() { InitializeComponent(); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == DataContextProperty) UpdateSidebar();
        if (change.Property == BoundsProperty && Environment.GetEnvironmentVariable("SYN3_DEBUG") == "1")
            Console.WriteLine($"[layout] bounds={Bounds.Width}x{Bounds.Height} scale={RenderScaling} client={ClientSize.Width}x{ClientSize.Height}");
    }

    /// <summary>Wide windows show icon + label in the sidebar; narrow ones collapse it to icons.</summary>
    private void UpdateSidebar()
    {
        if (DataContext is MainWindowViewModel vm && Bounds.Width > 0) vm.SidebarExpanded = Bounds.Width >= 760;
    }
}
