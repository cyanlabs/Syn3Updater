using CommunityToolkit.Mvvm.ComponentModel;
using Cyanlabs.Syn3Updater.App.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    protected PageViewModel() { Ctx.Loc.LanguageChanged += () => OnPropertyChanged(nameof(Title)); }

    /// <summary>Language key for the sidebar title.</summary>
    public abstract string TitleKey { get; }
    public string Title => Ctx.T(TitleKey);
    /// <summary>SVG path data (24x24) for the sidebar icon.</summary>
    public abstract string IconData { get; }
    public virtual void OnShown() { }
}
