using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;

namespace Cyanlabs.Syn3Updater.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Esc / Enter drive the in-window dialog; tunnel so they work wherever the focus happens to be
        AddHandler(KeyDownEvent, OnDialogKey, RoutingStrategies.Tunnel);
        DialogHostViewModel.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DialogHostViewModel.Current) && DialogHostViewModel.Instance.Current?.IsPrompt == true)
                Dispatcher.UIThread.Post(() => { var box = this.FindControl<TextBox>("PromptBox"); box?.Focus(); box?.SelectAll(); }, DispatcherPriority.Background);
        };
    }

    protected override void OnOpened(EventArgs e)
    {
        DialogHostViewModel.Instance.IsAttached = true;       // dialogs can now be drawn inside this window
        base.OnOpened(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        DialogHostViewModel.Instance.IsAttached = false;
        base.OnClosed(e);
    }

    private void OnDialogKey(object? sender, KeyEventArgs e)
    {
        var host = DialogHostViewModel.Instance;
        if (host.Current == null) return;
        if (e.Key == Key.Escape) { host.Cancel(); e.Handled = true; }
        else if (e.Key == Key.Enter) { host.AcceptDefault(); e.Handled = true; }
    }

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
