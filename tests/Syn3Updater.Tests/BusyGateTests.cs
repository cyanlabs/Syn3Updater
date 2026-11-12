using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Drives;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

public class BusyGateTests
{
    [AvaloniaFact]
    public async Task WhileAJobRuns_HomeUtilitySettingsAndDiagnostics_StayShut_ButDownloadsAndLogsOpen()
    {
        using var env = new AppEnv(); var m = env.Main; Assert.Same(m.Home, m.Current);
        Workflow.Busy = true;
        try
        {
            Assert.False(await m.GoToAsync(m.Utility)); Assert.Same(m.Home, m.Current);
            Assert.False(await m.GoToAsync(m.Settings)); Assert.Same(m.Home, m.Current);
            Assert.False(await m.GoToAsync(m.Diagnostics)); Assert.Same(m.Home, m.Current);
            Assert.Contains(Ctx.T("MessageBox.DownloadInProgress"), env.Dialogs.Messages);
            Assert.True(await m.GoToAsync(m.Downloads)); Assert.Same(m.Downloads, m.Current);
            Assert.False(await m.GoToAsync(m.Home)); Assert.Same(m.Downloads, m.Current);          // and the way back to Home is shut too
            Assert.True(await m.GoToAsync(m.Logs)); Assert.Same(m.Logs, m.Current);
        }
        finally { Workflow.Busy = false; }
        Assert.True(await m.GoToAsync(m.Home)); Assert.Same(m.Home, m.Current);                   // reopens when the job ends
    }

    [AvaloniaFact]
    public async Task EjectIsRefused_WhileAJobRuns()
    {
        using var env = new AppEnv();
        Workflow.Busy = true;
        try
        {
            Assert.False(await EjectFlow.RunAsync(new DriveInfoModel { Name = "USB", MountPath = "/media/usb" }, "/media/usb"));
            Assert.Contains(Ctx.T("MessageBox.DownloadInProgress"), env.Dialogs.Messages);
        }
        finally { Workflow.Busy = false; }
    }
}
