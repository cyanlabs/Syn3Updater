using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>The next-start crash report offer: asked once, nothing sent without a yes, Cancel at the comment box cancels the report.</summary>
public class CrashReportsFlowTests
{
    private const string Dsn = "https://thekey@glitchtip.test/1";

    private static async Task<(AppEnv Env, bool Asked)> Run(bool yes, string? comment, bool pending = true)
    {
        var old = Environment.GetEnvironmentVariable("SYN3_SENTRY_DSN");
        Environment.SetEnvironmentVariable("SYN3_SENTRY_DSN", Dsn);
        var env = new AppEnv();
        try
        {
            if (pending) CrashReporter.SavePending(Ctx.Paths.DataDir, new CrashRecord { Time = "2026-11-07T00:00:00Z", Type = "System.IO.IOException", Message = "boom", Version = "3.0.0", Channel = "Stable", Os = "Linux" });
            env.Dialogs.Say(m => m.Contains("closed unexpectedly"), yes);
            env.Dialogs.PromptAnswer = comment;
            var asked = await CrashReports.OfferAsync();
            return (env, asked);
        }
        finally { Environment.SetEnvironmentVariable("SYN3_SENTRY_DSN", old); }
    }

    private static bool Sent(AppEnv env) => env.Api.Queries.Any(q => q.Contains("syn3updater@3.0.0"));

    [AvaloniaFact]
    public async Task Yes_WithAComment_SendsTheReportWithTheComment_AndSaysThanks()
    {
        var (env, asked) = await Run(yes: true, comment: "it crashed when I clicked Start");
        using (env)
        {
            Assert.True(asked); Assert.True(Sent(env));
            Assert.Contains(env.Api.Queries, q => q.Contains("user_comment") && q.Contains("it crashed when I clicked Start"));
            Assert.Contains(env.Dialogs.Messages, m => m.Contains("crash report was sent"));
            Assert.Null(CrashReporter.LoadPending(Ctx.Paths.DataDir));
        }
    }

    [AvaloniaFact]
    public async Task Yes_WithAnEmptyComment_StillSends_WithoutAComment()
    {
        var (env, _) = await Run(yes: true, comment: "");
        using (env)
        {
            Assert.True(Sent(env));
            Assert.DoesNotContain(env.Api.Queries, q => q.Contains("user_comment"));
        }
    }

    [AvaloniaFact]
    public async Task Cancel_AtTheCommentBox_CancelsTheReport_NothingIsSent_NoThanks()
    {
        var (env, asked) = await Run(yes: true, comment: null);          // null = the prompt was cancelled
        using (env)
        {
            Assert.True(asked);
            Assert.False(Sent(env));
            Assert.DoesNotContain(env.Dialogs.Messages, m => m.Contains("crash report was sent") || m.Contains("could not be sent"));
            Assert.Null(CrashReporter.LoadPending(Ctx.Paths.DataDir));         // asked once: it does not come back
        }
    }

    [AvaloniaFact]
    public async Task No_SendsNothing_AndNeverAsksForAComment()
    {
        var (env, asked) = await Run(yes: false, comment: "ignored");
        using (env)
        {
            Assert.True(asked); Assert.False(Sent(env));
            Assert.Empty(env.Dialogs.Prompts);
            Assert.Null(CrashReporter.LoadPending(Ctx.Paths.DataDir));
        }
    }

    [AvaloniaFact]
    public async Task NothingPending_NothingIsAsked()
    {
        var (env, asked) = await Run(yes: true, comment: "x", pending: false);
        using (env) { Assert.False(asked); Assert.False(Sent(env)); Assert.DoesNotContain(env.Dialogs.Messages, m => m.Contains("closed unexpectedly")); }
    }
}
