namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// Transfer speed over a sliding window. Feed it the CUMULATIVE number of bytes moved so far (summed over all parallel
/// connections), so the speed is the real overall rate no matter how many connections are in use.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly record struct Sample(long Ticks, long Bytes);
    private readonly Queue<Sample> _samples = new();
    private readonly long _windowTicks;
    private readonly long _idleTicks;

    /// <param name="window">How far back the average looks. Longer = smoother, shorter = reacts faster.</param>
    /// <param name="idle">With no bytes moving for this long, the speed reads as zero (e.g. while a checksum is verified).</param>
    public ThroughputMeter(TimeSpan? window = null, TimeSpan? idle = null)
    {
        _windowTicks = (window ?? TimeSpan.FromSeconds(6)).Ticks;
        _idleTicks = (idle ?? TimeSpan.FromSeconds(3)).Ticks;
    }

    private long _lastBytes = -1, _lastMoveTicks;
    private Sample _last;

    public void Reset() { _samples.Clear(); _lastBytes = -1; }

    /// <summary>Records the cumulative byte count at <paramref name="nowTicks"/> (a monotonic clock).</summary>
    public void Add(long cumulativeBytes, long nowTicks)
    {
        if (cumulativeBytes < _lastBytes) _samples.Clear();            // the counter restarted (a new phase began)
        if (cumulativeBytes != _lastBytes) _lastMoveTicks = nowTicks;
        _lastBytes = cumulativeBytes;
        _last = new Sample(nowTicks, cumulativeBytes);
        _samples.Enqueue(_last);
        while (_samples.Count > 2 && nowTicks - _samples.Peek().Ticks > _windowTicks) _samples.Dequeue();
    }

    /// <summary>Average bytes per second over the window; 0 until two samples exist, or when nothing has moved for the idle time.</summary>
    public double BytesPerSecond(long nowTicks)
    {
        if (_samples.Count < 2 || nowTicks - _lastMoveTicks > _idleTicks) return 0;
        var first = _samples.Peek();
        var seconds = (_last.Ticks - first.Ticks) / (double)TimeSpan.TicksPerSecond;
        return seconds <= 0 ? 0 : (_last.Bytes - first.Bytes) / seconds;
    }

    /// <summary>Time left for <paramref name="remainingBytes"/> at the current speed, or null when the speed is unknown.</summary>
    public TimeSpan? Eta(long remainingBytes, long nowTicks)
    {
        var speed = BytesPerSecond(nowTicks);
        return speed < 1 ? null : TimeSpan.FromSeconds(Math.Max(0, remainingBytes) / speed);
    }
}

public static class TransferText
{
    /// <summary>"12.3MB/s"; empty when the speed is unknown.</summary>
    public static string Speed(double bytesPerSecond) =>
        bytesPerSecond < 1 ? "" : Drives.Format.Bytes((long)bytesPerSecond) + "/s";

    /// <summary>"45 s", "2 min 10 s", "1 h 5 min"; rounds up so it never promises less time than it takes.</summary>
    public static string Eta(TimeSpan? eta)
    {
        if (eta is not { } t) return "";
        var secs = (long)Math.Ceiling(t.TotalSeconds);
        if (secs < 60) return $"{Math.Max(1, secs)} s";
        if (secs < 3600) return $"{secs / 60} min {secs % 60} s";
        return $"{secs / 3600} h {secs % 3600 / 60} min";
    }
}
