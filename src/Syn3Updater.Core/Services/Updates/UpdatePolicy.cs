using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ---- the cached copy ----

    public void Save(string path)
    {
        var o = new JsonObject
        {
            ["fetchedAt"] = FetchedAt.ToString("o"),
            ["rows"] = new JsonArray(Rows.Select(r => (JsonNode)new JsonObject
            {
                ["channel"] = r.Channel.ToString(), ["enabled"] = r.Enabled, ["minAll"] = r.MinAll, ["minWindows"] = r.MinWindows, ["minLinux"] = r.MinLinux, ["minMac"] = r.MinMac, ["message"] = r.Message
            }).ToArray())
        };
        try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, o.ToJsonString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* the cache is a convenience */ }
    }

    public static UpdatePolicy? Load(string path)
    {
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o || o["rows"] is not JsonArray arr) return null;
            var rows = new List<PolicyRow>();
            foreach (var n in arr)
            {
                if (n is not JsonObject r || !Enum.TryParse<UpdateChannel>(r["channel"]?.GetValue<string>(), false, out var ch) || !Enum.IsDefined(ch)) continue;
                rows.Add(new PolicyRow(ch, r["enabled"]?.GetValue<bool>() ?? false, r["minAll"]?.GetValue<string>(), r["minWindows"]?.GetValue<string>(),
                    r["minLinux"]?.GetValue<string>(), r["minMac"]?.GetValue<string>(), r["message"]?.GetValue<string>()));
            }
            return new UpdatePolicy(DateTimeOffset.TryParse(o["fetchedAt"]?.GetValue<string>(), out var at) ? at : default, rows);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
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
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromDays(60);

    /// <summary>
    /// The policy to apply: the live one (and the cache is refreshed), or when the backend cannot be reached the cached one, unless it is too old to be believed. Never throws for network problems.
    /// With neither, null: an unreachable policy never forces anything by itself.
    /// </summary>
    public static async Task<UpdatePolicy?> GetAsync(Func<CancellationToken, Task<UpdatePolicy>> fetch, string cacheFile, DateTimeOffset now, TimeSpan timeout, Action<string>? log = null, CancellationToken ct = default)
    {
        log ??= _ => { };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct); cts.CancelAfter(timeout);
        try
        {
            var live = await fetch(cts.Token);
            live.Save(cacheFile);
            return live;
        }
        catch (Exception e) when (e is ApiException or HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            log("The update policy could not be fetched: " + e.Message);
        }
        var cached = UpdatePolicy.Load(cacheFile);
        if (cached == null) { log("No cached update policy: nothing is forced"); return null; }
        if (now - cached.FetchedAt > MaxCacheAge) { log("The cached update policy is too old to use"); return null; }
        log($"Using the update policy cached on {cached.FetchedAt:yyyy-MM-dd}");
        return cached;
    }
}
