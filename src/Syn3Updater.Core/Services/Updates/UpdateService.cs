using System.Text;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

/// <summary>What to do about a release that was found.</summary>
public enum PlanKind
{
    /// <summary>The app downloads and installs it.</summary>
    Install,
    /// <summary>The release is genuine but this copy is updated another way: <see cref="UpdatePlan.Instructions"/> says how.</summary>
    Manual,
    /// <summary>Only tell the user it exists (no signed manifest, no file for this system, or a development run).</summary>
    NotifyOnly,
    /// <summary>The release must NOT be installed (bad signature, not newer, wrong channel): <see cref="UpdatePlan.Reason"/> says why.</summary>
    Rejected
}

/// <param name="Transient">For a NotifyOnly plan: the reason is temporary (the network), so trying again may give an installable plan. A permanent reason (no manifest, no file for this system) never will.</param>
public sealed record UpdatePlan(PlanKind Kind, ReleaseManifest? Manifest, ManifestAsset? Asset, InstallInfo Install, string? Instructions = null, string? Reason = null, bool Transient = false)
{
    public static UpdatePlan Notify(InstallInfo i, string reason, ReleaseManifest? m = null, bool transient = false) => new(PlanKind.NotifyOnly, m, null, i, null, reason, transient);
    public static UpdatePlan Reject(InstallInfo i, string reason) => new(PlanKind.Rejected, null, null, i, null, reason);
}

/// <summary>
/// Turns "a newer release exists" into a decision. The release's manifest is fetched and its signature checked BEFORE anything else is believed;
/// only then is a file chosen for this OS, CPU and kind of install. Never throws for network or format problems: they become a plan.
/// </summary>
public sealed class UpdateService
{
    private const int MaxManifestBytes = 512 * 1024, MaxSignatureBytes = 8 * 1024;
    private readonly HttpClient _http;
    private readonly IReadOnlyList<MinisignKey> _keys;
    private readonly Func<InstallInfo, bool> _canApply;

    /// <param name="canApply">Whether this version of the app can really replace an install of that kind (default: see <see cref="DefaultCanApply"/>).
    /// An install that "may" update itself but cannot yet is shown instructions instead of a button that would fail.</param>
    public UpdateService(HttpClient http, IReadOnlyList<MinisignKey>? keys = null, Func<InstallInfo, bool>? canApply = null)
    {
        _http = http; _keys = keys ?? UpdateKeys.Trusted; _canApply = canApply ?? DefaultCanApply;
    }

    public async Task<UpdatePlan> PlanAsync(UpdateInfo info, InstallInfo install, string current, UpdateChannel wanted, UpdateOs os, UpdateArch arch, CancellationToken ct = default)
    {
        if (install.Route == UpdateRoute.InPlace && !_canApply(install))
            install = install with { Route = UpdateRoute.Manual, Reason = "this version of Syn3 Updater cannot update this kind of install by itself yet" };
        if (install.Route == UpdateRoute.None) return UpdatePlan.Notify(install, install.Reason);
        if (info.ManifestUrl == null || info.SignatureUrl == null) return UpdatePlan.Notify(install, "this release has no signed manifest");
        ReleaseManifest manifest;
        try
        {
            var json = await GetAsync(info.ManifestUrl, MaxManifestBytes, ct);
            var sig = Encoding.UTF8.GetString(await GetAsync(info.SignatureUrl, MaxSignatureBytes, ct));
            manifest = ManifestTrust.Open(json, sig, _keys);
        }
        catch (FormatException e) { return UpdatePlan.Reject(install, e.Message); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested) throw;
            return UpdatePlan.Notify(install, "the signed manifest could not be downloaded: " + e.Message, null, transient: true);
        }

        // as versions, not text: a release published under the legacy tag 3.0.0.0 is announced as "3.0.0.0" while its manifest says "3.0.0", and they are the same version
        if (SemVer.Parse(manifest.Version) is not { } mv || SemVer.Parse(info.Version) is not { } iv || mv.CompareTo(iv) != 0) return UpdatePlan.Reject(install, $"the manifest is for {manifest.Version}, not {info.Version}");
        if (!ManifestTrust.MayInstall(manifest, current, wanted, info.Switch)) return UpdatePlan.Reject(install, $"{manifest.Version} is not an update for {current} on the {wanted} channel");

        var kind = KindFor(install.Kind);
        var asset = kind == null ? null : manifest.Find(os, arch, kind.Value);
        if (asset == null && install.Route == UpdateRoute.InPlace) return UpdatePlan.Notify(install, "this release has no file for this system", manifest);
        if (install.Route == UpdateRoute.InPlace) return new UpdatePlan(PlanKind.Install, manifest, asset, install);

        // updated another way: name the exact command for the file this system needs, or the download page
        return new UpdatePlan(PlanKind.Manual, manifest, asset, install, InstallDetector.ManualInstructions(install.Kind, asset?.Name), install.Reason);
    }

    /// <summary>User-level Linux tar installs and Windows folders: the two the app replaces itself. (Package-managed and macOS installs are shown instructions.)</summary>
    public static bool DefaultCanApply(InstallInfo i) => i.Kind is InstallKind.LinuxTar or InstallKind.WindowsFolder;

    /// <summary>The kind of file each way of installing is updated from.</summary>
    public static AssetKind? KindFor(InstallKind k) => k switch
    {
        InstallKind.WindowsFolder => AssetKind.Zip,
        InstallKind.LinuxTar => AssetKind.TarGz,
        InstallKind.LinuxDeb => AssetKind.Deb,
        InstallKind.LinuxRpm => AssetKind.Rpm,
        InstallKind.LinuxPacman => AssetKind.ArchPkg,
        InstallKind.MacApp => AssetKind.Dmg,
        _ => null
    };

    private async Task<byte[]> GetAsync(string url, int max, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !ReleaseManifest.AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith(ReleaseManifest.AllowedPathPrefix, StringComparison.OrdinalIgnoreCase)) throw new FormatException($"manifest address not allowed: {url}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(30));
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > max) throw new FormatException("manifest is too large");
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        using var ms = new MemoryStream();
        var buf = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > max) throw new FormatException("manifest is too large");
        }
        return ms.ToArray();
    }
}
