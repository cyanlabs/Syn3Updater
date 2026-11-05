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
        new ChoiceOption("Default", "System"), new ChoiceOption("Light", "Light"), new ChoiceOption("Dark", "Dark"),
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
    [ObservableProperty] private string _apiToken = "";
    [ObservableProperty] private bool _advanced;
    [ObservableProperty] private string _message = "";

    private bool _loading;

    public SettingsViewModel() { Load(); }
    public override void OnShown() => Load();

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
        Theme = ThemeOptions.FirstOrDefault(o => o.Value == (m.Theme ?? "Default")) ?? ThemeOptions[0];
        Connections = Math.Clamp(p.DownloadConnections, 1, 8);
        DownloadPath = m.DownloadPath ?? ""; LogPath = m.LogPath ?? "";
        LicenseKey = m.LicenseKey ?? ""; ApiToken = m.ApiToken ?? "";
        Advanced = false; Message = "";
        _loading = false;
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
        Ctx.Loc.SetLanguage(value.Code);   // preview immediately; persisted on Apply
        Loc.Instance.Refresh();
    }

    partial void OnThemeChanged(ChoiceOption? value) { if (!_loading && value != null) ApplyTheme(value.Value); }

    public static void ApplyTheme(string? name)
    {
        if (Application.Current != null)
            Application.Current.RequestedThemeVariant = name switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
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
    private async Task ApplyAsync()
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

        m.Lang = Language?.Code ?? m.Lang;
        m.Theme = Theme?.Value ?? "Default";
        m.LicenseKey = LicenseKey.Trim();
        m.ApiToken = string.IsNullOrWhiteSpace(ApiToken) ? null : ApiToken.Trim();
        p.CurrentRegion = Region?.Code;
        p.CurrentVersion = digits.Length == 7 ? int.Parse(digits) : 0;
        p.CurrentNav = Nav;
        p.My20v2 = My20?.Value switch { "on" => true, "off" => false, _ => null };
        p.InstallMode = Mode?.Value ?? "autodetect";
        p.DownloadConnections = Connections;
        Ctx.Save();
        Ctx.Log.Info($"Settings applied - Region: {p.CurrentRegion} - Version: {p.VersionString} - Navigation: {p.CurrentNav}");
        Message = Ctx.T("String.Completed");
        MainWindowViewModel.Instance.Home.ReloadSummary();
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
