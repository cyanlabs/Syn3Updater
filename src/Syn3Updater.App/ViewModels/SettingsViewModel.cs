using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public record LanguageOption(string Code, string Display)
{
    public override string ToString() => Display;
}

public record ChoiceOption(string Value, string Display)
{
    public override string ToString() => Display;
}

public partial class SettingsViewModel : PageViewModel
{
    public override string TitleKey => "Main.Settings";
    public override string IconData => "M12,15.5A3.5,3.5 0 0,1 8.5,12A3.5,3.5 0 0,1 12,8.5A3.5,3.5 0 0,1 15.5,12A3.5,3.5 0 0,1 12,15.5M19.43,12.97C19.47,12.65 19.5,12.33 19.5,12C19.5,11.67 19.47,11.34 19.43,11L21.54,9.37C21.73,9.22 21.78,8.95 21.66,8.73L19.66,5.27C19.54,5.05 19.27,4.96 19.05,5.05L16.56,6.05C16.04,5.66 15.5,5.32 14.87,5.07L14.5,2.42C14.46,2.18 14.25,2 14,2H10C9.75,2 9.54,2.18 9.5,2.42L9.13,5.07C8.5,5.32 7.96,5.66 7.44,6.05L4.95,5.05C4.73,4.96 4.46,5.05 4.34,5.27L2.34,8.73C2.21,8.95 2.27,9.22 2.46,9.37L4.57,11C4.53,11.34 4.5,11.67 4.5,12C4.5,12.33 4.53,12.65 4.57,12.97L2.46,14.63C2.27,14.78 2.21,15.05 2.34,15.27L4.34,18.73C4.46,18.95 4.73,19.03 4.95,18.95L7.44,17.94C7.96,18.34 8.5,18.68 9.13,18.93L9.5,21.58C9.54,21.82 9.75,22 10,22H14C14.25,22 14.46,21.82 14.5,21.58L14.87,18.93C15.5,18.67 16.04,18.34 16.56,17.94L19.05,18.95C19.27,19.03 19.54,18.95 19.66,18.73L21.66,15.27C21.78,15.05 21.73,14.78 21.54,14.63L19.43,12.97Z";

    public IReadOnlyList<Region> Regions => Region.All;
    public IReadOnlyList<LanguageOption> Languages { get; } =
        Localizer.AvailableCodes.Select(c => new LanguageOption(c, $"{Localizer.NativeName(c)} ({c})")).ToList();
    public IReadOnlyList<ChoiceOption> My20Options { get; private set; } = BuildMy20();
    public IReadOnlyList<ChoiceOption> ModeOptions { get; private set; } = BuildMode();
    public IReadOnlyList<ChoiceOption> ThemeOptions { get; private set; } = BuildTheme();
    public IReadOnlyList<ChoiceOption> ChannelOptions { get; private set; } = BuildChannels();
    private static ChoiceOption[] BuildChannels() => new[]
    {
        new ChoiceOption(nameof(UpdateChannel.Stable), Ctx.T("Settings.ChannelStable")), new ChoiceOption(nameof(UpdateChannel.Beta), Ctx.T("Settings.ChannelBeta")),
        new ChoiceOption(nameof(UpdateChannel.Nightly), Ctx.T("Settings.ChannelNightly")),
    };
    private static ChoiceOption[] BuildMy20() => new[]
    {
        new ChoiceOption("auto", Ctx.T("Settings.OptAuto")), new ChoiceOption("on", Ctx.T("Settings.OptMy20On")), new ChoiceOption("off", Ctx.T("Settings.OptMy20Off")),
    };

    private static ChoiceOption[] BuildMode() => new[]
    {
        new ChoiceOption("autodetect", Ctx.T("Settings.OptAuto")), new ChoiceOption(InstallModes.Autoinstall, Ctx.T("Settings.ModeAutoinstall")),
        new ChoiceOption(InstallModes.Reformat, Ctx.T("Settings.ModeReformat")), new ChoiceOption(InstallModes.Downgrade, Ctx.T("Settings.ModeDowngrade")),
    };

    private static ChoiceOption[] BuildTheme() => new[]
    {
        new ChoiceOption("Dark", Ctx.T("Settings.ThemeDark")), new ChoiceOption("Light", Ctx.T("Settings.ThemeLight")), new ChoiceOption("Default", Ctx.T("Settings.ThemeSystem")),
    };

    public IReadOnlyList<int> ConnectionOptions { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    [ObservableProperty] private LanguageOption? _language;
    [ObservableProperty] private Region? _region;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _nav;
    [ObservableProperty] private ChoiceOption? _channel;
    [ObservableProperty] private ChoiceOption? _my20;
    [ObservableProperty] private ChoiceOption? _mode;
    [ObservableProperty] private ChoiceOption? _theme;
    [ObservableProperty] private int _connections = 1;
    [ObservableProperty] private string _downloadPath = "";
    [ObservableProperty] private string _logPath = "";
    [ObservableProperty] private string _licenseKey = "";
    [ObservableProperty] private bool _advanced;
    [ObservableProperty] private string _message = "";

    private bool _loading;

    public SettingsViewModel() { Load(); }
    /// <summary>The localized warning is stored with a line break; show it as one line.</summary>
    public string WarningText => Ctx.T("Settings.Warning").Replace("\r\n", " ").Replace("\n", " ").Trim();
    public string ProfileName => Ctx.Main.Profile;

    /// <summary>Versions suggested in the version box (from the API). Empty while loading or offline; typing any version still works.</summary>
    public ObservableCollection<string> KnownVersions { get; } = new();
    private bool _loadingVersions;

    /// <summary>Loads the suggestion list once per session; call again (e.g. when the box gets focus) to retry after a failure.</summary>
    public async Task LoadVersionsAsync()
    {
        if (KnownVersions.Count > 0 || _loadingVersions) return;
        _loadingVersions = true;
        try
        {
            foreach (var v in await Ctx.Api.GetAppVersions()) KnownVersions.Add(v);
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            Ctx.Log.Debug("Version suggestions unavailable: " + e.Message);     // offline: the box is just a text box
        }
        finally { _loadingVersions = false; }
    }

    public override void OnShown() { Load(); OnPropertyChanged(nameof(WarningText)); OnPropertyChanged(nameof(ProfileName)); OnPropertyChanged(nameof(ConfigRequired)); OnPropertyChanged(nameof(SetupText)); _ = LoadVersionsAsync(); }
    public override void OnLeaving() { if (_dirty) _ = ApplyAsync(quiet: true); }
    private bool _dirty;
    public bool IsDirty => _dirty;

    /// <summary>True while the profile has no SYNC version and region yet: shows the banner and keeps Home closed.</summary>
    public bool ConfigRequired => !MainWindowViewModel.Instance.ProfileReady;
    public string SetupText => Ctx.T("App.ConfigRequired");
    /// <summary>Called when someone tries to reach Home before the profile is set up: explains what is missing in a message box.</summary>
    public async Task RequireSetupAsync()
    {
        OnPropertyChanged(nameof(ConfigRequired));
        await Dialogs.Show(Ctx.T("App.ConfigRequiredBox"), Ctx.T("App.ConfigRequiredTitle"), new[] { Ctx.T("String.OK") }, defaultIndex: 0);
    }

    /// <summary>Asked before leaving the page with unsaved changes. True = go ahead and leave; false = stay (cancelled, or the changes were not valid).</summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (!_dirty) return true;
        var pick = await Dialogs.Show(Ctx.T("Settings.UnsavedMessage"), Ctx.T("Settings.UnsavedTitle"),
            new[] { Ctx.T("Settings.KeepEditing"), Ctx.T("Settings.Discard"), Ctx.T("Settings.SaveChanges") }, defaultIndex: 2);
        switch (pick)
        {
            case 2: return await ApplyAsync(quiet: true);          // invalid input stays on the page with the reason shown
            case 1:
                Load();                                              // back to what is saved, including the language/theme previews
                Ctx.Loc.SetLanguage(Ctx.Main.Lang); Loc.Instance.Refresh(); ApplyTheme(Ctx.Main.Theme);
                OnPropertyChanged(nameof(WarningText));
                return true;
            default: return false;
        }
    }
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(Message) or nameof(Advanced) or nameof(WarningText) or nameof(ProfileName) or nameof(ConfigRequired) or nameof(SetupText) or nameof(IsActive))) _dirty = true;
    }

    private void Load()
    {
        _loading = true;
        My20Options = BuildMy20(); ModeOptions = BuildMode(); ThemeOptions = BuildTheme(); ChannelOptions = BuildChannels();          // labels in the current language
        OnPropertyChanged(nameof(My20Options)); OnPropertyChanged(nameof(ModeOptions)); OnPropertyChanged(nameof(ThemeOptions)); OnPropertyChanged(nameof(ChannelOptions));
        var m = Ctx.Main; var p = Ctx.Profile;
        Language = Languages.FirstOrDefault(l => l.Code == Ctx.Loc.Code) ?? Languages.FirstOrDefault();
        Region = Regions.FirstOrDefault(r => r.Code == p.CurrentRegion);
        Version = p.CurrentVersion is > 0 ? p.VersionString : "";
        Nav = p.CurrentNav;
        My20 = My20Options[p.My20v2 switch { null => 0, true => 1, false => 2 }];
        Mode = ModeOptions.FirstOrDefault(o => o.Value == p.InstallMode) ?? ModeOptions[0];
        Theme = ThemeOption(m.Theme);
        Connections = Math.Clamp(m.DownloadConnections, 1, 8);
        Channel = ChannelOptions.FirstOrDefault(o => o.Value == Ctx.UpdateChannel.ToString()) ?? ChannelOptions[0];
        DownloadPath = m.DownloadPath ?? ""; LogPath = m.LogPath ?? "";
        LicenseKey = m.LicenseKey ?? "";
        Advanced = false; Message = "";
        _loading = false; _dirty = false;
    }

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (_loading || value == null) return;
        OnPropertyChanged(nameof(WarningText));
        Ctx.Loc.SetLanguage(value.Code);   // preview immediately; persisted on Apply
        Loc.Instance.Refresh();
        RebuildOptions();
    }

    /// <summary>The dropdown labels are text, so they are rebuilt (keeping the selection) when the language changes.</summary>
    private void RebuildOptions()
    {
        var (my, mode, theme, channel) = (My20?.Value, Mode?.Value, Theme?.Value, Channel?.Value);
        var wasLoading = _loading; _loading = true;
        My20Options = BuildMy20(); ModeOptions = BuildMode(); ThemeOptions = BuildTheme(); ChannelOptions = BuildChannels();
        OnPropertyChanged(nameof(My20Options)); OnPropertyChanged(nameof(ModeOptions)); OnPropertyChanged(nameof(ThemeOptions)); OnPropertyChanged(nameof(ChannelOptions));
        My20 = My20Options.FirstOrDefault(o => o.Value == my) ?? My20Options[0];
        Mode = ModeOptions.FirstOrDefault(o => o.Value == mode) ?? ModeOptions[0];
        Theme = ThemeOption(theme);
        Channel = ChannelOptions.FirstOrDefault(o => o.Value == channel) ?? ChannelOptions[0];
        _loading = wasLoading;
    }

    partial void OnThemeChanged(ChoiceOption? value) { if (!_loading && value != null) ApplyTheme(value.Value); }

    /// <summary>The dropdown entry for a saved theme: Dark and Light as saved (v2 saved those two words), anything else, including nothing saved yet, is System (follow the OS).</summary>
    private ChoiceOption ThemeOption(string? saved) =>
        ThemeOptions.FirstOrDefault(o => o.Value != "Default" && string.Equals(o.Value, saved, StringComparison.OrdinalIgnoreCase)) ?? ThemeOptions.First(o => o.Value == "Default");

    /// <summary>Dark and Light are fixed; everything else (System, nothing saved, an unknown value) follows the operating system's light/dark setting.</summary>
    public static void ApplyTheme(string? name)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = name?.ToLowerInvariant() switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
    }

    [RelayCommand]
    private async Task PickDownloadPathAsync()
    {
        var p = await Dialogs.PickFolder(Ctx.T("Settings.DownloadLocation"));
        if (p != null) DownloadPath = p;
    }

    [RelayCommand]
    private async Task PickLogPathAsync()
    {
        var p = await Dialogs.PickFolder(Ctx.T("Settings.LogLocation"));
        if (p != null) LogPath = p;
    }

    [RelayCommand]
    private async Task ApplyClickAsync()
    {
        // a successful Apply takes you to Home, ready to pick a drive and version; errors keep you here so you can read and fix them
        if (!await ApplyAsync(false)) return;
        OnPropertyChanged(nameof(ConfigRequired));
        if (MainWindowViewModel.Instance.ProfileReady) MainWindowViewModel.Instance.ShowHome();
        else await RequireSetupAsync();                       // applied, but still no region/version: stay here and say why
    }

    /// <returns>True when the settings were applied; false when validation or a folder error stopped it (the reason is in <see cref="Message"/>).</returns>
    private async Task<bool> ApplyAsync(bool quiet)
    {
        // version: "3.4.20282" or "3420282" -> 7 digits
        var digits = new string(Version.Where(char.IsDigit).ToArray());
        if (Version.Trim().Length > 0 && digits.Length != 7)
        {
            Message = Ctx.T("Settings.VersionFormat"); return false;
        }
        var m = Ctx.Main; var p = Ctx.Profile;

        // download location change: offer to move what is already there
        var newDl = DownloadPath.Trim();
        if (newDl.Length > 0 && !string.Equals(newDl, m.DownloadPath, StringComparison.Ordinal))
        {
            try { Directory.CreateDirectory(newDl); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = e.Message; return false; }
            if (m.DownloadPath != null && Directory.Exists(m.DownloadPath) && Directory.EnumerateFiles(m.DownloadPath).Any() &&
                await Dialogs.Confirm(Ctx.T("MessageBox.DownloadPathChangeCopy", m.DownloadPath, newDl)))
            {
                foreach (var f in Directory.GetFiles(m.DownloadPath))
                    try { File.Move(f, Path.Combine(newDl, Path.GetFileName(f)), true); } catch (IOException) { }
            }
            m.DownloadPath = newDl;
        }
        if (LogPath.Trim().Length > 0)
        {
            try { Directory.CreateDirectory(LogPath.Trim()); m.LogPath = LogPath.Trim(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = e.Message; return false; }
        }

        m.DownloadConnections = Connections;
        var channelBefore = Ctx.UpdateChannel;
        m.UpdateChannel = Channel?.Value ?? m.UpdateChannel;
        // the Launcher installs whatever channel its prefs file names (v2 wrote it from this same setting), so keep that file in step
        var branch = LauncherPrefs.ToBranch(Channels.Parse(m.UpdateChannel, Ctx.Version));
        if (LauncherPrefs.HasLauncher(Ctx.LauncherPrefsPath) && LauncherPrefs.ReadBranch(Ctx.LauncherPrefsPath) != branch)
            Ctx.Log.Info(LauncherPrefs.WriteBranch(Ctx.LauncherPrefsPath, branch) ? $"Told the Launcher to follow the {(branch == LauncherPrefs.Stable ? "Stable" : "Beta")} channel" : "Could not update the Launcher's channel (launcherPrefs.json)");
        if (Ctx.UpdateChannel != channelBefore) _ = MainWindowViewModel.Instance.CheckForUpdateAsync();       // the notice should follow the new channel straight away
        m.Lang = Language?.Code ?? m.Lang;
        m.Theme = Theme?.Value ?? "Default";
        m.LicenseKey = LicenseKey.Trim();
        var before = (p.CurrentRegion, p.CurrentVersion, p.CurrentNav, p.My20v2, p.InstallMode);
        p.CurrentRegion = Region?.Code;
        p.CurrentVersion = digits.Length == 7 ? int.Parse(digits) : 0;
        p.CurrentNav = Nav;
        p.My20v2 = My20?.Value switch { "on" => true, "off" => false, _ => null };
        p.InstallMode = Mode?.Value ?? "autodetect";
        Ctx.Save();
        Ctx.Log.Info($"Settings applied - Region: {p.CurrentRegion} - Version: {p.VersionString} - Navigation: {p.CurrentNav}");
        _dirty = false;
        Message = quiet ? "" : Ctx.T("String.Completed");
        OnPropertyChanged(nameof(ConfigRequired));
        MainWindowViewModel.Instance.Home.ReloadSummary();
        // pressing Apply always starts the Home choices afresh (region, version, maps - not the USB drive); leaving the tab only does so
        // when the car configuration actually changed, because then the old choices no longer make sense
        if (!quiet || before != (p.CurrentRegion, p.CurrentVersion, p.CurrentNav, p.My20v2, p.InstallMode))
            MainWindowViewModel.Instance.Home.ClearSelections(includeRegion: true);
        return true;
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await Dialogs.Confirm(Ctx.T("Settings.ResetConfirm"), Ctx.T("String.Warning") + "!", warning: true)) return;
        Ctx.Store.Reset();
        Ctx.Loc.SetLanguage(Ctx.Main.Lang); Loc.Instance.Refresh();
        Load();
        MainWindowViewModel.Instance.Home.ReloadSummary();
    }
}
