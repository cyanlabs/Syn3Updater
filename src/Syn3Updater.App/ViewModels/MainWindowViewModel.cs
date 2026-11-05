using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Avalonia.Media.Imaging;

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
    /// <summary>Sidebar pages above the divider; Settings sits at the bottom like the original.</summary>
    public ObservableCollection<PageViewModel> TopPages { get; }

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
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ArgumentException or InvalidOperationException)
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
        Pages = new() { About, Home, Utility, Downloads, Profiles, Logs, News, Settings };
        TopPages = new() { About, Home, Utility, Downloads, Profiles, Logs, News };
        _current = Home;
    }

    partial void OnCurrentChanged(PageViewModel value) => value?.OnShown();

    public void ShowHome() => Current = Home;
    public void ShowDownloads() => Current = Downloads;
    public void ShowUtility() => Current = Utility;
    public void ShowSettings() => Current = Settings;

    [RelayCommand] private void Navigate(PageViewModel page) { if (page != null) Current = page; }
}
