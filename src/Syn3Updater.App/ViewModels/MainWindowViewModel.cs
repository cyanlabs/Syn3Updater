using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Avalonia.Media.Imaging;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    public static MainWindowViewModel Instance { get; private set; } = null!;

    // ---- profile switcher (lives in the sidebar) ----
    public ObservableCollection<string> Profiles { get; } = new();
    [ObservableProperty] private string? _selectedProfile;
    public bool CanDeleteProfile => Profiles.Count > 1;
    private bool _syncingProfile;

    public void ReloadProfiles()
    {
        _syncingProfile = true;
        Profiles.Clear();
        foreach (var p in Ctx.Store.ListProfiles()) Profiles.Add(p);
        SelectedProfile = Profiles.FirstOrDefault(p => p == Ctx.Main.Profile);
        _syncingProfile = false;
        OnPropertyChanged(nameof(CanDeleteProfile)); OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (_syncingProfile || value == null || value == Ctx.Main.Profile) return;
        Ctx.Store.SwitchProfile(value);
        ProfileChanged();
    }

    private void ProfileChanged()
    {
        ReloadProfiles();
        Home.ReloadSummary(); Home.ClearSelections(); Settings.OnShown();
    }

    [RelayCommand]
    private async Task AddProfile()
    {
        var name = await Dialogs.Prompt(Ctx.T("App.NewProfileTitle"), Ctx.T("Profiles.NewProfile"));
        if (string.IsNullOrWhiteSpace(name)) return;
        Ctx.Store.SwitchProfile(AppPaths.FileSafe(name.Trim()));
        ProfileChanged();
    }

    [RelayCommand]
    private async Task DeleteProfile()
    {
        if (!CanDeleteProfile || SelectedProfile is not { } doomed) return;
        if (!await Dialogs.Confirm(Ctx.T("App.DeleteProfileConfirm", doomed), Ctx.T("String.Warning") + "!", warning: true)) return;
        if (doomed == Ctx.Main.Profile)      // deleting the active profile: move to another one first
            Ctx.Store.SwitchProfile(Profiles.First(x => x != doomed));
        Ctx.Store.DeleteProfile(doomed);
        ProfileChanged();
    }

    public HomeViewModel Home { get; } = new();
    public DownloadViewModel Downloads { get; } = new();
    public UtilityViewModel Utility { get; } = new();
    public NewsViewModel News { get; } = new();
    public LogsViewModel Logs { get; } = new();
    public SettingsViewModel Settings { get; } = new();
    public AboutViewModel About { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; }
    /// <summary>Sidebar pages above the divider; Settings sits at the bottom like the original.</summary>
    public ObservableCollection<PageViewModel> TopPages { get; }
    public ObservableCollection<PageViewModel> BottomPages { get; }

    public string WindowTitle => $"Syn3 Updater {Ctx.Version} ({Ctx.Branch}) - Current Profile {Ctx.Main.Profile}";
    public void RefreshTitle() => OnPropertyChanged(nameof(WindowTitle));

    // ---- ad slot ----
    [ObservableProperty] private bool _adVisible;
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? _adImage;
    [ObservableProperty] private bool _adHasImage;

    public async Task LoadAdAsync()
    {
        var m = Ctx.Main;
        AdVisible = m.ShowAdSlot || !string.IsNullOrWhiteSpace(m.AdImageUrl);
        AdImage = null; AdHasImage = false;
        if (string.IsNullOrWhiteSpace(m.AdImageUrl)) return;
        try
        {
            var bytes = await Ctx.Http.GetByteArrayAsync(m.AdImageUrl);
            AdImage = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(bytes));
            AdHasImage = true;
        }
        catch (Exception e)   // an ad that fails to load (network, bad image data) must never affect the app
        {
            Ctx.Log.Debug("Ad image not loaded: " + e.Message);
        }
    }

    [RelayCommand]
    private Task OpenAd() => string.IsNullOrWhiteSpace(Ctx.Main.AdLinkUrl) ? Task.CompletedTask : Dialogs.OpenUrl(Ctx.Main.AdLinkUrl!);

    [ObservableProperty] private PageViewModel _current;

    public MainWindowViewModel()
    {
        Instance = this;
        Pages = new() { About, Home, Utility, Downloads, Logs, News, Settings };
        TopPages = new() { Home, Utility, Downloads, Logs, News, About };
        BottomPages = new() { Settings };
        _current = Home;
        Home.IsActive = true;
        ReloadProfiles();
    }

    partial void OnCurrentChanging(PageViewModel value) { if (_current != null && _current != value) _current.OnLeaving(); }

    partial void OnCurrentChanged(PageViewModel value)
    {
        foreach (var page in Pages) page.IsActive = page == value;
        value?.OnShown();
    }

    /// <summary>Wide windows show icon + label; narrow ones collapse the sidebar to icons.</summary>
    [ObservableProperty] private bool _sidebarExpanded = true;
    public double SidebarWidth => SidebarExpanded ? 200 : 64;
    partial void OnSidebarExpandedChanged(bool value) => OnPropertyChanged(nameof(SidebarWidth));

    public void ShowHome() => Current = Home;
    public void ShowDownloads() => Current = Downloads;
    public void ShowUtility() => Current = Utility;
    public void ShowSettings() => Current = Settings;

    [RelayCommand] private void Navigate(PageViewModel page) { if (page != null) Current = page; }
}
