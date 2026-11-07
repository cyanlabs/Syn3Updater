using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

/// <summary>One row of the Interrogator result: a label and its value ("—" when the log has none).</summary>
public record FactItem(string Label, string Value, bool Mono);
/// <summary>A partition: its path, "21M free of 3.7G", and how full it is (0 to 1) for the bar.</summary>
public record PartitionRow(string Path, string Summary, double Used);
/// <summary>An as-built block: its id (DE00) and value, dimmed when the unit reports none.</summary>
public record AsBuiltRow(string Id, string Value, bool Blank) { public double Opacity => Blank ? 0.45 : 1; }

public partial class UtilityViewModel : PageViewModel
{
    public override string TitleKey => "Main.Utility";
    public override string IconData => "M22.7,19L13.6,9.9C14.5,7.6 14,4.9 12.1,3C10.1,1 7.1,0.6 4.7,1.7L9,6L6,9L1.6,4.7C0.4,7.1 0.9,10.1 2.9,12.1C4.8,14 7.5,14.5 9.8,13.6L18.9,22.7C19.3,23.1 19.9,23.1 20.3,22.7L22.6,20.4C23.1,20 23.1,19.3 22.7,19Z";

    public ObservableCollection<DriveInfoModel> Drives { get; } = new();
    [ObservableProperty] private DriveInfoModel? _selectedDrive;
    [ObservableProperty] private bool _buttonsEnabled = true;
    [ObservableProperty] private string _driveInfo = "";

    [ObservableProperty] private string _logDetails = "";
    [ObservableProperty] private string _logPartitions = "";
    [ObservableProperty] private string _logAsBuilt = "";
    [ObservableProperty] private bool _hasLog;
    [ObservableProperty] private string _resVin = "", _resModel = "", _resType = "", _resSize = "", _resFree = "", _resVersion = "";
    [ObservableProperty] private string _apimModelInput = "";

    // the result as it is shown: the verdict, the facts, and the lists behind the expanders
    [ObservableProperty] private string _resVerdict = "";
    [ObservableProperty] private Avalonia.Media.IBrush _resBrush = Avalonia.Media.Brushes.Gray;
    [ObservableProperty] private Avalonia.Media.IBrush _resBackground = Avalonia.Media.Brushes.Transparent;
    public ObservableCollection<FactItem> Facts { get; } = new();
    public ObservableCollection<PartitionRow> PartitionRows { get; } = new();
    public ObservableCollection<string> PackageRows { get; } = new();
    public ObservableCollection<AsBuiltRow> AsBuiltRows { get; } = new();

    private InterrogatorResult? _parsed;

    public UtilityViewModel() { Ctx.DriveMonitor.Changed += OnDrivesChanged; Ctx.Loc.LanguageChanged += () => { OnPropertyChanged(nameof(PrepareLabel)); OnPropertyChanged(nameof(PrepareTip)); OnPropertyChanged(nameof(UploadTip)); }; }

    public override void OnShown() => _ = RefreshAsync();

    /// <summary>The Prepare USB buttons are grey until a drive is selected (and while another job is being started).</summary>
    public bool CanPrepare => ButtonsEnabled && SelectedDrive != null;
    partial void OnButtonsEnabledChanged(bool value) => RaisePrepare();

    /// <summary>A grey button explains itself: it says what is missing instead of "Prepare USB".</summary>
    public string PrepareLabel => Ctx.T(SelectedDrive != null ? "App.PrepareUsb" : "App.SelectUsbFirst");
    /// <summary>Tooltip for a disabled Prepare button (null, i.e. none, once it is usable).</summary>
    public string? PrepareTip => SelectedDrive == null ? Ctx.T("App.SelectUsbTip") : null;
    public string? UploadTip => HasLog ? null : Ctx.T("App.OpenLogFirstTip");
    partial void OnHasLogChanged(bool value) => OnPropertyChanged(nameof(UploadTip));

    private void RaisePrepare()
    {
        OnPropertyChanged(nameof(CanPrepare)); OnPropertyChanged(nameof(PrepareLabel)); OnPropertyChanged(nameof(PrepareTip));
    }

    public bool CanEjectSelected => SelectedDrive is { IsFolder: false, IsChooser: false };

    [RelayCommand]
    private async Task EjectSelectedAsync()
    {
        if (SelectedDrive is not { IsFolder: false, IsChooser: false } drive) return;
        if (await EjectFlow.RunAsync(drive, drive.MountPath)) { SelectedDrive = null; await RefreshAsync(); }
    }

    partial void OnSelectedDriveChanged(DriveInfoModel? value)
    {
        OnPropertyChanged(nameof(CanEjectSelected)); RaisePrepare();
        if (value?.Encrypted == true) { _ = Dialogs.Error(Ctx.T("MessageBox.EncryptedDrive")); SelectedDrive = null; return; }
        DriveInfo = value == null ? "" : $"{value.PartitionType} {value.FileSystem} · {Format.Bytes(value.SizeBytes)}";
    }

    /// <summary>The drives changed by themselves (plugged in or removed): update the list in place and keep the selection on the same stick.</summary>
    private void OnDrivesChanged(IReadOnlyList<DriveInfoModel> found)
    {
        var before = SelectedDrive;
        var result = DriveListMerger.Merge(Drives, found);
        if (before == null) return;
        var now = result.Current(before);
        if (!ReferenceEquals(now, before)) SelectedDrive = now;
        if (now == null) DriveInfo = Ctx.T("App.DriveRemoved");
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            var prev = SelectedDrive?.DeviceId;
            var found = await Ctx.Drives.ListAsync();
            Drives.Clear(); foreach (var d in found) Drives.Add(d);
            SelectedDrive = Drives.FirstOrDefault(d => d.DeviceId == prev);
        }
        catch (Exception e) { Ctx.Log.Error(e.Message); await Dialogs.Error(e.Message); }
    }

    // ---------- special USBs ----------
    [RelayCommand] private Task GracenotesAsync() => RunSpecialAsync(SpecialPackages.GraceNotesRemoval, UpdateAction.GracenotesRemoval, Ctx.T("App.TaskGracenotes"));
    [RelayCommand] private Task SmallVoiceAsync() => RunSpecialAsync(SpecialPackages.SmallVoice, UpdateAction.VoiceShrinker, Ctx.T("App.TaskVoice"));
    [RelayCommand] private Task DowngradeAsync() => RunSpecialAsync(SpecialPackages.DowngradeApp, UpdateAction.Downgrade, Ctx.T("App.TaskDowngrade"));
    [RelayCommand] private Task Upgrade22Async() => RunSpecialAsync(SpecialPackages.Upgrade22App, UpdateAction.Upgrade22, Ctx.T("App.TaskUpgrade22"));
    [RelayCommand] private Task RwDataCleanerAsync() => RunSpecialAsync(null, UpdateAction.RwDataCleaner, Ctx.T("App.TaskRwCleaner"), rwOnly: true);
    [RelayCommand] private Task CreateLogUsbAsync() => PrepareLogUsbAsync(SelectedDrive, UpdateAction.LogUtility);

    private async Task RunSpecialAsync(string? package, UpdateAction action, string name, bool rwOnly = false)
    {
        if (SelectedDrive == null) { await Dialogs.Error(Ctx.T("Utility.SelectUSBDrive")); return; }
        ButtonsEnabled = false;
        try
        {
            await Workflow.StartAsync(new JobSpec
            {
                Action = action, Mode = InstallModes.Autoinstall, Release = name, Drive = SelectedDrive,
                SpecialPackage = package, NeedRwCleaner = rwOnly,
            });
        }
        finally { ButtonsEnabled = true; }
    }

    /// <summary>The interrogator tool depends on the installed Sync version.</summary>
    public static string LogToolFor(int version) => version switch
    {
        < 3200000 => SpecialPackages.LogToolAA,
        < 3400000 => SpecialPackages.LogToolAB,
        <= 3420237 => SpecialPackages.LogToolAC,   // upstream: 3.4.18437 - 3.4.20237 (earlier 3.4.x use the same tool)
        _ => SpecialPackages.LogToolAD,            // 3.4.20282+
    };

    public async Task PrepareLogUsbAsync(DriveInfoModel? drive, UpdateAction action)
    {
        if (drive == null) { await Dialogs.Error(Ctx.T("Utility.SelectUSBDrive")); return; }
        ButtonsEnabled = false;
        try
        {
            await Workflow.StartAsync(new JobSpec
            {
                Action = action, Mode = InstallModes.Autoinstall, Release = Ctx.T("App.TaskInterrogator"), Drive = drive,
                SpecialPackage = LogToolFor(Ctx.Profile.CurrentVersion ?? 0), NeedRwCleaner = false,
            });
        }
        finally { ButtonsEnabled = true; }
    }

    // ---------- interrogator log ----------
    [RelayCommand]
    private async Task ParseLogAsync()
    {
        var path = await Dialogs.PickFile(Ctx.T("Utility.PickInterrogatorTitle"), Ctx.T("Utility.PickInterrogatorFilter"), "*.xml");
        if (path == null) { await Dialogs.Info(Ctx.T("MessageBox.My20CheckCancelled")); return; }
        try
        {
            File.Copy(path, Path.Combine(Ctx.Main.LogPath!, Path.GetFileName(path)), true);
            var r = Interrogator.Parse(await File.ReadAllTextAsync(path));
            _parsed = r;

            bool? logIsMy20 = null;                                    // what THIS log says; null = could not be decided
            // No model in the log means the unit cannot be told apart from an MY20 one, and a wrong "not MY20" can brick the APIM: assume MY20, whatever the server says
            if (!r.ModelKnown) { logIsMy20 = true; Ctx.Profile.My20v2 = true; }
            else try
            {
                logIsMy20 = (await Ctx.Api.GetMy20Models()).Any(m => r.ApimModel.StartsWith(m.Model));
                Ctx.Profile.My20v2 = logIsMy20;                                  // the log that was read last decides, in both directions (a log with no model never gets here: see above)
            }
            catch (ApiException) { /* offline: leave MY20 state alone */ }

            string? friendly = null;
            try
            {
                friendly = await Ctx.Api.LookupVersion(r.SoftwareName);
                if (friendly != null && friendly != Ctx.Profile.VersionString &&
                    await Dialogs.Confirm(Ctx.T("MessageBox.UpdateCurrentVersionUtility", Ctx.Profile.VersionString, friendly)))
                    Ctx.Profile.CurrentVersion = int.Parse(friendly.Replace(".", ""));
            }
            catch (ApiException) { /* no internet: ignore like the original */ }
            Ctx.Save();

            if (r.ApimSizeGb == 0) { await Dialogs.Error(Ctx.T("MessageBox.LogUtilityIncompatibleVersion", friendly ?? "")); return; }
            (LogDetails, LogPartitions, LogAsBuilt) = Interrogator.Describe(r, friendly);
            CrashReporter.CurrentVin = r.Vin;       // kept for a crash report, which is only sent if the user agrees
            ResVin = r.Vin; ResModel = r.ApimModel; ResType = r.Nav ? "Navigation" : "Non-Navigation";
            ResSize = $"{r.ApimSizeGb} GB"; ResFree = r.ApimFree; ResVersion = friendly is null ? "" : $"{friendly} ({r.SoftwareName})";
            ShowResult(r, friendly, logIsMy20);
            HasLog = true;
            // the popup is about the log that was just opened
            await Dialogs.Info(!r.ModelKnown ? Ctx.T("MessageBox.LogUtilityNoModelFound") + "\n\n" + Ctx.T("Utility.ModelUnknownProtected")
                : logIsMy20 switch { true => Ctx.T("MessageBox.My20Found"), false => Ctx.T("MessageBox.My20NotFound"), null => Ctx.T("Utility.ResultMy20Unknown") });
        }
        catch (InterrogatorException e) when (e.Message == "MODEL_NOT_FOUND") { await Dialogs.Error(Ctx.T("MessageBox.LogUtilityNoModelFound")); }
        catch (InterrogatorException) { await Dialogs.Error(Ctx.T("MessageBox.LogUtilityInvalidFile")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Error(e.Message); }
    }

    /// <summary>Fills what the result card shows. The verdict is about the log that was opened (blue = it could not be decided, e.g. offline).</summary>
    private void ShowResult(InterrogatorResult r, string? friendly, bool? logIsMy20)
    {
        var (text, colour) = logIsMy20 switch
        {
            // red = MY20 (Reformat and Downgrade are dangerous), green = not MY20 (everything is allowed), amber = nobody can tell from this log, blue = could not be decided
            true when !r.ModelKnown => (Ctx.T("Utility.ResultModelUnknown"), "#e8a23a"),
            true => (Ctx.T("Utility.ResultMy20"), "#ef6b62"),
            false => (Ctx.T("Utility.ResultNotMy20"), "#3fb97a"),
            null => (Ctx.T("Utility.ResultMy20Unknown"), "#5b9de6"),
        };
        var c = Avalonia.Media.Color.Parse(colour);
        ResVerdict = text; ResBrush = new Avalonia.Media.SolidColorBrush(c);
        ResBackground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x24, c.R, c.G, c.B));

        string Or(string? s) => string.IsNullOrWhiteSpace(s) ? "—" : s;
        Facts.Clear();
        Facts.Add(new(Ctx.T("App.Model"), Or(r.ApimModel), true));
        Facts.Add(new("VIN", Or(r.Vin), true));
        Facts.Add(new(Ctx.T("Utility.APIMType"), r.Nav ? "Navigation" : "Non-Navigation", false));
        Facts.Add(new(Ctx.T("Utility.APIMSize"), $"{r.ApimSizeGb} GB", false));
        Facts.Add(new(Ctx.T("Utility.APIMFree"), Or(r.ApimFree), false));
        Facts.Add(new(Ctx.T("App.Version"), friendly is null ? Or(r.SoftwareName) : $"{friendly} ({r.SoftwareName})", false));

        PartitionRows.Clear();
        foreach (var x in Interrogator.Usage(r)) PartitionRows.Add(new(x.Path, Ctx.T("Utility.FreeOf", x.Free, x.Total), x.Used));
        PackageRows.Clear();
        foreach (var p in Interrogator.PackageList(r)) PackageRows.Add(p);
        AsBuiltRows.Clear();
        foreach (var d in r.AsBuilt)
        {
            var v = d.Response.Trim();
            AsBuiltRows.Add(new(d.Value, v.Length == 0 ? "—" : v.ToUpperInvariant(), v.Length == 0 || v.Equals("Blank", StringComparison.OrdinalIgnoreCase)));
        }
    }

    [RelayCommand]
    private Task CopyAsBuiltAsync() =>
        Dialogs.SetClipboard($"{LogDetails}{Environment.NewLine}{Environment.NewLine}{LogPartitions}{Environment.NewLine}{LogAsBuilt}");

    [RelayCommand]
    private async Task ViewAsBuiltAsync()
    {
        if (_parsed?.AsBuiltXml == null) return;
        if (!await Dialogs.Confirm(Ctx.T("MessageBox.AsBuiltVinWarning"), yes: Ctx.T("String.Upload"), no: Ctx.T("Download.CancelButton"))) return;
        try
        {
            var url = await Ctx.Api.UploadAsBuilt(_parsed.AsBuiltXml.ToString(), _parsed.ApimModel, _parsed.Nav, _parsed.ApimSizeGb, _parsed.Vin);
            if (url != null) await Dialogs.OpenUrl(url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) { await Dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private async Task CheckMy20Async()
    {
        var model = ApimModelInput.Trim();
        if (!Regex.IsMatch(model, "^[a-zA-Z0-9]{4}-14G371-[a-zA-Z0-9]{2,3}$"))
        {
            await Dialogs.Error(Ctx.T("Utility.My20InvalidModel")); return;
        }
        try
        {
            var matches = (await Ctx.Api.GetMy20Models()).Where(m => model.StartsWith(m.Model, StringComparison.OrdinalIgnoreCase)).Select(m => m.Model).ToList();
            await Dialogs.Info(matches.Count > 0
                ? Ctx.T("Utility.My20Detected", model, string.Join("\n", matches))
                : Ctx.T("Utility.My20NotDetected", model), Ctx.T("Utility.My20DetectionTitle"));
        }
        catch (ApiException e) { await Dialogs.Error(e.Message); }
    }

    [RelayCommand]
    private async Task UploadLogAsync()
    {
        string? file = null;
        if (SelectedDrive is { MountPath.Length: > 0 } d && File.Exists(Path.Combine(d.MountPath, "log.txt"))) file = Path.Combine(d.MountPath, "log.txt");
        file ??= await Dialogs.PickFile(Ctx.T("Utility.PickLogTitle"), Ctx.T("Utility.PickLogFilter"), "*.txt");
        if (file != null) await DownloadViewModel.UploadAsync(await File.ReadAllTextAsync(file));
    }

    [RelayCommand] private Task TroubleshootAsync() =>
        Dialogs.OpenUrl("https://community.cyanlabs.net/t/tutorial-sync-3-4-non-nav-apim-failure-to-update-to-newer-version-sync-3-4/1984");
}
