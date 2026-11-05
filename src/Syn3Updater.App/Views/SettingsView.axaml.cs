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
        box.GotFocus += async (_, _) =>
        {
            if (DataContext is SettingsViewModel vm) await vm.LoadVersionsAsync();
            box.IsDropDownOpen = box.ItemsSource != null && HasSuggestions(box);
        };
        box.AddHandler(PointerPressedEvent, (_, _) => { if (!box.IsDropDownOpen && HasSuggestions(box)) box.IsDropDownOpen = true; }, RoutingStrategies.Tunnel);
    }

    private bool HasSuggestions(AutoCompleteBox box) => DataContext is SettingsViewModel { KnownVersions.Count: > 0 };
}
