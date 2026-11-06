using Avalonia.Threading;
using Cyanlabs.Syn3Updater.Core.Drives;

namespace Cyanlabs.Syn3Updater.App.Services;

/// <summary>
///     Notices when a drive is plugged in or removed and tells the drive dropdowns, so nobody has to press Refresh.
///     It polls a cheap fingerprint every couple of seconds and only lists the drives (the expensive part) when that changed and settled.
///     It stays quiet while an update run or a dialog is active, and a failure is only logged, never shown.
/// </summary>
public sealed class DriveMonitor
{
    private readonly IDriveService _drives;
    private readonly DriveChangeDetector _detector;
    private readonly Func<bool> _canPoll;
    private DispatcherTimer? _timer;
    private bool _polling;

    /// <summary>Raised on the UI thread with the drives that are there now.</summary>
    public event Action<IReadOnlyList<DriveInfoModel>>? Changed;

    public DriveMonitor(IDriveService drives, Func<string?> fingerprint, Func<bool> canPoll)
    {
        _drives = drives; _detector = new DriveChangeDetector(fingerprint); _canPoll = canPoll;
    }

    public void Start(TimeSpan interval)
    {
        _detector.Prime();
        _timer ??= new DispatcherTimer(interval, DispatcherPriority.Background, (_, _) => _ = PollAsync());
        _timer.Start();
    }

    public void Stop() => _timer?.Stop();

    /// <summary>One check. Called by the timer; tests call it directly.</summary>
    public async Task PollAsync()
    {
        if (_polling || !_canPoll()) return;
        _polling = true;
        try
        {
            // The fingerprint asks Windows about every drive (DriveInfo), which can block for seconds on a stick that is still spinning up or mounting:
            // never do that on the UI thread, or the whole window stalls and the list looks like it is not refreshing.
            if (!await Task.Run(() => _detector.Poll())) return;
            IReadOnlyList<DriveInfoModel> found;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try { found = await _drives.ListAsync(); }
            catch (Exception e)
            {
                Ctx.Log.Debug("Drive auto-refresh could not list the drives: " + e.Message);
                _detector.Retry();                                  // try again on the next settled poll
                return;
            }
            Ctx.Log.Info($"Drives changed: {found.Count} found in {timer.ElapsedMilliseconds} ms");
            Changed?.Invoke(found);
        }
        catch (Exception e) { Ctx.Log.Debug("Drive auto-refresh failed: " + e.Message); }
        finally { _polling = false; }
    }
}
