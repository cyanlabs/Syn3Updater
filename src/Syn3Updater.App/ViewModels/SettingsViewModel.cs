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
    public IReadOnlyList<ChoiceOption> My20Options { get; } = new[]
    {
        new ChoiceOption("auto", "Auto-Detect"), new ChoiceOption("on", "Enabled (MY20)"), new ChoiceOption("off", "Disabled / Not MY20"),
    };
    public IReadOnlyList<ChoiceOption> ModeOptions { get; } = new[]
    {
        new ChoiceOption("autodetect", "Auto-Detect"), new ChoiceOption(InstallModes.Autoinstall, "autoinstall"),
        new ChoiceOption(InstallModes.Reformat, "reformat"), new ChoiceOption(InstallModes.Downgrade, "downgrade"),
    };
    public IReadOnlyList<ChoiceOption> ThemeOptions { get; } = new[]
    {
        new ChoiceOption("Dark", "Dark"), new ChoiceOption("Light", "Light"), new ChoiceOption("Default", "System"),
    };
    public IReadOnlyList<int> ConnectionOptions { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8 };

    [ObservableProperty] private LanguageOption? _language;
    [ObservableProperty] private Region? _region;
    [ObservableProperty] private string _version = "";
    [ObservableProperty] private bool _nav;
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

    public override void OnShown() { Load(); OnPropertyChanged(nameof(WarningText)); OnPropertyChanged(nameof(ProfileName)); }
    public override void OnLeaving() { if (_dirty) _ = ApplyAsync(quiet: true); }
    private bool _dirty;
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (!_loading && e.PropertyName is not (nameof(Message) or nameof(Advanced))) _dirty = true;
    }

    private void Load()
    {
        _loading = true;
        var m = Ctx.Main; var p = Ctx.Profile;
        Language = Languages.FirstOrDefault(l => l.Code == Ctx.Loc.Code) ?? Languages.FirstOrDefault();
        Region = Regions.FirstOrDefault(r => r.Code == p.CurrentRegion);
        Version = p.CurrentVersion is > 0 ? p.VersionString : "";
        Nav = p.CurrentNav;
        My20 = My20Options[p.My20v2 switch { null => 0, true => 1, false => 2 }];
        Mode = ModeOptions.FirstOrDefault(o => o.Value == p.InstallMode) ?? ModeOptions[0];
        Theme = ThemeOptions.FirstOrDefault(o => o.Value == (m.Theme ?? "Dark")) ?? ThemeOptions[0];
        Connections = Math.Clamp(m.DownloadConnections, 1, 8);
        DownloadPath = m.DownloadPath ?? ""; LogPath = m.LogPath ?? "";
        LicenseKey = m.LicenseKey ?? "";
        Advanced = false; Message = "";
        _loading = false; _dirty = false;
    }

    partial void OnAdvancedChanged(bool value)
    {
        if (_loading || !value) return;
        _ = Task.Run(async () =>
        {
            if (!await Dialogs.Confirm(Ctx.T("MessageBox.AdvancedSettings"), Ctx.T("String.Warning") + "!", warning: true))
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Advanced = false);
        });
    }

    partial void OnLanguageChanged(LanguageOption? value)
    {
        if (_loading || value == null) return;
        OnPropertyChanged(nameof(WarningText));
        Ctx.Loc.SetLanguage(value.Code);   // preview immediately; persisted on Apply
        Loc.Instance.Refresh();
    }

    partial void OnThemeChanged(ChoiceOption? value) { if (!_loading && value != null) ApplyTheme(value.Value); }

    public static void ApplyTheme(string? name)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = name switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Dark };
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
    private Task ApplyClickAsync() => ApplyAsync(false);

    private async Task ApplyAsync(bool quiet)
    {
        // version: "3.4.20282" or "3420282" -> 7 digits
        var digits = new string(Version.Where(char.IsDigit).ToArray());
        if (Version.Trim().Length > 0 && digits.Length != 7)
        {
            Message = "Version must look like 3.4.20282 (Settings > General > About on the car)."; return;
        }
        var m = Ctx.Main; var p = Ctx.Profile;

        // download location change: offer to move what is already there
        var newDl = DownloadPath.Trim();
        if (newDl.Length > 0 && !string.Equals(newDl, m.DownloadPath, StringComparison.Ordinal))
        {
            try { Directory.CreateDirectory(newDl); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = e.Message; return; }
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
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Message = e.Message; return; }
        }

        m.DownloadConnections = Connections;
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
        MainWindowViewModel.Instance.Home.ReloadSummary();
        // only throw away the Home selection when the car configuration actually changed
        if (before != (p.CurrentRegion, p.CurrentVersion, p.CurrentNav, p.My20v2, p.InstallMode))
            MainWindowViewModel.Instance.Home.ClearSelections();
    }

    [RelayCommand]
    private async Task ResetAsync()
    {
        if (!await Dialogs.Confirm("Reset all settings (including this profile) to their defaults?", Ctx.T("String.Warning") + "!", warning: true)) return;
        Ctx.Store.Reset();
        Ctx.Loc.SetLanguage(Ctx.Main.Lang); Loc.Instance.Refresh();
        Load();
        MainWindowViewModel.Instance.Home.ReloadSummary();
    }
}
