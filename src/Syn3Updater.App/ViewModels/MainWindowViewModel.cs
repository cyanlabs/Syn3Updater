using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public static MainWindowViewModel Instance { get; private set; } = null!;

    public HomeViewModel Home { get; } = new();
    public DownloadViewModel Downloads { get; } = new();
    public UtilityViewModel Utility { get; } = new();
    public NewsViewModel News { get; } = new();
    public LogsViewModel Logs { get; } = new();
    public ProfilesViewModel Profiles { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public AboutViewModel About { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; }

    [ObservableProperty] private PageViewModel _current;

    public MainWindowViewModel()
    {
        Instance = this;
        Pages = new() { Home, Downloads, Utility, News, Logs, Profiles, Settings, About };
        _current = Home;
    }

    partial void OnCurrentChanged(PageViewModel value) => value?.OnShown();

    public void ShowHome() => Current = Home;
    public void ShowDownloads() => Current = Downloads;
    public void ShowUtility() => Current = Utility;
    public void ShowSettings() => Current = Settings;

    [RelayCommand] private void Navigate(PageViewModel page) { if (page != null) Current = page; }
}
