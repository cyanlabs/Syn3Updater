using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Threading;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public partial class DownloadViewModel : PageViewModel
{
    public override string TitleKey => "Main.Downloads";

    public ObservableCollection<string> Queue { get; } = new();
    [ObservableProperty] private string _log = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private int _fileProgress;
    [ObservableProperty] private int _totalProgress;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _headline = "";

    private CancellationTokenSource? _cts;
    private readonly List<string> _runLog = new();

    public DownloadViewModel()
    {
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
        Queue.Clear();
        foreach (var i in req.Queue) Queue.Add(i.FileName);
        Ctx.Log.Info($"Install mode: {req.InstallMode}");

        var progress = new Progress<RunProgress>(p =>
        {
            if (!string.IsNullOrEmpty(p.Status)) Status = p.Status;
            FileProgress = p.FilePercent; TotalProgress = p.TotalPercent;
        });

        bool ok;
        try { ok = await Task.Run(() => Ctx.Runner.RunAsync(req, progress, _cts.Token)); }
        catch (Exception e) { Ctx.Log.Error(e.ToString()); ok = false; await Dialogs.Error(e.Message); }

        IsRunning = false; Workflow.Busy = false;
        bool cancelled = _cts.IsCancellationRequested;
        if (!ok)
        {
            Status = cancelled ? "Cancelled" : "Failed";
            if (!cancelled && Ctx.Runner.LastError is { } err)
                await Dialogs.Error(err.StartsWith("Unable to validate") ? Ctx.T("MessageBox.FailedToValidate3", err.Split(' ')[3]) : err);
            Return(spec);
            return;
        }

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

        var root = req.Drive!.MountPath;
        var isUtilityLog = spec.Action is UpdateAction.LogUtility or UpdateAction.LogUtilityMy20;
        if (!isUtilityLog)
        {
            var upload = await Dialogs.Confirm(Ctx.T("MessageBox.UploadLog"), yes: Ctx.T("String.Upload"));
            await WriteLogAsync(req, root, upload);
        }

        switch (spec.Action)
        {
            case UpdateAction.Main:
                if (req.Drive.IsFolder)
                {
                    await Dialogs.Info(Ctx.T("MessageBox.CompletedFolder"));
                    await Dialogs.OpenUrl(root);
                }
                else
                {
                    await Dialogs.Info(Ctx.T("MessageBox.Completed"));
                    await Dialogs.OpenUrl(ApiEndpoints.DocsBase + "installmodes/" + req.InstallMode);
                }
                MainWindowViewModel.Instance.Home.ClearSelections();
                break;
            case UpdateAction.LogUtility:
                await Dialogs.Info(Ctx.T("MessageBox.LogUtilityComplete"));
                break;
            case UpdateAction.LogUtilityMy20:
                await Dialogs.Info(Ctx.T("MessageBox.LogUtilityCompleteMy20"));
                break;
            default:
                await Dialogs.Info(Ctx.T("MessageBox.GenericUtilityComplete"));
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
            File.WriteAllText(Path.Combine(root, "log.txt"), text);
            Directory.CreateDirectory(Ctx.Main.LogPath!);
            File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, $"log-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.txt"), text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Error(e.Message); }
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

    private static void Return(JobSpec spec)
    {
        if (spec.Action == UpdateAction.Main) MainWindowViewModel.Instance.ShowHome();
        else MainWindowViewModel.Instance.ShowUtility();
    }
}
