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

public partial class PackageRow : ObservableObject
{
    public Ivsu Ivsu { get; }
    [ObservableProperty] private bool _selected = true;
    public PackageRow(Ivsu i) { Ivsu = i; }
    public string Display => $"{Ivsu.Type} - {Ivsu.Name}{(string.IsNullOrEmpty(Ivsu.Version) ? "" : " " + Ivsu.Version)}";
    public string SizeText => Format.Bytes(Ivsu.FileSize);
}

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
    public ObservableCollection<string> ConfigChips { get; } = new();

    private bool _suppress;
    private string _releaseNotes = "", _mapNotes = "";
    private bool _invalidSelection;

    public bool HasPackages => Packages.Count > 0;
    public bool HasNotes => Notes.Length > 0;
    public bool HasMode => InstallMode.Length > 0;
    public bool HasFeedback => !string.IsNullOrEmpty(FeedbackUrl);
    public bool ModeIsForced => Ctx.ModeForced;
    partial void OnNotesChanged(string value) => OnPropertyChanged(nameof(HasNotes));
    partial void OnInstallModeChanged(string value) { OnPropertyChanged(nameof(HasMode)); OnPropertyChanged(nameof(ModeIsForced)); }
    partial void OnFeedbackUrlChanged(string? value) => OnPropertyChanged(nameof(HasFeedback));

    public HomeViewModel()
    {
        Ctx.Loc.LanguageChanged += () => ReloadSummary();
        Packages.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasPackages));
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
        ConfigMissing = (p.CurrentVersion ?? 0) == 0 || string.IsNullOrEmpty(p.CurrentRegion);
        ConfigChips.Clear();
        if (!ConfigMissing)
        {
            ConfigChips.Add(p.VersionString);
            ConfigChips.Add(p.CurrentRegion!);
            ConfigChips.Add(Ctx.T(p.CurrentNav ? "Home.Nav" : "String.NonNavAPIM"));
        }
        ConfigChips.Add($"{Ctx.T("Home.MY20Protection")}: {My20Text}");
        ConfigChips.Add($"{Ctx.T("Home.Profile")}: {Ctx.Main.Profile}");
        if (SelectedRegion == null && Regions.FirstOrDefault(r => r.Code == p.CurrentRegion) is { } r0)
            SelectedRegion = r0;
    }

    /// <summary>Called after a completed run: forget the selected release/maps so the next run starts clean.</summary>
    public void ClearSelections()
    {
        _suppress = true;
        SelectedMap = null; SelectedRelease = null; Releases.Clear(); MapVersions.Clear(); Packages.Clear();
        Notes = ""; _suppress = false;
        UpdateButtons();
        if (SelectedRegion != null) _ = LoadReleasesAsync();
    }

    // ---------- drives ----------
    [RelayCommand]
    public async Task RefreshDrivesAsync()
    {
        try
        {
            var previous = SelectedDrive;
            var found = await Ctx.Drives.ListAsync();
            Drives.Clear();
            foreach (var d in found) Drives.Add(d);
            if (previous?.IsFolder == true) Drives.Add(previous);
            NoDrives = found.Count == 0 && previous?.IsFolder != true;
            SelectedDrive = Drives.FirstOrDefault(d => d.DeviceId == previous?.DeviceId && d.IsFolder == previous?.IsFolder) ;
        }
        catch (Exception e)
        {
            Ctx.Log.Error(e.Message);
            await Dialogs.Error(e.Message);
        }
    }

    [RelayCommand]
    private async Task ChooseFolderAsync()
    {
        var path = await Dialogs.PickFolder(Ctx.T("Home.NoUSBDir"));
        if (path == null) return;
        var item = new DriveInfoModel { IsFolder = true, MountPath = path, Name = $"{Ctx.T("Home.NoUSBDir")}: {path}", DeviceId = "" };
        foreach (var old in Drives.Where(d => d.IsFolder).ToList()) Drives.Remove(old);
        Drives.Add(item);
        SelectedDrive = item;
    }

    partial void OnSelectedDriveChanged(DriveInfoModel? value)
    {
        if (value?.Encrypted == true)
        {
            _ = Dialogs.Error(Ctx.T("MessageBox.EncryptedDrive"));
            SelectedDrive = null; return;
        }
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
        _releaseNotes = rel.Release?.Notes is { Length: > 0 } n ? "Release Notes:\n" + n : "";
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
        Packages.Clear();
        _mapNotes = map.Release?.Notes is { Length: > 0 } n ? "Map Release Notes:\n" + n + "\n\n" : "";
        Notes = _mapNotes + _releaseNotes;
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
                        Packages.Add(new PackageRow(ri.Ivsu.ToIvsu()));
            }
            if (map.Kind == MapChoice.MapPackage && map.Release != null)
            {
                var root = await Ctx.Api.GetMapReleaseIvsus(map.Release.Name);
                foreach (var ri in root.MapReleases?.FirstOrDefault()?.Ivsus?.Where(x => x.MapIvsu != null) ?? Enumerable.Empty<ReleaseIvsu>())
                    if (ri.MapIvsu!.Regions?.Contains("ALL") == true || ri.MapIvsu.Regions?.Contains(region.Code) == true)
                        Packages.Add(new PackageRow(ri.MapIvsu.ToIvsu()));
            }
        }
        catch (ApiException e) { await ShowApiError(e); }
        finally { Loading = false; }
        UpdateButtons();
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

    [RelayCommand]
    private Task RegionInfoAsync() =>
        Dialogs.Info(string.Join("\n", Region.All.Select(r => $"{r.Code}: {r.Name}")), Ctx.T("Home.Region"));

    [RelayCommand] private void EditConfig() => MainWindowViewModel.Instance.ShowSettings();
    [RelayCommand] private Task OpenInstructionsAsync() => Dialogs.OpenUrl(string.IsNullOrEmpty(InstallDocsUrl) ? ApiEndpoints.DocsBase : InstallDocsUrl);
    [RelayCommand] private void SelectAll() { foreach (var r in Packages) r.Selected = true; }
    [RelayCommand] private void SelectNone() { foreach (var r in Packages) r.Selected = false; }

    [RelayCommand] private Task OpenFeedbackAsync() => FeedbackUrl is { Length: > 0 } u ? Dialogs.OpenUrl(u) : Task.CompletedTask;
}
