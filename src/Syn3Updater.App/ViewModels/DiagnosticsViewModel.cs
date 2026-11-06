using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

/// <summary>One line on the Diagnostics page. Localized text is resolved when it is created and again when the language changes.</summary>
public class DiagRow
{
    public DiagRow(DiagResult r) { Result = r; }
    public DiagResult Result { get; }
    public string Title => Ctx.T("Diag." + Result.Id);
    public string Detail => Result.Detail;
    public bool HasDetail => Result.Detail.Length > 0;
    public string Hint => Result.Status is DiagStatus.Pass or DiagStatus.Info ? "" : Fix();
    public bool HasHint => Hint.Length > 0;
    public string StatusText => Ctx.T(Result.Status switch { DiagStatus.Pass => "Diag.Pass", DiagStatus.Warn => "Diag.Warn", DiagStatus.Fail => "Diag.Fail", _ => "Diag.Info" });
    public string IconData => Result.Status switch
    {
        DiagStatus.Pass => "M21,7L9,19L3.5,13.5L4.91,12.09L9,16.17L19.59,5.59L21,7Z",
        DiagStatus.Warn => "M13,14H11V10H13M13,18H11V16H13M1,21H23L12,2L1,21Z",
        DiagStatus.Fail => "M19,6.41L17.59,5L12,10.59L6.41,5L5,6.41L10.59,12L5,17.59L6.41,19L12,13.41L17.59,19L19,17.59L13.41,12L19,6.41Z",
        _ => "M11,9H13V7H11M12,20C7.59,20 4,16.41 4,12C4,7.59 7.59,4 12,4C16.41,4 20,7.59 20,12C20,16.41 16.41,20 12,20M12,2A10,10 0 0,0 2,12A10,10 0 0,0 12,22A10,10 0 0,0 22,12A10,10 0 0,0 12,2M11,17H13V11H11V17Z",
    };
    public string Color => Result.Status switch { DiagStatus.Pass => "#4cc38a", DiagStatus.Warn => "#f0b45a", DiagStatus.Fail => "#e5645b", _ => "#93a3a9" };
    public IBrush IconBrush => new SolidColorBrush(Avalonia.Media.Color.Parse(Color));
    public IBrush IconBackground { get { var c = Avalonia.Media.Color.Parse(Color); return new SolidColorBrush(Avalonia.Media.Color.FromArgb(0x33, c.R, c.G, c.B)); } }

    private string Fix()
    {
        var key = "Diag." + Result.Id + ".Fix";
        var text = Ctx.T(key);
        return text == key ? "" : text;
    }

    /// <summary>English, language-independent line for the copy-to-clipboard report (so support can read it).</summary>
    public string ReportLine => $"[{Result.Status.ToString().ToUpperInvariant(),-4}] {Result.Id}{(Result.Detail.Length > 0 ? " - " + Result.Detail : "")}";
}

public partial class DiagnosticsViewModel : PageViewModel
{
    public override string TitleKey => "Main.Diagnostics";
    public override bool IsNavVisible => false;
    [RelayCommand] private Task BackAsync() => MainWindowViewModel.Instance.GoToAsync(MainWindowViewModel.Instance.About);
    public override string IconData => "M19.5,3.5L18,2L16.5,3.5L15,2L13.5,3.5L12,2L10.5,3.5L9,2L7.5,3.5L6,2V16H4V18A4,4 0 0,0 8,22H20A2,2 0 0,0 22,20V2L19.5,3.5M18,20H8A2,2 0 0,1 6,18V18H16V4H18V20M10,7H14V9H10V7M10,11H14V13H10V11Z";

    public ObservableCollection<DiagRow> Rows { get; } = new();
    public ObservableCollection<KeyValuePair<string, string>> Facts { get; } = new();
    [ObservableProperty] private bool _running;
    [ObservableProperty] private bool _hasRun;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasProblems;
    private CancellationTokenSource? _cts;

    public DiagnosticsViewModel()
    {
        Ctx.Loc.LanguageChanged += () =>
        {
            var old = Rows.ToList(); Rows.Clear();
            foreach (var r in old) Rows.Add(new DiagRow(r.Result));
            UpdateSummary();
        };
    }

    public override void OnShown() { if (!HasRun && !Running) _ = RunAsync(); }
    public override void OnLeaving() { _cts?.Cancel(); }

    [RelayCommand]
    public async Task RunAsync()
    {
        if (Running) return;
        Running = true; Summary = Ctx.T("Diag.Running");
        _cts = new CancellationTokenSource();
        try
        {
            Facts.Clear();
            foreach (var f in Ctx.SystemFacts()) Facts.Add(f);
            var results = await Task.Run(() => Ctx.Diagnostics.RunAsync(_cts.Token));
            Rows.Clear();
            foreach (var r in results) Rows.Add(new DiagRow(r));
            HasRun = true;
            var summary = ReportText();
            var problems = results.Where(r => r.Status is DiagStatus.Warn or DiagStatus.Fail).Select(r => $"{r.Status} {r.Id}").ToList();
            Ctx.Log.Info("Diagnostics: " + (problems.Count == 0 ? "all checks passed" : string.Join("; ", problems)));
            Ctx.Log.Debug("Diagnostics report:" + Environment.NewLine + summary);
        }
        catch (OperationCanceledException) { }
        finally { Running = false; UpdateSummary(); }
    }

    private void UpdateSummary()
    {
        int pass = Rows.Count(r => r.Result.Status == DiagStatus.Pass), warn = Rows.Count(r => r.Result.Status == DiagStatus.Warn),
            fail = Rows.Count(r => r.Result.Status == DiagStatus.Fail);
        HasProblems = warn + fail > 0;
        Summary = Rows.Count == 0 ? "" : Ctx.T("Diag.Summary", pass, warn, fail);
    }

    public string ReportText() =>
        "Syn3 Updater diagnostics" + Environment.NewLine + SystemInfo.ToText(Facts, Environment.NewLine) + Environment.NewLine +
        $"Profile: {Ctx.Main.Profile} (version {Ctx.Profile.VersionString}, region {Ctx.Profile.CurrentRegion ?? "-"}, nav {Ctx.Profile.CurrentNav}){Environment.NewLine}" +
        $"Language: {Ctx.Loc.Code}{Environment.NewLine}{Environment.NewLine}" +
        string.Join(Environment.NewLine, Rows.Select(r => r.ReportLine));

    [RelayCommand] private Task CopyReportAsync() => Dialogs.SetClipboard(ReportText());
    [RelayCommand] private Task OpenDataFolderAsync() => Dialogs.OpenUrl(Ctx.Paths.DataDir);
}
