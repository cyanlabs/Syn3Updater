using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates;

public enum UpdateOs { Windows, Linux, MacOs }
public enum UpdateArch { X64, Arm64 }

/// <summary>What a downloadable file is: a Windows zip, a Linux tar.gz or package, or a zipped macOS app.</summary>
public enum AssetKind { Zip, TarGz, Deb, Rpm, ArchPkg, MacAppZip, Dmg }

public sealed record ManifestAsset(UpdateOs Os, UpdateArch Arch, AssetKind Kind, string Name, string Url, long Size, string Sha256);

/// <summary>
/// The signed description of one release (manifest.json, signed as manifest.json.minisig): its version and channel, release notes and every
/// file, with the size and SHA-256 the download must match. The app trusts nothing else about a release.
/// </summary>
public sealed class ReleaseManifest
{
    public const int Schema = 1;
    public string Version { get; init; } = "";
    public UpdateChannel Channel { get; init; }
    public DateTimeOffset Published { get; init; }
    public string Notes { get; init; } = "";
    public IReadOnlyList<ManifestAsset> Assets { get; init; } = Array.Empty<ManifestAsset>();

    private static readonly Regex Hex64 = new("^[0-9a-f]{64}$", RegexOptions.Compiled);
    private static readonly Regex SafeName = new(@"^[A-Za-z0-9][A-Za-z0-9._+\-]*$", RegexOptions.Compiled);
    public static readonly string[] AllowedHosts = { "github.com" };
    /// <summary>Downloads must come from this repository's releases, not any other repository on the allowed host.</summary>
    public static readonly string AllowedPathPrefix = $"/{UpdateChecker.Repo}/releases/download/";
    private const long MaxAsset = 600L * 1024 * 1024;

    /// <summary>
    /// Reads and checks a manifest. Anything unexpected is an error: a manifest is only ever produced by the release workflow, so a wrong value is
    /// a bug or an attack and the update is refused, not repaired.
    /// </summary>
    public static ReleaseManifest Parse(byte[] json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatException("manifest is not an object");
            if (r.GetProperty("schema").GetInt32() != Schema) throw new FormatException("this manifest is from a newer release format");
            var version = r.GetProperty("version").GetString() ?? "";
            var sv = SemVer.Parse(version) ?? throw new FormatException($"bad version '{version}'");
            if (version.StartsWith('v') || version.StartsWith('V')) throw new FormatException("version must not start with 'v'");
            var channel = Enum.Parse<UpdateChannel>(r.GetProperty("channel").GetString() ?? "", ignoreCase: false);
            if (Channels.Of(version) != channel) throw new FormatException($"version {version} does not belong to channel {channel}");
            var assets = new List<ManifestAsset>();
            foreach (var a in r.GetProperty("assets").EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!SafeName.IsMatch(name) || name.Contains("..")) throw new FormatException($"unsafe file name '{name}'");
                var url = a.GetProperty("url").GetString() ?? "";
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                    || !AllowedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase)
                    || !uri.AbsolutePath.StartsWith(AllowedPathPrefix, StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.Contains("..")) throw new FormatException($"download address not allowed: {url}");
                var size = a.GetProperty("size").GetInt64();
                if (size <= 0 || size > MaxAsset) throw new FormatException($"implausible size for {name}");
                var sha = (a.GetProperty("sha256").GetString() ?? "").ToLowerInvariant();
                if (!Hex64.IsMatch(sha)) throw new FormatException($"bad sha256 for {name}");
                assets.Add(new ManifestAsset(ParseOs(a.GetProperty("os").GetString()), ParseArch(a.GetProperty("arch").GetString()),
                    ParseKind(a.GetProperty("kind").GetString()), name, url, size, sha));
            }
            if (assets.Count == 0) throw new FormatException("manifest lists no files");
            if (assets.GroupBy(x => (x.Os, x.Arch, x.Kind)).Any(g => g.Count() > 1)) throw new FormatException("manifest lists the same file type twice");
            var published = r.TryGetProperty("published", out var p) && p.ValueKind == JsonValueKind.String ? p.GetDateTimeOffset() : default;
            var notes = r.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
            _ = sv;
            return new ReleaseManifest { Version = version, Channel = channel, Published = published, Notes = notes, Assets = assets };
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException or OverflowException)
        {
            throw new FormatException("manifest is not valid: " + e.Message, e);
        }
    }

    /// <summary>The release tag a file is published under (the folder in its download address: v3.1.0, 2.99.99.5, nightly), or null.</summary>
    public static string? TagOf(ManifestAsset asset) =>
        Uri.TryCreate(asset.Url, UriKind.Absolute, out var u) && u.AbsolutePath.StartsWith(AllowedPathPrefix, StringComparison.OrdinalIgnoreCase)
            ? u.AbsolutePath[AllowedPathPrefix.Length..].Split('/')[0] is { Length: > 0 } t ? Uri.UnescapeDataString(t) : null : null;

    /// <summary>The file for this OS, CPU and kind of install, or null when the release has none.</summary>
    public ManifestAsset? Find(UpdateOs os, UpdateArch arch, AssetKind kind) => Assets.FirstOrDefault(a => a.Os == os && a.Arch == arch && a.Kind == kind);

    public static UpdateOs CurrentOs() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? UpdateOs.Windows : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? UpdateOs.MacOs : UpdateOs.Linux;

    /// <summary>The CPU the running PROGRAM is built for (not the machine's): an x64 build under emulation must be updated with an x64 build.</summary>
    public static UpdateArch CurrentArch() => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? UpdateArch.Arm64 : UpdateArch.X64;

    private static UpdateOs ParseOs(string? s) => s switch { "windows" => UpdateOs.Windows, "linux" => UpdateOs.Linux, "macos" => UpdateOs.MacOs, _ => throw new FormatException($"unknown os '{s}'") };
    private static UpdateArch ParseArch(string? s) => s switch { "x64" => UpdateArch.X64, "arm64" => UpdateArch.Arm64, _ => throw new FormatException($"unknown arch '{s}'") };
    private static AssetKind ParseKind(string? s) => s switch
    {
        "zip" => AssetKind.Zip, "targz" => AssetKind.TarGz, "deb" => AssetKind.Deb, "rpm" => AssetKind.Rpm, "arch" => AssetKind.ArchPkg,
        "app-zip" => AssetKind.MacAppZip, "dmg" => AssetKind.Dmg, _ => throw new FormatException($"unknown kind '{s}'")
    };
}

/// <summary>Checks a manifest and its signature together, and decides whether it may replace the running version.</summary>
public static class ManifestTrust
{
    /// <summary>Verifies <paramref name="signature"/> over <paramref name="json"/> with the trusted keys, parses it, and requires the signed trusted comment to name this version.</summary>
    public static ReleaseManifest Open(byte[] json, string signature, IEnumerable<MinisignKey> keys)
    {
        var v = Minisign.Verify(json, signature, keys);
        if (!v.Valid) throw new FormatException("manifest signature rejected: " + v.Error);
        var m = ReleaseManifest.Parse(json);
        // the signature also covers a comment written by CI ("version:3.0.0"), so a validly signed manifest of one release cannot be passed off as another
        if (v.TrustedComment == null || !Regex.IsMatch(v.TrustedComment, $@"(^|\s)version:{Regex.Escape(m.Version)}(\s|$)"))
            throw new FormatException("manifest signature was made for a different version");
        return m;
    }

    /// <summary>
    /// Whether to move from <paramref name="current"/> to the manifest's release. Never to an older or equal version, unless the user explicitly chose a
    /// different channel (<paramref name="explicitSwitch"/>); then only to a release of that channel.
    /// </summary>
    public static bool MayInstall(ReleaseManifest m, string current, UpdateChannel wanted, bool explicitSwitch)
    {
        var running = SemVer.Parse(current); var target = SemVer.Parse(m.Version);
        if (running == null || target == null) return false;
        if (!Channels.Accepts(wanted, m.Channel)) return false;
        return target.CompareTo(running) > 0 || (explicitSwitch && Channels.Of(current) != m.Channel && target.CompareTo(running) != 0);
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}
