using System.Net;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>The "no connection to the CyanLabs servers" banner: what counts as unreachable, when it appears and when it goes away.</summary>
public class ConnectionBannerTests
{
    private sealed class Handler(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public Func<HttpResponseMessage> Answer { get; set; } = answer;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(Answer());
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body) };
    private const string UserAgents = """{"data":{"useragents":[{"useragent":"UA","min":"1","max":"2"}]}}""";

    private static (ApiClient Api, Handler Http, List<bool> Events) Client(Func<HttpResponseMessage> answer)
    {
        var http = new Handler(answer); var api = new ApiClient(new HttpClient(http), () => "t", () => ""); var events = new List<bool>();
        api.ReachabilityChanged += events.Add;
        return (api, http, events);
    }

    [Fact]
    public async Task ANetworkError_MeansUnreachable_ThenAnAnswerMeansReachable_AndOnlyChangesAreReported()
    {
        var (api, http, events) = Client(() => throw new HttpRequestException("Resource temporarily unavailable"));
        await Assert.ThrowsAsync<ApiException>(() => api.GetUserAgents());
        await Assert.ThrowsAsync<ApiException>(() => api.GetUserAgents());
        Assert.Equal(new[] { false }, events);                           // two failures in a row: one report
        http.Answer = () => Json(UserAgents);
        await api.GetUserAgents(); await api.GetUserAgents();
        Assert.Equal(new[] { false, true }, events);                      // back: reported once
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false)]
    [InlineData(HttpStatusCode.GatewayTimeout, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    [InlineData(HttpStatusCode.BadRequest, true)]          // the server answered: it is there, it just did not like the request
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.OK, true)]
    public async Task OnlyServerErrors_CountAsUnreachable(HttpStatusCode code, bool reachable)
    {
        var (api, _, events) = Client(() => Json(code == HttpStatusCode.OK ? UserAgents : "{}", code));
        try { await api.GetUserAgents(); } catch (ApiException) { /* the error dialogs are tested elsewhere */ }
        Assert.Equal(new[] { reachable }, events);
    }

    [Fact]
    public async Task ATimeout_MeansUnreachable_ButTheCallerGivingUpIsNot()
    {
        var (api, _, events) = Client(() => throw new TaskCanceledException("timed out"));
        await Assert.ThrowsAsync<TaskCanceledException>(() => api.GetUserAgents());
        Assert.Equal(new[] { false }, events);

        var (api2, _, events2) = Client(() => throw new TaskCanceledException());
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => api2.GetUserAgents(cts.Token));
        Assert.Empty(events2);                                          // cancelled by us: nothing is known about the servers
    }

    [Fact]
    public async Task Ping_SaysWhetherTheBackendAnswers_AndNeverThrows()
    {
        var (api, http, events) = Client(() => throw new HttpRequestException("down"));
        Assert.False(await api.PingAsync());
        http.Answer = () => Json(UserAgents);
        Assert.True(await api.PingAsync());
        Assert.Equal(new[] { false, true }, events);
    }

    // ---- the app ----

    [AvaloniaFact]
    public async Task TheBanner_AppearsWhenTheBackendCannotBeReached_AndGoesWhenRetryingWorks()
    {
        using var env = new AppEnv(); var main = env.Main;
        Assert.False(main.Offline);
        env.Api.Offline = true; main.Home.ReloadSummary();
        await AppEnv.Until(() => main.Offline);
        Assert.Equal(Ctx.T("App.NoConnectionBanner"), main.OfflineText);
        Assert.Equal("No connection to CyanLabs. Check your internet connectivity.", main.OfflineText);          // (the English wording)

        await main.RetryConnectionCommand.ExecuteAsync(null);                  // still down: the banner stays
        Assert.True(main.Offline);

        env.Api.Offline = false;
        await main.RetryConnectionCommand.ExecuteAsync(null);
        await AppEnv.Until(() => !main.Offline);
    }

    [AvaloniaFact]
    public async Task TheBanner_GoesAsSoonAsAnyRequestGetsThrough()
    {
        using var env = new AppEnv(); var main = env.Main;
        env.Api.Offline = true; main.Home.ReloadSummary();
        await AppEnv.Until(() => main.Offline);
        env.Api.Offline = false;
        await Ctx.Api.GetMy20Models();                                         // some other part of the app asks and is answered
        await AppEnv.Until(() => !main.Offline);
    }

    [AvaloniaFact]
    public async Task TheBannersStatusButton_OpensTheStatusPage()
    {
        using var env = new AppEnv();
        await env.Main.OpenStatusPageCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "https://status.cyanlabs.net" }, env.Dialogs.Opened);
    }

    [Fact]
    public void TheBannerMarkup_SitsAboveTheUpdateNotice_WithRetryAndStatusButtons()
    {
        var xaml = File.ReadAllText(Path.Combine(Path.GetDirectoryName(InterrogatorFixtureTests.Fixture("My20.xml"))!, "..", "..", "..", "..", "src", "Syn3Updater.App", "Views", "MainWindow.axaml"));
        var banner = xaml.IndexOf("x:Name=\"OfflineBanner\"", StringComparison.Ordinal); var update = xaml.IndexOf("IsVisible=\"{Binding UpdateAvailable}\"", StringComparison.Ordinal);
        Assert.True(banner > 0 && banner < update, "the connection banner comes first, the update notice below it");
        var offline = xaml[banner..update];
        Assert.Contains("Classes=\"banner error\"", offline); Assert.DoesNotContain("WarnFg", offline);          // red, not the amber of the other notices
        foreach (var bit in new[] { "IsVisible=\"{Binding Offline}\"", "{Binding OfflineText}", "RetryConnectionCommand", "OpenStatusPageCommand", "App.NoConnectionRetry", "Home.OpenStatusPage" })
            Assert.Contains(bit, xaml);
    }
}
