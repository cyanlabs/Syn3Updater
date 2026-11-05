using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Bindable view of the localizer: bindings re-evaluate when the language changes.</summary>
public class Loc : INotifyPropertyChanged
{
    public static Loc Instance { get; } = new();
    public event PropertyChangedEventHandler? PropertyChanged;
    public string this[string key] => Ctx.Loc[key];
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
}

/// <summary>XAML: Text="{svc:T Home.Refresh}"</summary>
public class TExtension : MarkupExtension
{
    public TExtension() { }
    public TExtension(string key) { Key = key; }
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new ReflectionBindingExtension($"[{Key}]") { Mode = BindingMode.OneWay, Source = Loc.Instance }.ProvideValue(serviceProvider);
}
