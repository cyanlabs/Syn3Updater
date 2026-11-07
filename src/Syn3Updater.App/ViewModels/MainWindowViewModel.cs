using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Cyanlabs.Syn3Updater.Core.Services.Updates;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

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
    public DiagnosticsViewModel Diagnostics { get; } = new();

    public ObservableCollection<PageViewModel> Pages { get; }
    /// <summary>Sidebar pages above the divider; Settings sits at the bottom like the original.</summary>
    public ObservableCollection<PageViewModel> TopPages { get; }
    public ObservableCollection<PageViewModel> BottomPages { get; }

    public string WindowTitle => Ctx.T("Main.WindowTitle", Ctx.Version, Ctx.Branch, Ctx.Main.Profile);
    public void RefreshTitle() => OnPropertyChanged(nameof(WindowTitle));

    // ---- new version notice, and installing it ----
    [ObservableProperty] private UpdateInfo? _update;
    /// <summary>What the app will do about <see cref="Update"/> (install it, explain how, or only tell): null until the release's signed manifest has been checked.</summary>
    [ObservableProperty] private UpdatePlan? _plan;
    [ObservableProperty] private bool _installingUpdate;
    [ObservableProperty] private double _updateProgress;
    [ObservableProperty] private string _updateStatus = "";
    private string? _dismissedVersion;

    public bool UpdateAvailable => Update != null;
    public bool CanUpdateNow => Plan?.Kind == PlanKind.Install && !InstallingUpdate;
    public bool CanShowHowTo => Plan?.Kind == PlanKind.Manual && !InstallingUpdate;
    public bool NotInstallingUpdate => !InstallingUpdate;
    public string UpdateText => Update == null ? "" : Update.Switch ? Ctx.T("App.UpdateSwitch", Update.Version, Ctx.Version, ChannelName(Ctx.UpdateChannel)) : Ctx.T("App.UpdateAvailable", Update.Version, Ctx.Version);
    /// <summary>What the banner says: the notice, or how far the installation is.</summary>
    public string UpdateBannerText => InstallingUpdate ? UpdateStatus : UpdateText;
    /// <summary>The channel's short name in the current language ("Stable", "Beta", "Nightly").</summary>
    public static string ChannelName(UpdateChannel channel) => channel switch { UpdateChannel.Beta => Ctx.T("Channel.Beta"), UpdateChannel.Nightly => Ctx.T("Channel.Nightly"), _ => Ctx.T("Channel.Stable") };
    partial void OnUpdateChanged(UpdateInfo? value) { Plan = null; OnPropertyChanged(nameof(UpdateAvailable)); OnPropertyChanged(nameof(UpdateText)); OnPropertyChanged(nameof(UpdateBannerText)); }
    partial void OnPlanChanged(UpdatePlan? value) { OnPropertyChanged(nameof(CanUpdateNow)); OnPropertyChanged(nameof(CanShowHowTo)); }
    partial void OnInstallingUpdateChanged(bool value) { OnPropertyChanged(nameof(CanUpdateNow)); OnPropertyChanged(nameof(CanShowHowTo)); OnPropertyChanged(nameof(NotInstallingUpdate)); OnPropertyChanged(nameof(UpdateBannerText)); }
    partial void OnUpdateStatusChanged(string value) => OnPropertyChanged(nameof(UpdateBannerText));

    /// <summary>
    /// Looks for a newer release and shows the banner if there is one, then checks its signed manifest to learn whether the app can install it itself.
    /// A failed check is silent: it is never worth interrupting anyone for. <paramref name="periodic"/> checks do not bring back a notice that was dismissed.
    /// </summary>
    public async Task CheckForUpdateAsync(bool periodic = false)
    {
        if (InstallingUpdate) return;
        var found = await Ctx.Updates.CheckAsync(Ctx.Version, Ctx.UpdateChannel);
        if (periodic && found != null && found.Version == _dismissedVersion) found = null;
        Update = found;
        Ctx.Log.Info(found == null ? $"No newer version than {Ctx.Version} found on the {Ctx.UpdateChannel} channel" : $"{(found.Switch ? "A build is available on the " + Ctx.UpdateChannel + " channel" : "A newer version is available")}: {found.Version}");
        if (found == null) return;
        var plan = await Ctx.UpdatePlanner.PlanAsync(found, Ctx.Install, Ctx.Version, Ctx.UpdateChannel, ReleaseManifest.CurrentOs(), ReleaseManifest.CurrentArch());
        if (!ReferenceEquals(Update, found) || InstallingUpdate) return;              // the check was repeated, or the notice dismissed, while this was working
        Plan = plan;
        Ctx.Log.Info($"Update {found.Version}: {plan.Kind}{(plan.Reason != null ? " (" + plan.Reason + ")" : "")}");
        if (plan.Kind == PlanKind.Rejected) Ctx.Log.Error($"Update {found.Version} was NOT trusted: {plan.Reason}");
    }

    /// <summary>Checks again every few hours while the app stays open, so a session left running for days still hears about a release.</summary>
    public void StartUpdateWatcher(TimeSpan? every = null)
    {
        var timer = new PeriodicTimer(every ?? TimeSpan.FromHours(6));
        _ = Task.Run(async () =>
        {
            while (await timer.WaitForNextTickAsync())
            {
                if (Workflow.Busy || InstallingUpdate) continue;
                try { await Dispatcher.UIThread.InvokeAsync(() => CheckForUpdateAsync(periodic: true)); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
            }
        });
    }

    [RelayCommand] private Task OpenUpdate() => Update == null ? Task.CompletedTask : Dialogs.OpenUrl(Update.Url);
    [RelayCommand] private void DismissUpdate() { if (InstallingUpdate) return; _dismissedVersion = Update?.Version; Update = null; }

    /// <summary>Shows how to update a copy the app does not update itself (a package manager command, or the download page).</summary>
    [RelayCommand]
    private async Task ShowHowTo()
    {
        if (Plan is not { Kind: PlanKind.Manual } plan || Update == null) return;
        var command = plan.Instructions ?? "";
        var choice = await Dialogs.Show(Ctx.T("App.UpdateHowToBody", Update.Version, command), Ctx.T("App.UpdateHowToTitle", Update.Version),
            new[] { Ctx.T("String.OK"), Ctx.T("App.UpdateCopyCommand"), Ctx.T("App.UpdateView") }, 0);
        if (choice == 1) await Dialogs.SetClipboard(command);
        else if (choice == 2) await Dialogs.OpenUrl(Update.Url);
    }

    /// <summary>Downloads, installs and restarts into the new version. Refused while a download or USB run is in progress.</summary>
    [RelayCommand]
    private async Task UpdateNow()
    {
        if (Plan is not { Kind: PlanKind.Install } plan || InstallingUpdate || plan.Manifest == null) return;
        if (Workflow.Busy) { await Dialogs.Info(Ctx.T("App.UpdateBusy")); return; }
        var version = plan.Manifest.Version;
        var notes = string.IsNullOrWhiteSpace(plan.Manifest.Notes) ? Ctx.T("App.UpdateNoNotes") : plan.Manifest.Notes.Trim();
        if (!await Dialogs.Confirm(Ctx.T("App.UpdateConfirmBody", notes), Ctx.T("App.UpdateConfirmTitle", version), Ctx.T("App.UpdateNow"), Ctx.T("App.UpdateLater"))) return;
        if (Workflow.Busy) { await Dialogs.Info(Ctx.T("App.UpdateBusy")); return; }          // started something while the question was open

        InstallingUpdate = true; Workflow.Busy = true;                       // nothing else may start while the program is being replaced
        UpdateProgress = 0; UpdateStatus = Ctx.T("App.UpdateDownloading", version, 0);
        var restarting = false;
        try
        {
            var progress = new Progress<double>(p => { UpdateProgress = p; UpdateStatus = Ctx.T("App.UpdateDownloading", version, (int)(p * 100)); });
            var exe = await Ctx.UpdateInstaller.RunAsync(plan, progress, stage =>
            {
                if (stage == UpdateInstaller.Stage.Installing) Dispatcher.UIThread.Post(() => { UpdateProgress = 1; UpdateStatus = Ctx.T("App.UpdateInstalling", version); });
            });
            Ctx.Log.Info($"Installed Syn3 Updater {version}; restarting into {exe}");
            Ctx.Main.WhatsNewVersion = version; Ctx.Main.WhatsNewNotes = plan.Manifest.Notes;
            Ctx.Save(); Ctx.Log.Save(Ctx.Paths.AppLogFile);
            UpdateStatus = Ctx.T("App.UpdateRestarting");
            if (UpdateRestart.Restart(exe)) { restarting = true; return; }
            await Dialogs.Error(Ctx.T("App.UpdateRestartFailed", version));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Ctx.Log.Error($"Update to {version} failed: {e.Message}");
            await Dialogs.Error(Ctx.T("App.UpdateFailed", e.Message));
        }
        finally
        {
            if (!restarting) { InstallingUpdate = false; Workflow.Busy = false; }
        }
    }

    /// <summary>After an update: shows the new version's release notes once. Also removes what the update left behind.</summary>
    public async Task OnStartedAsync()
    {
        if (Ctx.Install.Kind == InstallKind.LinuxTar) LinuxTarApplier.CleanUpPrevious(Ctx.Install.AppDir);
        var m = Ctx.Main;
        if (m.WhatsNewVersion == null) return;
        var version = m.WhatsNewVersion; var notes = m.WhatsNewNotes;
        m.WhatsNewVersion = null; m.WhatsNewNotes = null; Ctx.Save();                         // once only, even if the dialog is never answered
        if (version == Ctx.Version && !string.IsNullOrWhiteSpace(notes)) await Dialogs.Info(notes!.Trim(), Ctx.T("App.WhatsNewTitle", version));
    }

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

    // ---- the backend cannot be reached: a banner above everything, until a request gets through again ----
    [ObservableProperty] private bool _offline;
    public string OfflineText => Ctx.T("App.NoConnectionBanner");
    private DispatcherTimer? _retryTimer;
    private ApiClient? _watched;

    /// <summary>Follows <see cref="ApiClient.ReachabilityChanged"/> of the current API client (which tests and the app replace as a whole).</summary>
    private void WatchBackend()
    {
        if (ReferenceEquals(_watched, Ctx.Api)) return;
        if (_watched != null) _watched.ReachabilityChanged -= OnReachability;
        _watched = Ctx.Api; _watched.ReachabilityChanged += OnReachability;
    }

    private void OnReachability(bool reachable)
    {
        void Apply() { Offline = !reachable; }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    partial void OnOfflineChanged(bool value)
    {
        OnPropertyChanged(nameof(OfflineText));
        if (value)                                       // keep trying quietly, so the banner goes away by itself when the servers come back
        {
            _retryTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _retryTimer.Tick -= OnRetryTick; _retryTimer.Tick += OnRetryTick; _retryTimer.Start();
        }
        else _retryTimer?.Stop();
    }

    private async void OnRetryTick(object? sender, EventArgs e) { if (Offline) await Ctx.Api.PingAsync(); }

    [RelayCommand] private Task RetryConnection() => Ctx.Api.PingAsync();
    [RelayCommand] private Task OpenStatusPage() => Dialogs.OpenUrl(ApiEndpoints.StatusPage);

    public MainWindowViewModel()
    {
        Instance = this;
        WatchBackend();
        Ctx.Loc.LanguageChanged += () => { OnPropertyChanged(nameof(UpdateText)); OnPropertyChanged(nameof(WindowTitle)); OnPropertyChanged(nameof(OfflineText)); };
        Pages = new() { About, Home, Utility, Downloads, Logs, News, Diagnostics, Settings };
        TopPages = new() { Home, Utility, Logs, News };      // the update workflow; Diagnostics is reached from a button on About, not the sidebar
        BottomPages = new() { Downloads, Settings, About };   // Downloads comes first so appearing/disappearing never shifts the tabs above it; Settings and About stay put
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
