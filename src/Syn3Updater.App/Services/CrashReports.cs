using Cyanlabs.Syn3Updater.Core.Services;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>
/// After a crash, the next start asks whether to send it. Nothing is sent without a yes, and a build without a crash report server
/// (no DSN compiled in) never asks. The question is asked once: the remembered crash is removed whatever the answer.
/// </summary>
public static class CrashReports
{
    /// <summary>Returns true when the user was asked.</summary>
    public static async Task<bool> OfferAsync()
    {
        var dsn = CrashReporter.ConfiguredDsn;
        var dir = Ctx.Paths.DataDir;
        var crash = CrashReporter.LoadPending(dir);
        if (crash == null) return false;
        if (dsn == null) { CrashReporter.ClearPending(dir); return false; }
        CrashReporter.ClearPending(dir);                      // first: a crash while this question is open must not make it come back forever

        var summary = $"{crash.Type}: {crash.Message}";
        if (summary.Length > 300) summary = summary[..300] + "...";
        if (!await Dialogs.Confirm(Ctx.T("MessageBox.CrashReportAsk", summary, string.IsNullOrWhiteSpace(crash.Vin) ? "" : Ctx.T("MessageBox.CrashReportVin", crash.Vin)),
                Ctx.T("MessageBox.CrashReportTitle")))
        {
            Ctx.Log.Info("A crash report was offered and declined");
            return true;
        }
        // optional: cancelling or leaving it empty simply sends the report without a comment
        crash.Comment = CrashReporter.SanitizeComment(await Dialogs.Prompt(Ctx.T("MessageBox.CrashReportCommentTitle"), Ctx.T("MessageBox.CrashReportComment")));
        var sent = await CrashReporter.SendAsync(Ctx.Http, dsn, crash);
        Ctx.Log.Info(sent ? "Crash report sent" : "Crash report could not be sent");
        await Dialogs.Info(Ctx.T(sent ? "MessageBox.CrashReportSent" : "MessageBox.CrashReportFailed"), Ctx.T("MessageBox.CrashReportTitle"));
        return true;
    }
}
