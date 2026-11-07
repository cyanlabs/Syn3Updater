using System.Net;
using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>Crash reports: the DSN, what is remembered, what goes into the event, and that sending only goes where the DSN says.</summary>
public class CrashReporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "syn3crash-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* temp */ } }

    private static Exception Thrown()
    {
        try { throw new InvalidOperationException(@"Cannot open C:\Users\scott\Downloads\Test\file.bin"); }
        catch (Exception e) { return e; }
    }

    [Theory]
    [InlineData("https://537f041b73184330a56f8fc1dcc4c140@glitchtip.cyanlabs.net/1", "537f041b73184330a56f8fc1dcc4c140", "https://glitchtip.cyanlabs.net/api/1/store/")]
    [InlineData("https://key@sentry.example.com:9000/42", "key", "https://sentry.example.com:9000/api/42/store/")]
    [InlineData("https://key@host.example.com/prefix/7", "key", "https://host.example.com/prefix/api/7/store/")]
    public void Dsn_BecomesTheStoreUrl_AndKeepsTheKey(string dsn, string key, string url)
    {
        var d = CrashDsn.TryParse(dsn);
        Assert.NotNull(d);
        Assert.Equal(key, d!.PublicKey);
        Assert.Equal(url, d.StoreUrl.ToString());
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("   ")] [InlineData("not a url")] [InlineData("ftp://key@host/1")]
    [InlineData("https://host/1")] [InlineData("https://key@host")]
    public void BadOrMissingDsn_IsNull_SoNothingIsEverOffered(string? dsn) => Assert.Null(CrashDsn.TryParse(dsn));

    [Fact]
    public void Scrub_ReplacesTheHomeFolderWithTilde_InBothSlashStyles()
    {
        Assert.Equal(@"~\Downloads and ~/x", CrashReporter.Scrub(@"C:\Users\scott\Downloads and C:/Users/scott/x", @"C:\Users\scott"));
        Assert.Equal("", CrashReporter.Scrub(null, @"C:\Users\scott"));
        Assert.Equal("keep", CrashReporter.Scrub("keep", ""));
    }

    [Fact]
    public void Capture_HasTheErrorTheVersionAndNoHomeFolder_AndTheVinWhenKnown()
    {
        CrashReporter.CurrentVin = "WF0TEST1234567890";
        try
        {
            var r = CrashReporter.Capture("Unhandled exception", Thrown(), "3.0.0-test13", "Beta", "Windows 11", "X64", @"C:\Users\scott");
            Assert.Equal("System.InvalidOperationException", r.Type);
            Assert.Contains(@"~\Downloads\Test\file.bin", r.Message);
            Assert.DoesNotContain(@"C:\Users\scott", r.Message + r.StackTrace);
            Assert.Equal("3.0.0-test13", r.Version);
            Assert.Equal("WF0TEST1234567890", r.Vin);
        }
        finally { CrashReporter.CurrentVin = null; }
    }

    [Fact]
    public void Capture_WithTheRealHomeFolder_LeavesItOutOfTheStackTraceToo()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var r = CrashReporter.Capture("Unhandled exception", Thrown(), "3.0.0", "Stable", "Linux", "X64");      // the stack names this test's source file, under the home folder
        Assert.Contains("CrashReporterTests.cs", r.StackTrace);
        Assert.DoesNotContain(home, r.StackTrace + r.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Pending_RoundTrips_AndClears_AndADamagedFileIsDroppedNotReported()
    {
        Assert.Null(CrashReporter.LoadPending(_dir));
        var r = CrashReporter.Capture("Unobserved task exception", Thrown(), "3.0.0", "Stable", "Linux", "X64");
        CrashReporter.SavePending(_dir, r);
        Assert.Equal(r.Message, CrashReporter.LoadPending(_dir)!.Message);
        CrashReporter.ClearPending(_dir);
        Assert.Null(CrashReporter.LoadPending(_dir));

        File.WriteAllText(CrashReporter.PendingPath(_dir), "{ not json");
        Assert.Null(CrashReporter.LoadPending(_dir));
        Assert.False(File.Exists(CrashReporter.PendingPath(_dir)));
    }

    [Fact]
    public void Event_HasReleaseEnvironmentTagsAndFramesOldestFirst_AndNeverTheMachineName()
    {
        var r = new CrashRecord
        {
            Time = "2026-11-07T00:00:00Z", Kind = "Unhandled exception", Type = "System.IO.IOException", Message = "boom",
            Os = "Linux", Arch = "X64", Version = "3.0.0-test13", Channel = "Beta",
            StackTrace = "   at Cyanlabs.Syn3Updater.Core.Inner.Run() in ~/src/Inner.cs:line 12\n   at Cyanlabs.Syn3Updater.App.Outer.Go()\n   at System.Threading.Thread.Start()",
        };
        using var doc = JsonDocument.Parse(CrashReporter.BuildEventJson(r, "abc"));
        var root = doc.RootElement;
        Assert.Equal("abc", root.GetProperty("event_id").GetString());
        Assert.Equal("syn3updater@3.0.0-test13", root.GetProperty("release").GetString());
        Assert.Equal("beta", root.GetProperty("environment").GetString());
        Assert.Equal("", root.GetProperty("server_name").GetString());
        Assert.Equal("Linux", root.GetProperty("tags").GetProperty("os").GetString());
        var exc = root.GetProperty("exception").GetProperty("values")[0];
        Assert.Equal("System.IO.IOException", exc.GetProperty("type").GetString());
        var frames = exc.GetProperty("stacktrace").GetProperty("frames");
        Assert.Equal(3, frames.GetArrayLength());
        Assert.StartsWith("System.Threading.Thread.Start", frames[0].GetProperty("function").GetString());      // the outermost call first
        Assert.False(frames[0].GetProperty("in_app").GetBoolean());
        Assert.Equal("Cyanlabs.Syn3Updater.Core.Inner.Run()", frames[2].GetProperty("function").GetString());
        Assert.True(frames[2].GetProperty("in_app").GetBoolean());
        Assert.Equal(12, frames[2].GetProperty("lineno").GetInt32());
        Assert.False(root.GetProperty("extra").TryGetProperty("vin", out _));        // no VIN known: none sent
    }

    [Fact]
    public void Event_CarriesTheVin_WhenOneWasRead()
    {
        var r = new CrashRecord { Time = "t", Type = "E", Message = "m", Version = "3.0.0", Channel = "Stable", Vin = "WF0TEST1234567890" };
        using var doc = JsonDocument.Parse(CrashReporter.BuildEventJson(r));
        Assert.Equal("WF0TEST1234567890", doc.RootElement.GetProperty("extra").GetProperty("vin").GetString());
    }

    [Theory]
    [InlineData(null, "")] [InlineData("", "")] [InlineData("   \t\n ", "")]
    [InlineData("  it   crashed\r\nwhen I clicked   Start  ", "it crashed when I clicked Start")]
    [InlineData("mail me at someone@example.com please", "mail me at [email removed] please")]
    [InlineData("zero\u0000width\u200Bchars\u0007 gone", "zerowidthchars gone")]
    public void SanitizeComment_CleansWhitespaceControlCharsAndEmails(string? input, string expected) =>
        Assert.Equal(expected, CrashReporter.SanitizeComment(input, @"C:\Users\scott"));

    [Fact]
    public void SanitizeComment_RemovesTheHomeFolder_AndCapsTheLength()
    {
        Assert.Equal(@"saved to ~\Downloads", CrashReporter.SanitizeComment(@"saved to C:\Users\scott\Downloads", @"C:\Users\scott"));
        var long_ = CrashReporter.SanitizeComment(new string('a', 2000));
        Assert.Equal(CrashReporter.MaxCommentLength + 3, long_.Length);
        Assert.EndsWith("...", long_);
    }

    [Fact]
    public void Event_CarriesTheComment_OnlyWhenThereIsOne()
    {
        var r = new CrashRecord { Time = "t", Type = "E", Message = "m", Version = "3.0.0", Channel = "Stable" };
        using (var none = JsonDocument.Parse(CrashReporter.BuildEventJson(r)))
            Assert.False(none.RootElement.GetProperty("extra").TryGetProperty("user_comment", out _));
        r.Comment = "it crashed when I clicked Start";
        using var with = JsonDocument.Parse(CrashReporter.BuildEventJson(r));
        Assert.Equal("it crashed when I clicked Start", with.RootElement.GetProperty("extra").GetProperty("user_comment").GetString());
    }

    [Fact]
    public async Task Send_PostsToTheStoreUrl_WithTheKey_AndReportsSuccess()
    {
        HttpRequestMessage? seen = null; string? body = null;
        var http = new HttpClient(new FakeHandler(req => { seen = req; body = req.Content!.ReadAsStringAsync().Result; return new HttpResponseMessage(HttpStatusCode.OK); }));
        var dsn = CrashDsn.TryParse("https://thekey@glitchtip.example.com/3")!;
        var ok = await CrashReporter.SendAsync(http, dsn, new CrashRecord { Time = "t", Type = "E", Message = "m", Version = "3.0.0", Channel = "Stable" });
        Assert.True(ok);
        Assert.Equal(HttpMethod.Post, seen!.Method);
        Assert.Equal("https://glitchtip.example.com/api/3/store/", seen.RequestUri!.ToString());
        Assert.Contains("sentry_key=thekey", seen.Headers.GetValues("X-Sentry-Auth").Single());
        Assert.Contains("\"release\":\"syn3updater@3.0.0\"", body);
    }

    [Fact]
    public async Task Send_ReportsFailure_WhenTheServerRefusesOrIsUnreachable()
    {
        var dsn = CrashDsn.TryParse("https://k@glitchtip.example.com/3")!;
        var rec = new CrashRecord { Time = "t", Type = "E", Message = "m", Version = "3.0.0", Channel = "Stable" };
        Assert.False(await CrashReporter.SendAsync(new HttpClient(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))), dsn, rec));
        Assert.False(await CrashReporter.SendAsync(new HttpClient(new FakeHandler(_ => throw new HttpRequestException("down"))), dsn, rec));
    }
}
