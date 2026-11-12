using Cyanlabs.Syn3Updater.Core.Drives;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>Eject a drive and tell the user how it went. Shared by Home, Utilities and the Downloads page.</summary>
public static class EjectFlow
{
    private static bool _busy;

    /// <returns>True when the drive was ejected (or at least unmounted) and can be removed.</returns>
    public static async Task<bool> RunAsync(DriveInfoModel drive, string? mountPath)
    {
        if (drive.IsFolder || drive.IsChooser || _busy) return false;
        if (Workflow.Busy) { await Dialogs.Error(Ctx.T("MessageBox.DownloadInProgress")); return false; }          // the drive may be the one being written to
        _busy = true;
        try
        {
            var result = await Ctx.Drives.EjectAsync(drive, string.IsNullOrEmpty(mountPath) ? drive.MountPath : mountPath);
            Ctx.Log.Info("Eject: " + result.Message.Replace("\n", " "));
            if (result.Ok) await Dialogs.Info(result.Message, Ctx.T("App.EjectedTitle")); else await Dialogs.Error(result.Message);
            return result.Ok;
        }
        catch (Exception e)
        {
            Ctx.Log.Error("Eject failed: " + e.Message);
            await Dialogs.Error(e.Message);
            return false;
        }
        finally { _busy = false; }
    }
}
