using System.Text.RegularExpressions;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
/// What a <c>syn3updater://</c> link asks for: preselect a region, a SYNC release and a map release on the Home page. Nothing else.
/// Links come from web pages, so they can only ever choose among options the app already offers; they never start a download or a format.
/// </summary>
public record UrlIntent(string? Region, string? Release, string? Maps)
{
    /// <summary>The form v2 expected and our website still produces: <c>syn3updater://Region=EU;Release=3.4.19274;Maps=Navigation_Map_Name</c>.</summary>
    public override string ToString() => string.Join(";", new[] { Region is null ? null : $"Region={Region}", Release is null ? null : $"Release={Release}", Maps is null ? null : $"Maps={Maps}" }.Where(s => s != null));
}

public static class UrlIntentParser
{
    public const string Scheme = "syn3updater";
    private const int MaxLength = 2048;
    private static readonly Regex RegionPattern = new("^[A-Za-z]{2,4}$", RegexOptions.Compiled);
    private static readonly Regex ReleasePattern = new(@"^[0-9A-Za-z .\-_]{1,64}$", RegexOptions.Compiled);

    /// <summary>The first command-line argument that is a syn3updater: link (the OS passes it as an ordinary argument), or null.</summary>
    public static string? Find(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.Trim().StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Parses the link. Accepts <c>syn3updater://K=V;K=V</c> (v2) as well as <c>&amp;</c> separators, a leading <c>?</c>, extra or missing slashes and
    /// percent-encoding. Unknown keys are ignored; a value that is too long, has control characters or an unexpected shape drops that key.
    /// Returns null when nothing usable is left.
    /// </summary>
    public static UrlIntent? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim().Trim('"');
        if (raw.Length > MaxLength || !raw.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = raw[(Scheme.Length + 1)..].TrimStart('/').TrimStart('?').TrimEnd('/');

        string? region = null, release = null, maps = null;
        foreach (var part in rest.Split(new[] { ';', '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0) continue;
            var key = part[..eq].Trim();
            var value = Decode(part[(eq + 1)..]);
            if (value == null || value.Length == 0 || value.Any(char.IsControl)) continue;
            if (key.Equals("Region", StringComparison.OrdinalIgnoreCase) && RegionPattern.IsMatch(value)) region = value.ToUpperInvariant();
            else if (key.Equals("Release", StringComparison.OrdinalIgnoreCase) && ReleasePattern.IsMatch(value)) release = value;
            else if (key.Equals("Maps", StringComparison.OrdinalIgnoreCase) && value.Length <= 200) maps = value.Replace('_', ' ');     // v2 wrote spaces as underscores
        }
        return region == null && release == null && maps == null ? null : new UrlIntent(region, release, maps);
    }

    private static string? Decode(string s)
    {
        try { return Uri.UnescapeDataString(s).Trim(); } catch (UriFormatException) { return null; }
    }
}
