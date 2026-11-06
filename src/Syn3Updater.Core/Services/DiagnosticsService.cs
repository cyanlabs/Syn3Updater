using System.Net.Http.Headers;
using Cyanlabs.Syn3Updater.Core.Drives;

namespace Cyanlabs.Syn3Updater.Core.Services;

public enum DiagStatus { Pass, Warn, Fail, Info }

/// <summary>
/// One diagnostic result. <paramref name="Id"/> is a stable name: the UI looks up the localized title as "Diag.&lt;Id&gt;" and,
/// for anything that is not a pass, a localized hint as "Diag.&lt;Id&gt;.Fix". <paramref name="Detail"/> is the raw value (version, path, error).
/// </summary>
public record DiagResult(string Id, DiagStatus Status, string Detail = "");

/// <summary>Checks that the things Syn3 Updater needs on this operating system are in place, without changing anything.</summary>
public class DiagnosticsService
{
    private readonly IDriveService _drives;
    private readonly HttpClient _http;
    private readonly CommandRunner _run;
    private readonly Func<string, bool> _exists;
    private readonly Func<string?> _downloadDir;
    private readonly string _dataDir;
    private readonly Func<string?> _logDir;
    private readonly bool _hasToken;
    private readonly Func<bool> _elevated;

    public const long MinFreeBytes = 4L * 1024 * 1024 * 1024;       // the largest map packages are several GB

    public DiagnosticsService(IDriveService drives, HttpClient http, string dataDir, Func<string?> downloadDir, Func<string?> logDir, bool hasToken,
        CommandRunner? run = null, Func<string, bool>? exists = null, Func<bool>? elevated = null)
    {
        _drives = drives; _http = http; _dataDir = dataDir; _downloadDir = downloadDir; _logDir = logDir; _hasToken = hasToken;
        _run = run ?? ((file, args, stdin, ct, timeout) => ProcessRunner.RunAsync(file, args, stdin, ct, timeout));
        _exists = exists ?? ProcessRunner.Exists;
        _elevated = elevated ?? (() => Environment.IsPrivilegedProcess);
    }

    public async Task<IReadOnlyList<DiagResult>> RunAsync(CancellationToken ct = default)
    {
        var r = new List<DiagResult>();

        // ---- OS specific: what formatting / mounting / ejecting need ----
        if (OperatingSystem.IsLinux()) await LinuxAsync(r, ct);
        else if (OperatingSystem.IsWindows()) await WindowsAsync(r, ct);
        else if (OperatingSystem.IsMacOS()) await MacAsync(r, ct);

        // ---- everywhere ----
        r.Add(await DriveListAsync(ct));
        r.Add(Writable("DataFolder", _dataDir, null));
        var dl = _downloadDir();
        r.Add(string.IsNullOrWhiteSpace(dl) ? new("DownloadFolder", DiagStatus.Warn, "") : Writable("DownloadFolder", dl, MinFreeBytes, "DownloadFolderSpace"));
        var logs = _logDir();
        if (!string.IsNullOrWhiteSpace(logs)) r.Add(Writable("LogFolder", logs, null));
        r.Add(_hasToken ? new("ApiToken", DiagStatus.Pass) : new("ApiToken", DiagStatus.Fail));
        r.AddRange(await NetworkAsync(ct));
        return r;
    }

    // ------------------------------------------------------------------ OS checks
    private async Task LinuxAsync(List<DiagResult> r, CancellationToken ct)
    {
        r.Add(Tool("lsblk", "Lsblk", required: true));
        r.Add(Tool("sfdisk", "Sfdisk", required: true));
        r.Add(Tool("mkfs.exfat", "MkfsExfat", required: true));
        r.Add(Tool("wipefs", "Wipefs", required: true));
        r.Add(Tool("findmnt", "Findmnt", required: false));
        r.Add(Tool("sync", "Sync", required: false));
        r.Add(_elevated() ? new("Pkexec", DiagStatus.Pass, "root") : Tool("pkexec", "Pkexec", required: true));
        var udisks = Tool("udisksctl", "Udisksctl", required: true);
        r.Add(udisks);
        if (udisks.Status == DiagStatus.Pass)
        {
            var s = await _run("udisksctl", new[] { "status" }, null, ct, 15_000);
            r.Add(s.Ok ? new("UdisksDaemon", DiagStatus.Pass) : new("UdisksDaemon", DiagStatus.Warn, FirstLine(s.StdErr)));
        }
        r.Add(Tool("xdg-open", "XdgOpen", required: false));
    }

    private async Task WindowsAsync(List<DiagResult> r, CancellationToken ct)
    {
        r.Add(_elevated() ? new("Admin", DiagStatus.Pass) : new("Admin", DiagStatus.Fail));
        var ps = Tool("powershell", "PowerShell", required: true);
        r.Add(ps);
        r.Add(Tool("diskpart", "Diskpart", required: true));
        if (ps.Status == DiagStatus.Pass)
        {
            var s = await _run("powershell", new[] { "-NoProfile", "-NonInteractive", "-Command", "(Get-Disk | Measure-Object).Count" }, null, ct, 30_000);
            r.Add(s.Ok ? new("StorageCmdlets", DiagStatus.Pass, s.StdOut.Trim() + " disks")
                       : new("StorageCmdlets", DiagStatus.Fail, FirstLine(s.StdErr)));
        }
    }

    private async Task MacAsync(List<DiagResult> r, CancellationToken ct)
    {
        var diskutil = Tool("diskutil", "Diskutil", required: true);
        r.Add(diskutil);
        // a quarantined app launched straight from the DMG / Downloads runs from a random read-only translocation path
        var path = AppContext.BaseDirectory;
        var translocated = path.Contains("/AppTranslocation/", StringComparison.Ordinal) || path.StartsWith("/Volumes/", StringComparison.Ordinal);
        r.Add(translocated ? new("AppLocation", DiagStatus.Warn, SystemInfo.Redact(path)) : new("AppLocation", DiagStatus.Pass, SystemInfo.Redact(path)));
        if (diskutil.Status == DiagStatus.Pass)
        {
            var s = await _run("diskutil", new[] { "list", "-plist", "external", "physical" }, null, ct, 30_000);
            r.Add(s.Ok ? new("DiskutilList", DiagStatus.Pass) : new("DiskutilList", DiagStatus.Fail, FirstLine(s.StdErr)));
        }
    }

    // ------------------------------------------------------------------ shared checks
    private DiagResult Tool(string program, string id, bool required) =>
        _exists(program) ? new(id, DiagStatus.Pass) : new(id, required ? DiagStatus.Fail : DiagStatus.Warn, program);

    private async Task<DiagResult> DriveListAsync(CancellationToken ct)
    {
        try
        {
            var drives = await _drives.ListAsync(ct);
            return new("DriveList", DiagStatus.Pass, drives.Count == 0 ? "0" : string.Join(", ", drives.Select(d => $"{d.Name} ({Format.Bytes(d.SizeBytes)})")));
        }
        catch (Exception e) when (e is IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new("DriveList", DiagStatus.Fail, FirstLine(e.Message));
        }
    }

    /// <summary>Checks a folder can be created and written to, and (optionally) has a sensible amount of free space.</summary>
    private static DiagResult Writable(string id, string dir, long? minFree, string? spaceId = null)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".syn3updater-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new(id, DiagStatus.Fail, SystemInfo.Redact(dir) + " - " + FirstLine(e.Message));
        }
        var free = SystemInfo.FreeSpace(dir);
        if (minFree is { } min)
        {
            try
            {
                var probe = Path.GetFullPath(dir);
                if (new DriveInfo(probe).AvailableFreeSpace < min) return new(spaceId ?? id, DiagStatus.Warn, $"{SystemInfo.Redact(dir)} - {free}");
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException) { }
        }
        return new(id, DiagStatus.Pass, $"{SystemInfo.Redact(dir)}{(free == null ? "" : " - " + free)}");
    }

    private async Task<IEnumerable<DiagResult>> NetworkAsync(CancellationToken ct)
    {
        var results = new List<DiagResult>();
        var api = await ReachAsync("Api", ApiEndpoints.ApiBase, ct, checkClock: true);
        results.Add(api.result);
        results.Add((await ReachAsync("Ford", "https://ivsu.binaries.ford.com/", ct, checkClock: false)).result);
        if (api.skew is { } skew && Math.Abs(skew.TotalMinutes) > 5)
            results.Add(new("Clock", DiagStatus.Warn, $"{skew.TotalMinutes:+0;-0} min"));
        else if (api.skew != null) results.Add(new("Clock", DiagStatus.Pass));
        return results;
    }

    private async Task<(DiagResult result, TimeSpan? skew)> ReachAsync(string id, string url, CancellationToken ct, bool checkClock)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            TimeSpan? skew = checkClock && resp.Headers.Date is { } d ? DateTimeOffset.UtcNow - d : null;
            return (new(id, DiagStatus.Pass, $"HTTP {(int)resp.StatusCode}"), skew);      // any answer proves the host is reachable
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return (new(id, DiagStatus.Fail, FirstLine(e.InnerException?.Message ?? e.Message)), null);
        }
    }

    private static string FirstLine(string? s) => (s ?? "").Trim().Split('\n')[0].Trim();
}
