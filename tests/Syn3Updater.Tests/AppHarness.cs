using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.Core.Drives;
using Xunit;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(Cyanlabs.Syn3Updater.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]   // the app keeps its services in statics

namespace Cyanlabs.Syn3Updater.Tests;

public class TestApp : Application { }

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>Scripted replacement for the real dialogs: records everything and answers Yes/No by rule.</summary>
public class FakeDialogs : IDialogBackend
{
    public List<string> Messages { get; } = new();
    public List<string> Opened { get; } = new();
    public List<string> Clipboard { get; } = new();
    public List<(Func<string, bool> Match, bool Yes)> Rules { get; } = new();
    public bool DefaultYes { get; set; }
    /// <summary>Button indexes to answer with, in order (for dialogs with more than two buttons).</summary>
    public Queue<int> Choices { get; } = new();
    public string? FolderToPick { get; set; }
    public string? FileToPick { get; set; }
    public string? PromptAnswer { get; set; }
    public List<string> Prompts { get; } = new();

    public List<(string Title, DialogOptions? Options, string[] Buttons)> Shown { get; } = new();

    public Task<int> Show(string message, string title, string[] buttons, bool warning, bool error, int defaultIndex, DialogOptions? options = null)
    {
        Messages.Add(message); Shown.Add((title, options, buttons));
        if (buttons.Length == 1) return Task.FromResult(0);
        if (Choices.Count > 0) return Task.FromResult(Choices.Dequeue());
        var yes = Rules.FirstOrDefault(r => r.Match(message)) is { Match: not null } rule ? rule.Yes : DefaultYes;
        return Task.FromResult(yes ? 1 : 0);
    }
    public int FolderPicks { get; private set; }
    public Task<string?> PickFolder(string title) { FolderPicks++; return Task.FromResult(FolderToPick); }
    public Task<string?> Prompt(string title, string message, string initial, bool multiline = false) { Prompts.Add(message); return Task.FromResult(PromptAnswer); }
    public Task<string?> PickFile(string title, string filterName, string[] patterns) => Task.FromResult(FileToPick);
    public Task SetClipboard(string text) { Clipboard.Add(text); return Task.CompletedTask; }
    public Task OpenUrl(string url) { Opened.Add(url); return Task.CompletedTask; }
    public void Say(Func<string, bool> match, bool yes) => Rules.Add((match, yes));
}

/// <summary>Serves bytes derived from the request path (so md5/size are predictable) on localhost.</summary>
public sealed class FileServer : IDisposable
{
    private HttpListener _l = new();
    public string Url { get; }
    public FileServer()
    {
        for (var attempt = 0; ; attempt++)            // tests run in parallel: retry when another server grabbed the random port
        {
            var port = Random.Shared.Next(40000, 60000);
            Url = $"http://127.0.0.1:{port}";
            _l = new HttpListener();                  // a failed Start() disposes the listener on Windows, so every attempt needs a new one
            _l.Prefixes.Add(Url + "/");
            try { _l.Start(); break; } catch (HttpListenerException) when (attempt < 20) { _l.Close(); }
        }
        _ = Task.Run(async () =>
        {
            while (_l.IsListening)
            {
                HttpListenerContext c;
                try { c = await _l.GetContextAsync(); } catch { return; }
                try
                {
                    var data = Blob(c.Request.Url!.AbsolutePath.TrimStart('/'));
                    int start = 0, end = data.Length - 1;
                    if (c.Request.Headers["Range"] is { } r) { var p = r.Replace("bytes=", "").Split('-'); start = int.Parse(p[0]); end = int.Parse(p[1]); c.Response.StatusCode = 206; }
                    c.Response.ContentLength64 = end - start + 1;
                    if (c.Request.HttpMethod != "HEAD") await c.Response.OutputStream.WriteAsync(data.AsMemory(start, end - start + 1));
                    c.Response.Close();
                }
                catch { /* client went away */ }
            }
        });
    }
    public static byte[] Blob(string name) => Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat(name, 300)));
    public static string Md5(string name) => Convert.ToHexString(MD5.HashData(Blob(name)));
    public void Dispose() { try { _l.Close(); } catch { } }
}

/// <summary>Answers the CyanLabs GraphQL/REST calls with canned data; everything else goes to the real network stack (localhost only in tests).</summary>
public sealed class FakeCyanLabs : DelegatingHandler
{
    private readonly string _files;
    public List<string> Queries { get; } = new();
    public bool Offline { get; set; }
    /// <summary>Awaited with the query text before the backend answers: lets a test hold a response open to overlap two loads.</summary>
    public Func<string, Task>? Gate { get; set; }
    public FakeCyanLabs(string fileServerUrl) : base(new SocketsHttpHandler()) { _files = fileServerUrl; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
    {
        var host = r.RequestUri!.Host;
        if (host.Contains("127.0.0.1")) return await base.SendAsync(r, ct);
        if (Offline) throw new HttpRequestException("offline");

        var path = r.RequestUri.AbsolutePath;
        if (path.Contains("githubchangelog")) return Json("""{"data":[{"version":"2.14.2.0","date":"30-08-2025","changelog":"Fixed: **API**"}]}""");
        if (path.Contains("logs/post")) return Json("""{"uuid":"u1"}""");
        if (path.Contains("asbuilt")) return Json("""{"filename":"f.xml"}""");

        var body = r.Content == null ? "" : await r.Content.ReadAsStringAsync(ct);
        string q; try { q = JsonDocument.Parse(body).RootElement.GetProperty("query").GetString()!; } catch { q = body; }
        Queries.Add(q);
        if (Gate != null) await Gate(q);
        string Ivsu(string name, string type, string extra = "") => $$"""{"id":"1","name":"{{name}}","type":"{{type}}","regions":["ALL"],"md5":"{{FileServer.Md5(name + ".tar.gz")}}","url":"{{_files}}/{{name}}.tar.gz","version":"{{(type == "APPS" ? "3.4.26057" : "")}}","filesize":"{{FileServer.Blob(name + ".tar.gz").Length}}","notes":"n"{{extra}}}""";

        if (q.Contains("_eq: \"APPS\"")) return Json("{\"data\":{\"ivsu\":[{\"version\":\"3.3.19052\"},{\"version\":\"3.4.23188\"},{\"version\":\"3.4.26057\"},{\"version\":\"3.4.26057\"},{\"version\":null},{\"version\":\"\"},{\"version\":\" 3.4.23088 \"}]}}");
        if (q.Contains("specialpackage")) { var n = q.Split("_eq: \"")[1].Split('"')[0]; return Json("{\"data\":{\"ivsu\":[" + Ivsu(n, "TOOL") + "]}}"); }
        if (q.Contains("map_releases(sort")) return Json("""{"data":{"map_releases":[{"name":"Map 2025","notes":"map notes","esn":false,"regions":["NA"]},{"name":"Map ESN","notes":"","esn":true,"regions":["NA"]}]}}""");
        if (q.Contains("map_releases(limit")) return Json("{\"data\":{\"map_releases\":[{\"name\":\"Map 2025\",\"ivsus\":[{\"map_ivsu\":" + Ivsu("MAP1", "MAP") + "},{\"map_ivsu\":" + Ivsu("LIC1", "MAP_LICENSE") + "}]}]}}");
        if (q.Contains("releases(sort")) return Json("""{"data":{"releases":[{"name":"Sync 3.4.26057","notes":"**Big** release","version":"3.4.26057","feedbackurl":"https://forum/x","regions":["NA"]}]}}""");
        if (q.Contains("releases(limit")) return Json("{\"data\":{\"releases\":[{\"name\":\"Sync 3.4.26057\",\"ivsus\":[{\"ivsu\":" + Ivsu("APP1", "APPS") + "},{\"ivsu\":" + Ivsu("VOICE1", "VOICE") + "}]}]}}");
        if (q.Contains("notices")) return Json("""{"data":{"notices":[{"id":"1","title":"Important","notice":"Read **this**","important":true,"date_created":"2025-01-02"},{"id":"2","title":"Other","notice":"fyi","important":false,"date_created":"2025-01-01"}]}}""");
        if (q.Contains("my20models")) return Json("""{"data":{"my20models":[{"model":"LB5T-14G371"},{"model":"5U5T-14G371"}]}}""");
        if (q.Contains("useragents")) return Json("""{"data":{"useragents":[{"useragent":"UA/[PLACEHOLDER]","min":"1","max":"5"}]}}""");
        if (q.Contains("ivsu(limit: 1, filter: {name")) return Json("""{"data":{"ivsu":[{"version":"3.4.20282"}]}}""");
        return Json("""{"data":{}}""");
    }

    private static HttpResponseMessage Json(string j) => new(HttpStatusCode.OK) { Content = new StringContent(j, Encoding.UTF8, "application/json") };
}

/// <summary>A machine with no removable drives, on every OS. Tests that need drives pass their own fake.</summary>
public sealed class InertDrives : IDriveService
{
    public string OsDescription => "none";
    public Task<IReadOnlyList<DriveInfoModel>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DriveInfoModel>>(new List<DriveInfoModel>());
    public Task<string> FormatAsync(DriveInfoModel drive, IProgress<string> log, CancellationToken ct = default) => throw new Cyanlabs.Syn3Updater.Core.Drives.FormatException("no drives in tests");
    public Task<string> EnsureMountedAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.FromResult(drive.MountPath);
    public Task FlushAsync(DriveInfoModel drive, CancellationToken ct = default) => Task.CompletedTask;
    public Task<EjectResult> EjectAsync(DriveInfoModel drive, string? mountPath, CancellationToken ct = default) => Task.FromResult(new EjectResult(false, "no drives in tests"));
}

/// <summary>One fully wired app (settings in a temp dir, fake dialogs, fake backend, a real view-model tree).</summary>
public sealed class AppEnv : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory("syn3app").FullName;
    public FileServer Files { get; } = new();
    public FakeCyanLabs Api { get; }
    public FakeDialogs Dialogs { get; } = new();
    public MainWindowViewModel Main { get; }
    public string DownloadDir => Path.Combine(Dir, "downloads");
    public string UsbDir => Path.Combine(Dir, "usb");

    public AppEnv(string region = "NA", int version = 3420282, bool nav = false, bool disclaimer = true, IDriveService? drives = null, Func<string?>? fingerprint = null)
    {
        Api = new FakeCyanLabs(Files.Url);
        Cyanlabs.Syn3Updater.App.Services.Dialogs.Backend = Dialogs;
        Ctx.Init(Path.Combine(Dir, "data"), Api, drives ?? new InertDrives(), fingerprint ?? (() => "none"));      // tests never talk to the host OS's real drives (CI runners have none)
        Ctx.Main.DownloadPath = DownloadDir; Ctx.Main.LogPath = Path.Combine(Dir, "logs"); Directory.CreateDirectory(Ctx.Main.LogPath);
        Ctx.Main.DisclaimerAccepted = disclaimer;
        Ctx.Profile.CurrentRegion = region; Ctx.Profile.CurrentVersion = version; Ctx.Profile.CurrentNav = nav; Ctx.Main.DownloadConnections = 2;
        Directory.CreateDirectory(UsbDir);
        Workflow.Busy = false;
        Main = new MainWindowViewModel();
    }

    public static async Task Until(Func<bool> condition, int ms = 15000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("condition not met"); await Task.Delay(25); }
    }

    public void Dispose() { Files.Dispose(); try { Directory.Delete(Dir, true); } catch { } }
}
