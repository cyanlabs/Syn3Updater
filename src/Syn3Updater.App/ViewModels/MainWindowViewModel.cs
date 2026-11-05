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
        // update the list in place: clearing it while the dropdown is mid-selection wiped the displayed value
        var wanted = Ctx.Store.ListProfiles().ToList();
        for (var i = Profiles.Count - 1; i >= 0; i--) if (!wanted.Contains(Profiles[i])) Profiles.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            if (i < Profiles.Count && Profiles[i] == wanted[i]) continue;
            var at = Profiles.IndexOf(wanted[i]);
            if (at >= 0) Profiles.Move(at, i); else Profiles.Insert(i, wanted[i]);
        }
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
        Home.ReloadSummary(); Home.ClearSelections(includeRegion: true); Settings.OnShown();
        if (!ProfileReady && Current == Home) ShowSettings();      // switched to a profile that is not set up yet
    }

    [RelayCommand]
    private async Task AddProfile()
    {
        var name = await Dialogs.Prompt(Ctx.T("App.NewProfileTitle"), Ctx.T("Profiles.NewProfile"));
        if (string.IsNullOrWhiteSpace(name)) return;
        Ctx.Store.SwitchProfile(AppPaths.FileSafe(name.Trim()));
        ProfileChanged();
        ShowSettings();                         // a new profile starts empty: take them straight to its car configuration
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

    public DialogHostViewModel Dialog => DialogHostViewModel.Instance;

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
        AdVisible = !string.IsNullOrWhiteSpace(m.AdImageUrl);          // no placeholder: the area only exists once an ad image is configured
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
        TopPages = new() { Home, Utility, Logs, News, About };
        BottomPages = new() { Downloads, Settings };          // Downloads sits just above Settings: appearing/disappearing never shifts the tabs above it
        ReloadProfiles();
        _current = ProfileReady ? Home : Settings;       // a fresh profile starts on Settings, because Home cannot work without a version and region
        _current.IsActive = true;
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

    public void ShowHome() { if (ProfileReady) Current = Home; else { Current = Settings; _ = Settings.RequireSetupAsync(); } }
    public void ShowDownloads() => Current = Downloads;
    public void ShowUtility() => Current = Utility;
    public void ShowSettings() => Current = Settings;

    /// <summary>Switches page, first asking about unsaved Settings changes. Returns false when the user chose to stay.</summary>
    public async Task<bool> GoToAsync(PageViewModel page)
    {
        if (page == null || page == Current) return true;
        if (Current is SettingsViewModel s && !await s.ConfirmLeaveAsync()) return false;
        if (page == Home && !ProfileReady)               // Home needs a region and version: send them to Settings instead
        {
            if (Current != Settings) Current = Settings;
            await Settings.RequireSetupAsync();
            return false;
        }
        Current = page; return true;
    }

    /// <summary>True once the active profile has a SYNC version and region; Home is not reachable before that.</summary>
    public bool ProfileReady => (Ctx.Profile.CurrentVersion ?? 0) > 0 && !string.IsNullOrEmpty(Ctx.Profile.CurrentRegion);

    [RelayCommand] private Task Navigate(PageViewModel page) => GoToAsync(page);
}
