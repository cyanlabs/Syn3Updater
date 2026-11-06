using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public class ReleaseItem
{
    public Release? Release { get; init; }
    public bool OnlyMaps { get; init; }
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

public class MapItem
{
    public Release? Release { get; init; }
    public MapChoice Kind { get; init; }
    public string Display { get; init; } = "";
    public override string ToString() => Display;
}

/// <summary>How each package type is presented: friendly name, icon and colour.</summary>
public static class PackageStyle
{
    public record Info(string Label, string Icon, string Color);
    private static readonly Dictionary<string, Info> Map = new()
    {
        ["APPS"] = new("Apps", "M4,8H8V4H4V8M10,20H14V16H10V20M4,20H8V16H4V20M4,14H8V10H4V14M10,14H14V10H10V14M16,4V8H20V4H16M10,8H14V4H10V8M16,14H20V10H16V14M16,20H20V16H16V20Z", "#2fa1ab"),
        ["MAP"] = new("Navigation map", "M15,19L9,16.89V5L15,7.11M20.5,3C20.4,3 20.4,3 20.3,3L15,5.1L9,3L3.4,4.9C3.2,5 3,5.2 3,5.5V20.5A0.5,0.5 0 0,0 3.5,21C3.6,21 3.6,21 3.7,21L9,18.9L15,21L20.6,19.1C20.8,19 21,18.8 21,18.5V3.5A0.5,0.5 0 0,0 20.5,3Z", "#4cc38a"),
        ["MAP_LICENSE"] = new("Map licence", "M7,14A2,2 0 0,1 5,12A2,2 0 0,1 7,10A2,2 0 0,1 9,12A2,2 0 0,1 7,14M12.65,10C11.83,7.67 9.61,6 7,6A6,6 0 0,0 1,12A6,6 0 0,0 7,18C9.61,18 11.83,16.33 12.65,14H17V18H21V14H23V10H12.65Z", "#e0b341"),
        ["VOICE"] = new("Voice (navigation prompts)", "M12,2A3,3 0 0,1 15,5V11A3,3 0 0,1 12,14A3,3 0 0,1 9,11V5A3,3 0 0,1 12,2M19,11C19,14.53 16.39,17.44 13,17.93V21H11V17.93C7.61,17.44 5,14.53 5,11H7A5,5 0 0,0 12,16A5,5 0 0,0 17,11H19Z", "#a77bff"),
        ["GRACENOTES"] = new("Gracenotes (album info)", "M12,3V13.55C11.41,13.21 10.73,13 10,13A4,4 0 0,0 6,17A4,4 0 0,0 10,21A4,4 0 0,0 14,17V7H18V3H12Z", "#f06fa6"),
        ["ENH_DAB"] = new("DAB radio", "M20,6H8.3L16.8,2.5L16.2,1L3.2,6.4C2.5,6.7 2,7.3 2,8V20A2,2 0 0,0 4,22H20A2,2 0 0,0 22,20V8A2,2 0 0,0 20,6M7,20A3,3 0 0,1 4,17A3,3 0 0,1 7,14A3,3 0 0,1 10,17A3,3 0 0,1 7,20M20,12H18V10H16V12H4V8H20V12Z", "#4f8cff"),
        ["TOOL"] = new("Tool", "M22.7,19L13.6,9.9C14.5,7.6 14,4.9 12.1,3C10.1,1 7.1,0.6 4.7,1.7L9,6L6,9L1.6,4.7C0.4,7.1 0.9,10.1 2.9,12.1C4.8,14 7.5,14.5 9.8,13.6L18.9,22.7C19.3,23.1 19.9,23.1 20.3,22.7L22.6,20.4C23.1,20 23.1,19.3 22.7,19Z", "#f08a3c"),
    };
    private static readonly Info Fallback = new("Package", "M21,16.5C21,16.88 20.79,17.21 20.47,17.38L12.57,21.82C12.41,21.94 12.21,22 12,22C11.79,22 11.59,21.94 11.43,21.82L3.53,17.38C3.21,17.21 3,16.88 3,16.5V7.5C3,7.12 3.21,6.79 3.53,6.62L11.43,2.18C11.59,2.06 11.79,2 12,2C12.21,2 12.41,2.06 12.57,2.18L20.47,6.62C20.79,6.79 21,7.12 21,7.5V16.5Z", "#93a3a9");
    public static Info For(string? type) => type != null && Map.TryGetValue(type.ToUpperInvariant(), out var i) ? i : Fallback;
}

public partial class PackageRow : ObservableObject
{
    public Ivsu Ivsu { get; }
    [ObservableProperty] private bool _selected = true;
    public event Action? SelectionChanged;
    public PackageRow(Ivsu i)
    {
        Ivsu = i;
        var s = PackageStyle.For(i.Type);
        TypeLabel = s.Label; IconData = s.Icon;
        var c = Avalonia.Media.Color.Parse(s.Color);
        IconBrush = new Avalonia.Media.SolidColorBrush(c);
        IconBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x33, c.R, c.G, c.B));
    }
    partial void OnSelectedChanged(bool value) => SelectionChanged?.Invoke();
    [RelayCommand] private void Toggle() => Selected = !Selected;

    public string TypeLabel { get; }
    public string IconData { get; }
    public Avalonia.Media.IBrush IconBrush { get; }
    public Avalonia.Media.IBrush IconBackground { get; }
    public string Display => $"{Ivsu.Type} - {Ivsu.Name}{(string.IsNullOrEmpty(Ivsu.Version) ? "" : " " + Ivsu.Version)}";
    public string Title => Ivsu.Name;
    public string Subtitle => string.IsNullOrEmpty(Ivsu.Version) ? TypeLabel : $"{TypeLabel}  ·  {Ivsu.Version}";
    public string SizeText => Format.Bytes(Ivsu.FileSize);
}

public record ConfigItem(string Label, string Value);

public partial class HomeViewModel : PageViewModel
{
    public override string TitleKey => "Main.Home";
    public override string IconData => "M10,20V14H14V20H19V12H22L12,3L2,12H5V20Z";

    public ObservableCollection<DriveInfoModel> Drives { get; } = new();
    public IReadOnlyList<Region> Regions => Region.All;
    public ObservableCollection<ReleaseItem> Releases { get; } = new();
    public ObservableCollection<MapItem> MapVersions { get; } = new();
    public ObservableCollection<PackageRow> Packages { get; } = new();

    [ObservableProperty] private DriveInfoModel? _selectedDrive;
    [ObservableProperty] private Region? _selectedRegion;
    [ObservableProperty] private ReleaseItem? _selectedRelease;
    [ObservableProperty] private MapItem? _selectedMap;
    [ObservableProperty] private string _notes = "";
    [ObservableProperty] private string _installMode = "";
    [ObservableProperty] private string? _feedbackUrl;
    [ObservableProperty] private bool _startEnabled;
    [ObservableProperty] private bool _downloadOnlyEnabled;
    [ObservableProperty] private bool _loading;

    // Current configuration summary
    [ObservableProperty] private string _currentVersion = "";
    [ObservableProperty] private string _currentRegion = "";
    [ObservableProperty] private string _currentNav = "";
    [ObservableProperty] private string _my20Text = "";
    [ObservableProperty] private string _modeForcedText = "";
    [ObservableProperty] private string _profileName = "";
    [ObservableProperty] private string _downloadPath = "";
    [ObservableProperty] private string _driveInfo = "";
    [ObservableProperty] private bool _configMissing;
    [ObservableProperty] private bool _noDrives;
    [ObservableProperty] private string _installDocsUrl = "";
    public ObservableCollection<ConfigItem> ConfigItems { get; } = new();
    [ObservableProperty] private string _selectionSummary = "";

    // install mode presentation
    [ObservableProperty] private string _modeTitle = "";
    [ObservableProperty] private string _modeDescription = "";
    [ObservableProperty] private Avalonia.Media.IBrush _modeBrush = Avalonia.Media.Brushes.Gray;
    [ObservableProperty] private Avalonia.Media.IBrush _modeBackground = Avalonia.Media.Brushes.Transparent;
    [ObservableProperty] private string _modeIcon = "";

    private bool _suppress;
    private string _releaseNotes = "", _mapNotes = "";
    private bool _invalidSelection;

    public bool HasPackages => Packages.Count > 0;
    public bool HasNotes => Notes.Length > 0;
    public bool HasMode => InstallMode.Length > 0;
    public bool HasFeedback => !string.IsNullOrEmpty(FeedbackUrl);
    public bool ModeIsForced => Ctx.ModeForced;
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
    partial void OnInstallModeChanged(string value)
    {
        OnPropertyChanged(nameof(HasMode)); OnPropertyChanged(nameof(ModeIsForced));
        InstallDocsUrl = value.Length > 0 ? ApiEndpoints.InstallDocsUrl(value) : "";
        UpdateModePresentation();
    }

    /// <summary>
    /// With nothing selected yet the mode is unknown, unless it was forced in Settings: then it is already decided
    /// and is shown straight away instead of "pick a version and maps".
    /// </summary>
    private void ResetInstallMode() => InstallMode = Ctx.ModeForced ? Ctx.Profile.InstallMode : "";
    partial void OnFeedbackUrlChanged(string? value) => OnPropertyChanged(nameof(HasFeedback));

    public HomeViewModel()
    {
        Ctx.Loc.LanguageChanged += () => ReloadSummary();
        Ctx.DriveMonitor.Changed += OnDrivesChanged;
        Packages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPackages));
        Drives.Add(Chooser());     // "Select A Directory..." is always available, even before the first refresh
    }

    public override void OnShown() { ReloadSummary(); _ = RefreshDrivesAsync(); }

    public void ReloadSummary()
    {
        var p = Ctx.Profile;
        CurrentVersion = p.VersionString;
        CurrentRegion = p.CurrentRegion ?? "-";
        CurrentNav = Ctx.T(p.CurrentNav ? "String.Yes" : "String.No");
        My20Text = p.My20v2 switch { null => Ctx.T("String.AutoDetect"), true => Ctx.T("String.Enabled"), false => Ctx.T("String.Disabled") };
        ModeForcedText = Ctx.T(Ctx.ModeForced ? "String.Yes" : "String.No");
        ProfileName = Ctx.Main.Profile;
        DownloadPath = Ctx.Main.DownloadPath ?? "";
        if (SelectedMap == null) ResetInstallMode();
        ConfigMissing = (p.CurrentVersion ?? 0) == 0 || string.IsNullOrEmpty(p.CurrentRegion);
        ConfigItems.Clear();
        if (!ConfigMissing)
        {
            ConfigItems.Add(new(Ctx.T("Home.Version"), p.VersionString));
            ConfigItems.Add(new(Ctx.T("Home.Region"), p.CurrentRegion!));
            ConfigItems.Add(new(Ctx.T("Home.Nav"), CurrentNav));
        }
        ConfigItems.Add(new(Ctx.T("Home.MY20Protection"), My20Text));
        // the profile's region is preselected only the first time Home appears; after that an empty dropdown stays empty until the user picks
        if (!_regionPreselected)
        {
            _regionPreselected = true;
            if (SelectedRegion == null && Regions.FirstOrDefault(r => r.Code == p.CurrentRegion) is { } r0) SelectedRegion = r0;
        }
    }

    private bool _regionPreselected;

    /// <summary>
    /// Empties the release and map choices and the package list. With <paramref name="includeRegion"/> the region dropdown is
    /// emptied too (after Settings are applied or the profile changes). The USB drive selection is never touched.
    /// </summary>
    public void ClearSelections(bool includeRegion = false)
    {
        _suppress = true;
        if (includeRegion) SelectedRegion = null;
        SelectedMap = null; SelectedRelease = null; Releases.Clear(); MapVersions.Clear(); Packages.Clear();
        Notes = ""; _suppress = false;
        ResetInstallMode();
        UpdateButtons();
        if (SelectedRegion != null) _ = LoadReleasesAsync();
    }

    // ---------- drives ----------
    private DriveInfoModel? _lastSelection;
    private DriveInfoModel Chooser() => new() { IsChooser = true, Name = Ctx.T("Home.NoUSBDir") };

    /// <summary>The dropdown is: detected drives, then the chosen folder (if any), then always "Select A Directory..." last.</summary>
    private void SetDrives(IEnumerable<DriveInfoModel> drives, DriveInfoModel? folder)
    {
        Drives.Clear();
        foreach (var d in drives) Drives.Add(d);
        if (folder != null) Drives.Add(folder);
        Drives.Add(Chooser());
    }

    [RelayCommand]
    public async Task RefreshDrivesAsync()
    {
        try
        {
            var previous = _lastSelection;
            var folder = Drives.FirstOrDefault(d => d.IsFolder);
            var found = await Ctx.Drives.ListAsync();
            SetDrives(found, folder);
            NoDrives = found.Count == 0 && folder == null;
            SelectedDrive = Drives.FirstOrDefault(d => !d.IsChooser && d.DeviceId == previous?.DeviceId && d.IsFolder == previous?.IsFolder && previous != null);
        }
        catch (Exception e)
        {
            Ctx.Log.Error(e.Message);
            await Dialogs.Error(e.Message);
        }
    }

    /// <summary>The drives changed by themselves (plugged in or removed): update the list in place and keep the selection on the same stick.</summary>
    private void OnDrivesChanged(IReadOnlyList<DriveInfoModel> found)
    {
        var before = SelectedDrive;
        var result = DriveListMerger.Merge(Drives, found);
        NoDrives = !Drives.Any(d => !d.IsChooser);
        if (before is { IsFolder: false, IsChooser: false })
        {
            var now = result.Current(before);
            if (!ReferenceEquals(now, before)) SelectedDrive = now;
            if (now == null) DriveInfo = Ctx.T("App.DriveRemoved");
        }
    }

    public bool CanEjectSelected => SelectedDrive is { IsFolder: false, IsChooser: false };

    [RelayCommand]
    private async Task EjectSelectedAsync()
    {
        if (SelectedDrive is not { IsFolder: false, IsChooser: false } drive) return;
        if (await EjectFlow.RunAsync(drive, drive.MountPath)) { SelectedDrive = null; await RefreshDrivesAsync(); }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var path = await Dialogs.PickFolder(Ctx.T("Home.NoUSBDir"));
        if (path == null) { SelectedDrive = _lastSelection; return; }                  // cancelled: keep what was selected before
        var item = new DriveInfoModel { IsFolder = true, MountPath = path, Name = $"{Ctx.T("Home.NoUSBDir")}: {path}", DeviceId = "" };
        SetDrives(Drives.Where(d => !d.IsFolder && !d.IsChooser).ToList(), item);
        SelectedDrive = item;
    }

    partial void OnSelectedDriveChanged(DriveInfoModel? value)
    {
        if (value?.IsChooser == true)             // the last dropdown entry is an action, not a drive
        {
            SelectedDrive = _lastSelection;
            _ = ChooseFolderAsync();
            return;
        }
        if (value?.Encrypted == true)
        {
            _ = Dialogs.Error(Ctx.T("MessageBox.EncryptedDrive"));
            SelectedDrive = null; return;
        }
        _lastSelection = value;
        OnPropertyChanged(nameof(CanEjectSelected));
        DriveInfo = value == null ? "" : value.IsFolder ? value.MountPath
            : value.MountPath.Length > 0 ? $"Mounted at {value.MountPath}" : "Not mounted yet - it will be mounted automatically";
        if (value != null) Ctx.Log.Info($"USB Drive selected - Name: {value.Name} - FileSystem: {value.FileSystem} - PartitionType: {value.PartitionType} - Path: {value.MountPath}");
        UpdateButtons();
    }

    // ---------- release selection chain ----------
    partial void OnSelectedRegionChanged(Region? value)
    {
        if (_suppress || value == null) return;
        _ = LoadReleasesAsync();
    }

    private async Task LoadReleasesAsync()
    {
        if (SelectedRegion == null) return;
        _suppress = true;
        SelectedRelease = null; SelectedMap = null; Releases.Clear(); MapVersions.Clear(); Packages.Clear(); Notes = ""; FeedbackUrl = null;
        _suppress = false;
        ResetInstallMode();
        UpdateButtons();
        Loading = true;
        try
        {
            var root = await Ctx.Api.GetReleases(SelectedRegion.Code);
            if (Ctx.Profile.CurrentNav && Ctx.Profile.CurrentVersion >= ApiEndpoints.ReformatVersion)
                Releases.Add(new ReleaseItem { OnlyMaps = true, Display = Ctx.T("String.OnlyMaps") });
            foreach (var r in root.Releases ?? new()) Releases.Add(new ReleaseItem { Release = r, Display = r.Name });
        }
        catch (ApiException e) { await ShowApiError(e); }
        finally { Loading = false; }
    }

    private static Task ShowApiError(ApiException e)
    {
        Ctx.Log.Error(e.Message);
        return Dialogs.Error("Syn3 Updater requires a connection to the backend to function.\n\n" + e.Message);
    }

    partial void OnSelectedReleaseChanged(ReleaseItem? value)
    {
        if (_suppress || value == null) return;
        _ = LoadMapsAsync(value);
    }

    private async Task LoadMapsAsync(ReleaseItem rel)
    {
        _suppress = true;
        SelectedMap = null; MapVersions.Clear(); Packages.Clear(); _mapNotes = "";
        _suppress = false;
        _releaseNotes = rel.Release?.Notes is { Length: > 0 } n ? n : "";
        FeedbackUrl = rel.Release?.FeedbackUrl;
        Notes = _releaseNotes;
        UpdateButtons();

        var p = Ctx.Profile;
        if (!p.CurrentNav)
            MapVersions.Add(new MapItem { Kind = MapChoice.NonNavApim, Display = Ctx.T("String.NonNavAPIM") });
        else if (p.CurrentVersion >= ApiEndpoints.ReformatVersion && !rel.OnlyMaps)
            MapVersions.Add(new MapItem { Kind = MapChoice.KeepExisting, Display = Ctx.T("String.KeepExistingMaps") });

        if (p.CurrentNav)
        {
            Loading = true;
            try
            {
                var compat = rel.OnlyMaps ? "3.4" : (rel.Release?.Version ?? "3.4.00000")[..3];
                var maps = await Ctx.Api.GetMapReleases(SelectedRegion!.Code, compat);
                foreach (var m in maps.MapReleases ?? new())
                {
                    bool esnBlocked = (rel.OnlyMaps || p.My20v2 == true) && m.Esn;
                    m.IsEnabled = !esnBlocked;
                    MapVersions.Add(new MapItem { Kind = MapChoice.MapPackage, Release = m, Display = m.Name + (esnBlocked ? " (not compatible with MY20)" : "") });
                }
            }
            catch (ApiException e) { await ShowApiError(e); }
            finally { Loading = false; }
        }
    }

    partial void OnSelectedMapChanged(MapItem? value)
    {
        if (_suppress || value == null) return;
        _ = LoadPackagesAsync(value);
    }

    private async Task LoadPackagesAsync(MapItem map)
    {
        var rel = SelectedRelease; var region = SelectedRegion;
        if (rel == null || region == null) return;
        Packages.Clear(); UpdateSelectionSummary();
        _mapNotes = map.Release?.Notes is { Length: > 0 } n ? n : "";
        Notes = ComposeNotes(_releaseNotes, _mapNotes);
        ResolveInstallMode();

        Loading = true;
        try
        {
            if (!rel.OnlyMaps)
            {
                var navType = Ctx.Profile.CurrentNav ? "nav" : "nonnav";
                var root = await Ctx.Api.GetReleaseIvsus(rel.Release!.Name, navType);
                foreach (var ri in root.Releases?.FirstOrDefault()?.Ivsus?.Where(x => x.Ivsu != null) ?? Enumerable.Empty<ReleaseIvsu>())
                    if (ri.Ivsu!.Regions?.Contains("ALL") == true || ri.Ivsu.Regions?.Contains(region.Code) == true)
                        AddRow(ri.Ivsu.ToIvsu());
            }
            if (map.Kind == MapChoice.MapPackage && map.Release != null)
            {
                var root = await Ctx.Api.GetMapReleaseIvsus(map.Release.Name);
                foreach (var ri in root.MapReleases?.FirstOrDefault()?.Ivsus?.Where(x => x.MapIvsu != null) ?? Enumerable.Empty<ReleaseIvsu>())
                    if (ri.MapIvsu!.Regions?.Contains("ALL") == true || ri.MapIvsu.Regions?.Contains(region.Code) == true)
                        AddRow(ri.MapIvsu.ToIvsu());
            }
        }
        catch (ApiException e) { await ShowApiError(e); }
        finally { Loading = false; }
        UpdateButtons();
    }

    /// <summary>Release and map notes share one callout; headings are only needed when both are present.</summary>
    public static string ComposeNotes(string release, string map) =>
        release.Length > 0 && map.Length > 0 ? $"### Release notes\n{release}\n\n### Map release notes\n{map}"
        : release.Length > 0 ? release : map;

    private void AddRow(Ivsu ivsu)
    {
        var row = new PackageRow(ivsu);
        row.SelectionChanged += UpdateSelectionSummary;
        Packages.Add(row);
        UpdateSelectionSummary();
    }

    private void UpdateSelectionSummary()
    {
        var chosen = Packages.Where(r => r.Selected).ToList();
        SelectionSummary = Packages.Count == 0 ? "" : $"{chosen.Count} of {Packages.Count} selected  ·  {Format.Bytes(chosen.Sum(r => r.Ivsu.FileSize))}";
    }

    /// <summary>Title, one-line explanation, colour and icon for the install mode shown in the action bar.</summary>
    private void UpdateModePresentation()
    {
        var (title, desc, colour, icon) = InstallMode switch
        {
            InstallModes.Reformat => ("Reformat", Ctx.T("App.Mode.reformat"), "#e8a23a", "M17.65,6.35C16.2,4.9 14.21,4 12,4A8,8 0 0,0 4,12A8,8 0 0,0 12,20C15.73,20 18.84,17.45 19.73,14H17.65C16.83,16.33 14.61,18 12,18A6,6 0 0,1 6,12A6,6 0 0,1 12,6C13.66,6 15.14,6.69 16.22,7.78L13,11H20V4L17.65,6.35Z"),
            InstallModes.Downgrade => ("Downgrade", Ctx.T("App.Mode.downgrade"), "#e5645b", "M9,4H15V12H19.84L12,19.84L4.16,12H9V4Z"),
            InstallModes.Autoinstall => ("AutoInstall", Ctx.T("App.Mode.autoinstall"), "#2fa1ab", "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z"),
            _ => ("", "", "#93a3a9", ""),
        };
        var c = Avalonia.Media.Color.Parse(colour);
        ModeTitle = title; ModeDescription = desc; ModeIcon = icon;
        ModeBrush = new Avalonia.Media.SolidColorBrush(c);
        ModeBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x2e, c.R, c.G, c.B));
    }

    private void ResolveInstallMode()
    {
        if (SelectedMap == null || SelectedRelease == null) return;
        var p = Ctx.Profile;
        var res = InstallModeResolver.Resolve(p.CurrentVersion ?? 0, p.My20v2, Ctx.ModeForced, SelectedMap.Kind,
            SelectedMap.Release?.Esn ?? false, SelectedRelease.OnlyMaps);
        _invalidSelection = res.Invalid;
        if (res.Mode != null) InstallMode = res.Mode;
        if (Ctx.ModeForced) InstallMode = p.InstallMode;
        InstallDocsUrl = ApiEndpoints.InstallDocsUrl(InstallMode);
        if (res.Invalid) _ = Dialogs.Error(Ctx.T("MessageBox.MY20InvalidConfiguration"));
        Ctx.Log.Debug($"Install mode option {res.Option}: {InstallMode}");
        UpdateButtons();
    }

    public void UpdateButtons()
    {
        bool chosen = SelectedRelease != null && SelectedRegion != null && SelectedMap != null && Packages.Count > 0 && !_invalidSelection;
        DownloadOnlyEnabled = chosen;
        StartEnabled = chosen && SelectedDrive != null;
    }

    // ---------- start ----------
    [RelayCommand] private Task StartAsync() => BeginAsync(false);
    [RelayCommand] private Task DownloadOnlyAsync() => BeginAsync(true);

    private async Task BeginAsync(bool downloadOnly)
    {
        if (SelectedRelease == null || SelectedRegion == null || SelectedMap == null) return;
        var p = Ctx.Profile;
        if ((p.CurrentVersion ?? 0) == 0 || string.IsNullOrEmpty(p.CurrentRegion))
        {
            await Dialogs.Error(Ctx.T("MessageBox.NoVersionOrRegionSelected"));
            MainWindowViewModel.Instance.ShowSettings();
            return;
        }
        var selected = Packages.Where(r => r.Selected).Select(r => r.Ivsu).ToList();
        if (selected.Count == 0) { await Dialogs.Error(Ctx.T("MessageBox.NoPackagesSelected")); return; }

        if (downloadOnly && !await Dialogs.Confirm(Ctx.T("MessageBox.DownloadOnlyMode"), Ctx.T("String.Warning") + "!", warning: true)) return;
        if (!downloadOnly && SelectedDrive == null) return;

        // MY20 protection: reformat/downgrade can brick a 2020+ APIM, so make the user decide while it is still unknown.
        bool reformatLike = InstallMode is InstallModes.Reformat or InstallModes.Downgrade;
        if (reformatLike && !downloadOnly && p.My20v2 == null)
        {
            if (await Dialogs.Confirm(Ctx.T("MessageBox.My20CheckV2"), Ctx.T("String.Warning") + "!", warning: true))
            {
                await MainWindowViewModel.Instance.Utility.PrepareLogUsbAsync(SelectedDrive, UpdateAction.LogUtilityMy20);
                return;
            }
        }

        if (SelectedRegion.Code != p.CurrentRegion &&
            !await Dialogs.Confirm(Ctx.T("MessageBox.CancelRegionMismatch"), Ctx.T("String.Warning") + "!", warning: true)) return;

        if (reformatLike && !selected.Any(i => i.Type == "APPS"))
        {
            await Dialogs.Error(Ctx.T("MessageBox.CancelNoApps")); return;
        }

        var ok = await Workflow.StartAsync(new JobSpec
        {
            Packages = selected, Mode = InstallMode, Action = UpdateAction.Main, Release = SelectedRelease.Display,
            Region = SelectedRegion.Code, MapVersion = SelectedMap.Display, Drive = SelectedDrive, DownloadOnly = downloadOnly,
            NeedDowngradeTools = InstallMode == InstallModes.Downgrade, NeedReformat = reformatLike,
        });
        if (!ok) UpdateButtons();
    }

    [RelayCommand] private void EditConfig() => MainWindowViewModel.Instance.ShowSettings();
    [RelayCommand] private Task OpenInstructionsAsync() => Dialogs.OpenUrl(string.IsNullOrEmpty(InstallDocsUrl) ? ApiEndpoints.DocsBase : InstallDocsUrl);
    [RelayCommand] private void SelectAll() { foreach (var r in Packages) r.Selected = true; UpdateSelectionSummary(); }
    [RelayCommand] private void SelectNone() { foreach (var r in Packages) r.Selected = false; UpdateSelectionSummary(); }

    [RelayCommand] private Task OpenFeedbackAsync() => FeedbackUrl is { Length: > 0 } u ? Dialogs.OpenUrl(u) : Task.CompletedTask;
}
