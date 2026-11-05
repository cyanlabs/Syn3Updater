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
        // the version box is an editable ComboBox: pick a known version or type any other. A failed list load is retried when the box opens.
        var box = this.FindControl<ComboBox>("VersionBox");
        if (box == null) return;
        box.DropDownOpened += async (_, _) => { if (DataContext is SettingsViewModel vm) await vm.LoadVersionsAsync(); };
    }
}
