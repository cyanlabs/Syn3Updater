using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

public sealed record PolicyRow(UpdateChannel Channel, bool Enabled, string? MinAll, string? MinWindows, string? MinLinux, string? MinMac, string? Message);

/// <summary>The oldest version that may still run, and the maintainers' explanation (shown to the user).</summary>
public sealed record PolicyRequirement(string Version, string? Message);

/// <summary>
/// What the backend says about which versions may still run: one row per update channel with a minimum for every OS and optional per-OS overrides. A version below the minimum
/// for its channel and OS is updated at the next launch. Values that are not versions are ignored (the backend validates them, but this is also checked here), and a row that is not enabled says nothing.
/// </summary>
public sealed class UpdatePolicy
{
    public DateTimeOffset FetchedAt { get; }
    public IReadOnlyList<PolicyRow> Rows { get; }
    public UpdatePolicy(DateTimeOffset fetchedAt, IReadOnlyList<PolicyRow> rows) { FetchedAt = fetchedAt; Rows = rows; }

    public static UpdatePolicy FromApi(UpdatePolicyRoot root, DateTimeOffset now) => new(now, (root.Rows ?? new()).Select(r =>
        Enum.TryParse<UpdateChannel>(r.Channel, false, out var ch) && Enum.IsDefined(ch)
            ? new PolicyRow(ch, r.Enabled, r.MinVersionAll, r.MinVersionWindows, r.MinVersionLinux, r.MinVersionMacos, r.Message) : null)
        .Where(r => r != null).Select(r => r!).ToList());

    /// <summary>The minimum for builds on <paramref name="channel"/> running on <paramref name="os"/>: the higher of the all-OS value and the OS's own, or null for none.</summary>
    public PolicyRequirement? MinimumFor(UpdateChannel channel, UpdateOs os)
    {
        var row = Rows.FirstOrDefault(r => r.Channel == channel && r.Enabled);
        if (row == null) return null;
        var own = os switch { UpdateOs.Windows => row.MinWindows, UpdateOs.Linux => row.MinLinux, _ => row.MinMac };
        string? best = null; SemVer? bestVer = null;
        foreach (var c in new[] { row.MinAll, own })
        {
            if (SemVer.Parse(c) is not { } v || string.IsNullOrWhiteSpace(c)) continue;
            if (bestVer == null || v.CompareTo(bestVer) > 0) { best = c!.Trim(); bestVer = v; }
        }
        return best == null ? null : new PolicyRequirement(best, string.IsNullOrWhiteSpace(row.Message) ? null : row.Message.Trim());
    }
}

public sealed record ForceDecision(bool Required, PolicyRequirement? Requirement, string Reason);

public static class ForcedUpdate
{
    /// <summary>
    /// Whether the running version must be updated now. Only when it is below the minimum for its channel and OS AND a release that meets the minimum really exists:
    /// a minimum above the newest release (a typo, or a release that was pulled) is ignored, so a mistake in the backend can never lock everyone out.
    /// </summary>
    /// <param name="newestVersion">The newest release on the channel, or null when none is known.</param>
    public static ForceDecision Evaluate(UpdatePolicy? policy, string running, UpdateChannel channel, UpdateOs os, string? newestVersion)
    {
        if (policy == null) return new(false, null, "no policy known");
        var req = policy.MinimumFor(channel, os);
        if (req == null) return new(false, null, "no minimum for this channel and OS");
        if (SemVer.Parse(running) is not { } run) return new(false, req, "the running version is not a version number");
        var min = SemVer.Parse(req.Version)!;
        if (run.CompareTo(min) >= 0) return new(false, req, $"{running} meets the minimum {req.Version}");
        if (SemVer.Parse(newestVersion) is not { } newest) return new(false, req, $"the minimum {req.Version} cannot be met: no release is known");
        if (newest.CompareTo(min) < 0) return new(false, req, $"the minimum {req.Version} is above the newest release {newestVersion}: ignored");
        if (newest.CompareTo(run) <= 0) return new(false, req, "there is nothing newer to update to");
        return new(true, req, $"{running} is below the minimum {req.Version}");
    }
}

public static class PolicySource
{
    /// <summary>
    /// The live policy, or null when it cannot be fetched in time. Never throws for network problems. Nothing is remembered: with the backend unreachable the app cannot do anything
    /// useful anyway (it says so itself), and a stale copy could only force an update the maintainers have since withdrawn. A policy that fails to load while the rest of the backend works
    /// (a misconfigured table, say) must not lock anyone out, so no policy means nothing is forced; the next start asks again.
    /// </summary>
    public static async Task<UpdatePolicy?> GetAsync(Func<CancellationToken, Task<UpdatePolicy>> fetch, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        try { return await fetch(cts.Token); }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            log("The update policy could not be fetched, so nothing is forced: " + e.Message);
            return null;
        }
    }
}
