using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public partial class QueueItem : ObservableObject
{
    public string Name { get; }
    public string Size { get; }
    public int Order { get; }
    [ObservableProperty] private bool _isActive;
    [ObservableProperty] private bool _isDone;
    public QueueItem(string name, string size, int order = 0) { Name = name; Size = size; Order = order; }
}

public partial class DownloadViewModel : PageViewModel
{
    public override string TitleKey => "Main.Downloads";
    public override string IconData => "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z";

    /// <summary>Files still to do in the current phase (the active one first). Finished files move to <see cref="Completed"/>.</summary>
    public ObservableCollection<QueueItem> Queue { get; } = new();
    public ObservableCollection<QueueItem> Completed { get; } = new();
    [ObservableProperty] private string _phaseLabel = "";
    private string _phase = "";
    private List<QueueItem> _all = new();

    // ---- speed / ETA: one meter fed with the bytes moved over ALL connections, so it is the true overall rate ----
    private readonly ThroughputMeter _meter = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _remainingBytes;
    private int _connections;
    private Avalonia.Threading.DispatcherTimer? _tick;
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _connectionsText = "";
    public bool HasStats => SpeedText.Length > 0 || EtaText.Length > 0 || ConnectionsText.Length > 0;
    partial void OnSpeedTextChanged(string value) => OnPropertyChanged(nameof(HasStats));
    partial void OnEtaTextChanged(string value) => OnPropertyChanged(nameof(HasStats));
    partial void OnConnectionsTextChanged(string value) => OnPropertyChanged(nameof(HasStats));

    /// <summary>Recomputes speed and ETA. Also runs on a timer so the numbers fall to nothing while a checksum is verified.</summary>
    internal void RefreshStats(long? nowTicks = null)
    {
        var now = nowTicks ?? _clock.Elapsed.Ticks;
        var speed = _meter.BytesPerSecond(now);
        SpeedText = TransferText.Speed(speed);
        EtaText = speed >= 1 && _remainingBytes > 0 ? Ctx.T("App.TimeLeft", TransferText.Eta(_meter.Eta(_remainingBytes, now))) : "";
        ConnectionsText = _connections > 1 ? Ctx.T("App.ConnectionsN", _connections) : _connections == 1 ? Ctx.T("App.ConnectionsOne") : "";
    }

    internal void ApplyTransfer(RunProgress p, long? nowTicks = null)
    {
        var now = nowTicks ?? _clock.Elapsed.Ticks;
        if (p.Phase.Length > 0 && p.Phase != _phaseForMeter) { _phaseForMeter = p.Phase; _meter.Reset(); }
        _meter.Add(p.TransferredBytes, now);
        _remainingBytes = p.RemainingBytes; _connections = p.Connections;
        RefreshStats(now);
    }
    private string _phaseForMeter = "";

    // ---- eject ----
    private DriveInfoModel? _ejectDrive; private string? _ejectPath;
    [ObservableProperty] private bool _canEject;
    [ObservableProperty] private bool _ejecting;
    public bool CanPressEject => CanEject && !Ejecting;
    partial void OnCanEjectChanged(bool value) => OnPropertyChanged(nameof(CanPressEject));
    partial void OnEjectingChanged(bool value) => OnPropertyChanged(nameof(CanPressEject));

    [RelayCommand]
    public async Task EjectAsync()
    {
        if (_ejectDrive == null || Ejecting) return;
        Ejecting = true;
        try { if (await EjectFlow.RunAsync(_ejectDrive, _ejectPath)) { CanEject = false; await MainWindowViewModel.Instance.Home.RefreshDrivesAsync(); } }
        finally { Ejecting = false; }
    }

    /// <summary>Completion message; when a real drive was written it also offers to eject it right away.</summary>
    private async Task CompletedAsync(string message)
    {
        if (!CanEject) { await Dialogs.Info(message); return; }
        var choice = await Dialogs.Show(message, Ctx.T("String.Notice"), new[] { Ctx.T("String.OK"), Ctx.T("App.EjectDrive") }, 0);
        if (choice == 1) await EjectAsync();
    }
    [ObservableProperty] private string _fileCounter = "";
    [ObservableProperty] private string _currentFile = "";
    private int _activeIndex = -1;
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _fileProgress;
    [ObservableProperty] private int _totalProgress;
    [ObservableProperty] private bool _isRunning;
    public bool IsIdle => !IsRunning && Queue.Count == 0 && Completed.Count == 0;

    /// <summary>The Downloads tab only appears while a download is running or after one has run (until the app is restarted).</summary>
    public override bool IsNavVisible => !IsIdle;
    public bool HasQueue => Queue.Count > 0;
    public bool HasCompleted => Completed.Count > 0;
    partial void OnIsRunningChanged(bool value) { OnPropertyChanged(nameof(IsIdle)); RaiseNavVisible(); }
    [ObservableProperty] private string _headline = "";

    private CancellationTokenSource? _cts;
    private readonly List<string> _runLog = new();

    public DownloadViewModel()
    {
        Queue.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(HasQueue)); RaiseNavVisible(); };
        Completed.CollectionChanged += (_, _) => { OnPropertyChanged(nameof(IsIdle)); OnPropertyChanged(nameof(HasCompleted)); RaiseNavVisible(); };
        Ctx.Log.LineAdded += line =>
        {
            if (!IsRunning) return;
            Dispatcher.UIThread.Post(() => { _runLog.Add(line); Log += line + Environment.NewLine; });
        };
    }

    [RelayCommand]
    private void Cancel() { _cts?.Cancel(); Status = "Cancelling..."; }

    public async Task RunAsync(UpdateRequest req, JobSpec spec)
    {
        Workflow.Busy = true; IsRunning = true;
        _cts = new CancellationTokenSource();
        _runLog.Clear(); Log = ""; FileProgress = 0; TotalProgress = 0;
        Headline = $"{spec.Release} - {spec.Region} - {spec.MapVersion}".Trim(' ', '-');
        _activeIndex = -1; _phase = ""; _phaseForMeter = ""; _meter.Reset(); _remainingBytes = 0; _connections = 0; RefreshStats();
        CanEject = false; _ejectDrive = null; _ejectPath = null;
        _tick ??= new Avalonia.Threading.DispatcherTimer(TimeSpan.FromMilliseconds(500), Avalonia.Threading.DispatcherPriority.Background, (_, _) => RefreshStats());
        _tick.Start();
        _all = req.Queue.Select((i, n) => new QueueItem(i.FileName, Format.Bytes(i.FileSize), n)).ToList();
        ResetQueue(); PhaseLabel = "";
        Ctx.Log.Info($"Install mode: {req.InstallMode}");

        var progress = new Progress<RunProgress>(p =>
        {
            if (!IsRunning) return;                                       // Progress<T> posts asynchronously: a report may arrive after the run ended
            if (!string.IsNullOrEmpty(p.Status)) Status = p.Status;
            FileProgress = p.FilePercent; TotalProgress = p.TotalPercent;
            ApplyTransfer(p);
            if (p.Phase.Length > 0 && p.Phase != _phase)
            {
                _phase = p.Phase; _activeIndex = -1; ResetQueue();            // downloading -> copying: every file is to do again
                PhaseLabel = Ctx.T(_phase == "copy" ? "App.PhaseCopy" : "App.PhaseDownload");
            }
            if (p.CurrentFile != null)
            {
                CurrentFile = p.CurrentFile;
                var active = _all.FirstOrDefault(x => x.Name == p.CurrentFile);
                if (active != null && active.Order != _activeIndex)
                {
                    _activeIndex = active.Order;
                    foreach (var done in Queue.Where(x => x.Order < _activeIndex).ToList()) { Queue.Remove(done); done.IsActive = false; done.IsDone = true; Completed.Add(done); }
                    foreach (var q in Queue) q.IsActive = q.Order == _activeIndex;
                    FileCounter = Ctx.T("App.FileOf", _activeIndex + 1, _all.Count);
                }
            }
        });

        bool ok;
        try { ok = await Task.Run(() => Ctx.Runner.RunAsync(req, progress, _cts.Token)); }
        catch (Exception e) { Ctx.Log.Error(e.ToString()); ok = false; await Dialogs.Error(e.Message); }

        IsRunning = false; Workflow.Busy = false;
        _tick?.Stop(); _meter.Reset(); _remainingBytes = 0; SpeedText = ""; EtaText = ""; ConnectionsText = "";
        bool cancelled = _cts.IsCancellationRequested;
        if (!ok)
        {
            Status = cancelled ? "Cancelled" : "Failed";
            if (!cancelled && Ctx.Runner.LastError is { } err)
                await Dialogs.Error(err.StartsWith("Unable to validate") ? Ctx.T("MessageBox.FailedToValidate3", err.Split(' ')[3]) : err);
            Return(spec);
            return;
        }

        foreach (var q in Queue.ToList()) { Queue.Remove(q); q.IsActive = false; q.IsDone = true; Completed.Add(q); }
        Status = Ctx.T("String.Completed");
        FileProgress = 100; TotalProgress = 100;
        await FinishAsync(req, spec);
    }

    private async Task FinishAsync(UpdateRequest req, JobSpec spec)
    {
        if (req.DownloadOnly)
        {
            await Dialogs.Info(Ctx.T("MessageBox.DownloadOnlyComplete"), Ctx.T("String.DownloadComplete"));
            Return(spec); return;
        }

        var root = Ctx.Runner.TargetRoot ?? req.Drive!.MountPath;   // the drive may have been remounted under a new name by the format
        if (req.Drive is { IsFolder: false } stick) { _ejectDrive = stick; _ejectPath = root; CanEject = true; }
        var isUtilityLog = spec.Action is UpdateAction.LogUtility or UpdateAction.LogUtilityMy20;
        if (!isUtilityLog)
        {
            var upload = await Dialogs.Confirm(Ctx.T("MessageBox.UploadLog"), yes: Ctx.T("String.Upload"));
            await WriteLogAsync(req, root, upload);
        }

        switch (spec.Action)
        {
            case UpdateAction.Main:
                // Open the CyanLabs instructions for this install mode straight away (before the dialog is dismissed).
                var docsUrl = InstallDocsUrl(req.InstallMode);
                Ctx.Log.Info("Opening instructions: " + docsUrl);
                await Dialogs.OpenUrl(docsUrl);
                if (req.Drive.IsFolder)
                {
                    await Dialogs.OpenUrl(root);
                    await Dialogs.Info(Ctx.T("MessageBox.CompletedFolder"));
                }
                else await CompletedAsync(Ctx.T("MessageBox.Completed"));
                MainWindowViewModel.Instance.Home.ClearSelections();
                break;
            case UpdateAction.LogUtility:
                await CompletedAsync(Ctx.T("MessageBox.LogUtilityComplete"));
                break;
            case UpdateAction.LogUtilityMy20:
                await CompletedAsync(Ctx.T("MessageBox.LogUtilityCompleteMy20"));
                break;
            default:
                await CompletedAsync(Ctx.T("MessageBox.GenericUtilityComplete"));
                break;
        }
        Return(spec);
    }

    private async Task WriteLogAsync(UpdateRequest req, string root, bool upload)
    {
        var text = LogReport.Build(Ctx.Version, Ctx.Branch, Ctx.OsName(), Ctx.Profile, req.InstallMode, req.ModeForced,
            req.Drive, root, string.Join(Environment.NewLine, _runLog));
        try
        {
            Directory.CreateDirectory(Ctx.Main.LogPath!);
            File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt"), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Error(e.Message); }
        try
        {
            // the copy on the stick is a convenience: if it cannot be written, say so but keep the local log and the upload
            if (Directory.Exists(root)) File.WriteAllText(Path.Combine(root, "log.txt"), text);
            else Ctx.Log.Error($"log.txt not written: {root} is not available");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Ctx.Log.Error("log.txt not written: " + e.Message); }
        if (upload) await UploadAsync(text);
    }

    public static async Task UploadAsync(string text)
    {
        try
        {
            var url = await Ctx.Api.UploadLog(Environment.MachineName, text);
            if (url != null) await Dialogs.OpenUrl(url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await Dialogs.Error(e.Message);
        }
    }

    /// <summary>https://cyanlabs.net/docs/syn3updater/installmodes/{autoinstall|reformat|downgrade}/</summary>
    public static string InstallDocsUrl(string mode) => ApiEndpoints.InstallDocsUrl(mode);

    private void ResetQueue()
    {
        Queue.Clear(); Completed.Clear();
        foreach (var q in _all) { q.IsActive = false; q.IsDone = false; Queue.Add(q); }
    }

    private static void Return(JobSpec spec)
    {
        if (spec.Action == UpdateAction.Main) MainWindowViewModel.Instance.ShowHome();
        else MainWindowViewModel.Instance.ShowUtility();
    }
}
