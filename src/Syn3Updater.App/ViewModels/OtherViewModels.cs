using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.ViewModels;

public partial class ProfilesViewModel : PageViewModel
{
    public override string TitleKey => "Main.Profiles";
    public override string IconData => "M18.92,6.01C18.72,5.42 18.16,5 17.5,5H6.5C5.84,5 5.29,5.42 5.08,6.01L3,12V20A1,1 0 0,0 4,21H5A1,1 0 0,0 6,20V19H18V20A1,1 0 0,0 19,21H20A1,1 0 0,0 21,20V12L18.92,6.01M6.5,16A1.5,1.5 0 0,1 5,14.5A1.5,1.5 0 0,1 6.5,13A1.5,1.5 0 0,1 8,14.5A1.5,1.5 0 0,1 6.5,16M17.5,16A1.5,1.5 0 0,1 16,14.5A1.5,1.5 0 0,1 17.5,13A1.5,1.5 0 0,1 19,14.5A1.5,1.5 0 0,1 17.5,16M5,11L6.5,6.5H17.5L19,11H5Z";
    public ObservableCollection<string> Profiles { get; } = new();
    [ObservableProperty] private string? _selected;
    [ObservableProperty] private string _current = "";
    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _renameTo = "";

    public override void OnShown() => Reload();

    private void Reload()
    {
        Profiles.Clear();
        foreach (var p in Ctx.Store.ListProfiles()) Profiles.Add(p);
        Current = Ctx.Main.Profile;
        MainWindowViewModel.Instance?.RefreshTitle();
        Selected = Profiles.FirstOrDefault(p => p == Current);
    }

    private void Switched()
    {
        Reload();
        MainWindowViewModel.Instance.Home.ReloadSummary();
        MainWindowViewModel.Instance.Home.ClearSelections();
        MainWindowViewModel.Instance.Settings.OnShown();
    }

    [RelayCommand]
    private void Select() { if (Selected != null) { Ctx.Store.SwitchProfile(Selected); Switched(); } }

    [RelayCommand]
    private void Create()
    {
        var name = AppPaths.FileSafe(NewName.Trim());
        if (NewName.Trim().Length == 0) return;
        Ctx.Store.SwitchProfile(name); NewName = ""; Switched();
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected == null) return;
        if (Selected == Ctx.Main.Profile) { await Dialogs.Error(Ctx.T("MessageBox.InvalidProfile")); return; }
        Ctx.Store.DeleteProfile(Selected); Reload();
    }

    [RelayCommand]
    private void Rename()
    {
        var to = AppPaths.FileSafe(RenameTo.Trim());
        if (Selected == null || RenameTo.Trim().Length == 0 || to == Selected) return;
        var from = Selected;
        bool wasCurrent = from == Ctx.Main.Profile;
        var src = Path.Combine(Ctx.Paths.ProfileDir, from + ".json");
        var dst = Path.Combine(Ctx.Paths.ProfileDir, to + ".json");
        if (wasCurrent) Ctx.Save();
        if (File.Exists(src) && !File.Exists(dst)) File.Move(src, dst);
        if (wasCurrent) { Ctx.Main.Profile = to; Ctx.Save(); }
        RenameTo = ""; Reload();
        MainWindowViewModel.Instance.Home.ReloadSummary();
    }
}

public class LogFile
{
    public string Path { get; init; } = "";
    public bool IsSession { get; init; }
    public LogKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Name => System.IO.Path.GetFileName(Path);
    public string IconData => Kind switch
    {
        LogKind.Session => "M13,3A9,9 0 0,0 4,12H1L4.89,15.89L4.96,16.03L9,12H6A7,7 0 0,1 13,5A7,7 0 0,1 20,12A7,7 0 0,1 13,19C11.07,19 9.32,18.21 8.06,16.94L6.64,18.36C8.27,20 10.5,21 13,21A9,9 0 0,0 22,12A9,9 0 0,0 13,3M12,8V13L16.28,15.54L17,14.33L13.5,12.25V8H12Z",
        LogKind.Interrogator => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,9V3.5L18.5,9H13Z",
        _ => "M5,20H19V18H5M19,9H15V3H9V9H5L12,16L19,9Z",
    };
    public string Color => Kind switch { LogKind.Session => "#2fa1ab", LogKind.Interrogator => "#4f8cff", LogKind.UpdateRun => "#4cc38a", _ => "#93a3a9" };
    public Avalonia.Media.IBrush IconBrush => new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(Color));
    public Avalonia.Media.IBrush IconBackground { get { var c = Avalonia.Media.Color.Parse(Color); return new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromArgb(0x33, c.R, c.G, c.B)); } }
}

public class LogLine
{
    public string Text { get; init; } = "";
    public LogLevel Level { get; init; }
    public bool IsError => Level == LogLevel.Error;
    public bool IsWarning => Level == LogLevel.Warning;
    public bool IsSuccess => Level == LogLevel.Success;
    public bool IsDebug => Level == LogLevel.Debug;
}

public partial class LogsViewModel : PageViewModel
{
    public override string TitleKey => "Main.Logs";
    public override string IconData => "M14,2H6A2,2 0 0,0 4,4V20A2,2 0 0,0 6,22H18A2,2 0 0,0 20,20V8L14,2M13,9V3.5L18.5,9H13Z";
    public ObservableCollection<LogFile> Files { get; } = new();
    public ObservableCollection<LogLine> Lines { get; } = new();
    [ObservableProperty] private LogFile? _selected;
    [ObservableProperty] private string _path = "";
    [ObservableProperty] private string _viewTitle = "";
    [ObservableProperty] private string _viewMeta = "";
    [ObservableProperty] private bool _errorsOnly;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private int _errorCount;
    [ObservableProperty] private int _warningCount;
    [ObservableProperty] private string _fileCountText = "";
    public bool HasSavedLogs => Files.Any(f => !f.IsSession);
    public bool CanDelete => Selected is { IsSession: false };
    private string[] _raw = Array.Empty<string>();

    public override void OnShown() => Reload();

    private void Reload()
    {
        var keep = Selected?.Path;
        Path = Ctx.Main.LogPath ?? "";
        Files.Clear();
        Files.Add(new LogFile { IsSession = true, Kind = LogKind.Session, Title = "Current session", Subtitle = $"{Ctx.Log.Lines.Count} entries - live" });
        try
        {
            if (Directory.Exists(Path))
                foreach (var f in new DirectoryInfo(Path).GetFiles().Where(f => f.Extension is ".txt" or ".xml").OrderByDescending(f => f.LastWriteTime))
                {
                    var d = LogParser.Describe(f.Name);
                    Files.Add(new LogFile { Path = f.FullName, Kind = d.Kind, Title = d.Title, Subtitle = $"{f.LastWriteTime:d MMM, HH:mm}  -  {LogParser.FileSize(f.Length)}" });
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Ctx.Log.Error(e.Message); }
        FileCountText = $"{Files.Count(f => !f.IsSession)}";
        OnPropertyChanged(nameof(HasSavedLogs));
        Selected = Files.FirstOrDefault(f => f.Path == keep && keep != null) ?? Files[0];
        Load();
    }

    partial void OnSelectedChanged(LogFile? value) { OnPropertyChanged(nameof(CanDelete)); Load(); }
    partial void OnErrorsOnlyChanged(bool value) => Rebuild();
    partial void OnSearchChanged(string value) => Rebuild();

    private void Load()
    {
        if (Selected == null) { _raw = Array.Empty<string>(); Rebuild(); return; }
        try
        {
            _raw = Selected.IsSession ? Ctx.Log.Lines.TakeLast(2000).ToArray()
                : File.ReadAllText(Selected.Path).Replace("\r\n", "\n").Split('\n');
            ViewTitle = Selected.Title;
            ViewMeta = Selected.IsSession ? "Everything logged since the app started" : Selected.Subtitle;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _raw = new[] { e.Message }; }
        Rebuild();
    }

    private void Rebuild()
    {
        Lines.Clear();
        int errors = 0, warns = 0;
        foreach (var l in _raw)
        {
            var level = LogParser.Classify(l);
            if (level == LogLevel.Error) errors++; else if (level == LogLevel.Warning) warns++;
            if (ErrorsOnly && level is not (LogLevel.Error or LogLevel.Warning)) continue;
            if (Search.Length > 0 && !l.Contains(Search, StringComparison.OrdinalIgnoreCase)) continue;
            Lines.Add(new LogLine { Text = l, Level = level });
        }
        ErrorCount = errors; WarningCount = warns;
    }

    [RelayCommand] private void Refresh() => Reload();
    [RelayCommand] private Task OpenFolderAsync() => Dialogs.OpenUrl(Path);
    [RelayCommand] private Task CopyAsync() => Dialogs.SetClipboard(string.Join(Environment.NewLine, Lines.Select(l => l.Text)));
    [RelayCommand] private Task UploadAsync() => _raw.Length == 0 ? Task.CompletedTask : DownloadViewModel.UploadAsync(string.Join(Environment.NewLine, _raw));

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (Selected is not { IsSession: false } sel) return;
        if (!await Dialogs.Confirm($"Delete {sel.Title}?", Ctx.T("String.Warning") + "!", warning: true)) return;
        try { File.Delete(sel.Path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { await Dialogs.Error(e.Message); }
        Selected = null; Reload();
    }
}

public partial class NewsViewModel : PageViewModel
{
    public override string TitleKey => "Main.News";
    public override string IconData => "M20,4H4A2,2 0 0,0 2,6V18A2,2 0 0,0 4,20H20A2,2 0 0,0 22,18V6A2,2 0 0,0 20,4M4,8H20V10H4V8M4,12H20V14H4V12M4,16H14V18H4V16Z";
    public ObservableCollection<Notice> Important { get; } = new();
    public ObservableCollection<Notice> Other { get; } = new();
    public ObservableCollection<Changelog> Changelogs { get; } = new();
    [ObservableProperty] private string _status = "";
    private bool _loaded;

    public override void OnShown() { if (!_loaded) _ = LoadAsync(); }

    public const string GithubRepo = "https://github.com/CyanLabs/Syn3Updater";
    /// <summary>Opens the GitHub release page of one changelog entry (as the original app does).</summary>
    public static string ReleaseUrl(string version) => $"{GithubRepo}/releases/tag/{Uri.EscapeDataString(version.Trim())}";

    [RelayCommand] private Task VisitGithub(string? version) =>
        Dialogs.OpenUrl(string.IsNullOrWhiteSpace(version) ? GithubRepo + "/releases" : ReleaseUrl(version));

    [RelayCommand]
    private async Task LoadAsync()
    {
        Status = "Loading...";
        try
        {
            var notices = await Ctx.Api.GetNotices();
            Important.Clear(); Other.Clear();
            foreach (var n in notices.OrderByDescending(n => n.DateCreated)) (n.Important ? Important : Other).Add(n);
            var logs = await Ctx.Api.GetChangelog();
            Changelogs.Clear();
            foreach (var c in logs.Take(20)) Changelogs.Add(c);
            Status = ""; _loaded = true;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException)
        {
            Status = e.Message;
        }
    }
}

public partial class AboutViewModel : PageViewModel
{
    public override string TitleKey => "Main.About";
    public override string IconData => "M12,2A10,10 0 1,0 12,22A10,10 0 1,0 12,2M11,7H13V9H11V7M11,11H13V17H11V11Z";
    public string Version => $"Syn3 Updater {Ctx.Version} ({Ctx.Branch}) · {Ctx.OsName()}";
    [ObservableProperty] private bool _accepted = Ctx.Main.DisclaimerAccepted;

    partial void OnAcceptedChanged(bool value) { Ctx.Main.DisclaimerAccepted = value; Ctx.Save(); }

    [RelayCommand] private Task OpenForumAsync() => Dialogs.OpenUrl("https://community.cyanlabs.net");
    [RelayCommand] private Task OpenDocsAsync() => Dialogs.OpenUrl("https://cyanlabs.net/docs/syn3updater/");
    [RelayCommand] private Task OpenDonateAsync() => Dialogs.OpenUrl("https://cyanlabs.net/support-us/");
    [RelayCommand] private Task OpenFHubAsync() => Dialogs.OpenUrl("https://cyanlabs.net/fhub");
}
