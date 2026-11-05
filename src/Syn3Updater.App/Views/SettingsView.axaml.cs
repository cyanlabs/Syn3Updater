using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cyanlabs.Syn3Updater.App.ViewModels;

namespace Cyanlabs.Syn3Updater.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // like the v2 app: the suggestions open as soon as you click into the box, and a failed load is retried then
        var box = this.FindControl<AutoCompleteBox>("VersionBox");
        if (box == null) return;
        // opening is posted, never done inside the box's own focus/pointer event, and never right after a pick (that fought the popup closing)
        var lastPick = DateTime.MinValue;
        box.SelectionChanged += (_, _) => lastPick = DateTime.UtcNow;
        void OpenSoon() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (!box.IsDropDownOpen && HasSuggestions(box) && (DateTime.UtcNow - lastPick).TotalMilliseconds > 400) box.IsDropDownOpen = true;
        }, Avalonia.Threading.DispatcherPriority.Background);
        box.GotFocus += async (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) await vm.LoadVersionsAsync();
            OpenSoon();
        };
        box.AddHandler(PointerPressedEvent, (_, _) => OpenSoon(), RoutingStrategies.Tunnel);
    }

    private bool HasSuggestions(AutoCompleteBox box) => DataContext is SettingsViewModel { KnownVersions.Count: > 0 };
}
