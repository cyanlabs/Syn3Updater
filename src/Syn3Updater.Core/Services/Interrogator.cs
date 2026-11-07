using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Cyanlabs.Syn3Updater.Core.Services;

public class PartitionHealth { public string Type = ""; public string Total = ""; public string Available = ""; }
public class DidEntry { public string Type = ""; public string Value = ""; public string Response = ""; }

public class InterrogatorResult
{
    /// <summary>What <see cref="ApimModel"/> holds when the log has no "ECU Delivery Assembly Number" (some logs carry none).</summary>
    public const string UnknownModel = "Unknown";
    public bool ModelKnown => ApimModel != UnknownModel;
    public string Vin = "";
    public string ApimModel = "";
    public string SoftwareName = "";          // "Embedded Consumer Operating System Part Number"
    public int ApimSizeGb;
    public bool Nav;
    public string ApimFree = "Unknown";
    public DateTimeOffset? Generated;
    public List<PartitionHealth> Partitions = new();
    public List<string> Packages = new();
    public List<DidEntry> AsBuilt = new();
    public XDocument? AsBuiltXml;
}

/// <summary>One partition of the APIM as the Home/Utility result shows it: how full it is, with the sizes as the log wrote them ("21M", "3.7G").</summary>
public record PartitionUsage(string Path, string Free, string Total, double Used);

/// <summary>The sizes in an Interrogator log: a number and a unit letter, "396K", "5.7M", "1.2G", "54G".</summary>
public static class SizeText
{
    /// <summary>The size in bytes (units are powers of 1024), or null when the text is not a size.</summary>
    public static double? ToBytes(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length < 2) return null;
        var scale = char.ToUpperInvariant(t[^1]) switch { 'B' => 1d, 'K' => 1024d, 'M' => 1024d * 1024, 'G' => 1024d * 1024 * 1024, 'T' => 1024d * 1024 * 1024 * 1024, _ => 0d };
        if (scale == 0 || !double.TryParse(t[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0) return null;
        return n * scale;
    }
}

public class InterrogatorException(string message) : Exception(message);

/// <summary>Parses an Interrogator log XML (namespace-agnostic) as written by the Sync 3 log tool.</summary>
public static class Interrogator
{
    public static string RemoveInvalidXmlChars(string text) =>
        new(text.Where(XmlConvert.IsXmlChar).ToArray());

    public static InterrogatorResult Parse(string xml, bool requireModel = false)
    {
        XDocument doc;
        try { doc = XDocument.Parse(RemoveInvalidXmlChars(xml)); }
        catch (XmlException e) { throw new InterrogatorException("Not a valid Interrogator log: " + e.Message); }

        XElement? Q(XElement? from, string name) => from?.Descendants().FirstOrDefault(e => e.Name.LocalName == name);
        var root = doc.Root;
        if (root == null || root.Name.LocalName != "OTAModuleSnapShot") throw new InterrogatorException("Not an Interrogator log.");

        var r = new InterrogatorResult { Vin = Q(root, "VIN")?.Value ?? "" };
        var dids = root.Descendants().Where(e => e.Name.LocalName == "DID").Select(e => new DidEntry
        {
            Type = (string?)e.Attribute("didType") ?? "",
            Value = (string?)e.Attribute("didValue") ?? "",
            Response = e.Elements().FirstOrDefault(c => c.Name.LocalName == "Response")?.Value ?? "",
        }).ToList();
        if (dids.Count == 0) throw new InterrogatorException("The log contains no DID data.");

        r.SoftwareName = dids.FirstOrDefault(d => d.Type == "Embedded Consumer Operating System Part Number")?.Response ?? "";
        r.ApimModel = dids.FirstOrDefault(d => d.Type == "ECU Delivery Assembly Number")?.Response ?? "";
        if (r.ApimModel == "" && requireModel) throw new InterrogatorException("MODEL_NOT_FOUND");
        if (r.ApimModel == "") r.ApimModel = InterrogatorResult.UnknownModel;

        var attrs = Q(root, "AdditionalAttributes");
        if (DateTimeOffset.TryParse((string?)attrs?.Attribute("logGeneratedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var gen)) r.Generated = gen;
        r.Partitions = root.Descendants().Where(e => e.Name.LocalName == "PartitionHealth").Select(e => new PartitionHealth
        {
            Type = (string?)e.Attribute("type") ?? "", Total = (string?)e.Attribute("total") ?? "", Available = (string?)e.Attribute("available") ?? "",
        }).ToList();

        var images = r.Partitions.FirstOrDefault(p => p.Type == "/fs/images/");
        if (images != null)
        {
            r.ApimFree = images.Available;
            if (double.TryParse(images.Total[..^1], NumberStyles.Any, CultureInfo.InvariantCulture, out var gb))
            {
                (r.Nav, r.ApimSizeGb) = gb switch
                {
                    >= 0 and <= 8 => (false, 8),
                    >= 9 and <= 16 => (false, 16),
                    >= 17 and <= 32 => (true, 32),
                    >= 33 and <= 64 => (true, 64),
                    _ => (false, 0),
                };
            }
        }

        foreach (var hex in new[] { "8060", "8061" })
        {
            var resp = dids.FirstOrDefault(d => d.Value == hex)?.Response;
            if (!string.IsNullOrEmpty(resp)) r.Packages.AddRange(HexToAscii.ConvertPackages(resp));
        }

        r.AsBuilt = dids.Where(d => d.Type.Contains("Direct Configuraation DID DE")).ToList();
        r.AsBuiltXml = BuildAsBuiltXml(r);
        return r;
    }

    private static XDocument BuildAsBuiltXml(InterrogatorResult r) =>
        new(new XElement("DirectConfiguration",
            new XElement("VEHICLE",
                new XAttribute("MODULE", "Syn3Updater"), new XAttribute("VIN", ""),
                new XAttribute("VEHICLE_ID", r.ApimModel), new XAttribute("VEHICLE_YEAR", r.Generated?.ToString() ?? ""),
                r.AsBuilt.Select(d => new XElement("DID", new XAttribute("ID", d.Value), d.Response.ToUpperInvariant())))));

    /// <summary>Every partition with the share of it that is used (0 to 1; 0 when a size cannot be read), in the order of the log.</summary>
    public static List<PartitionUsage> Usage(InterrogatorResult r) =>
        r.Partitions.Select(x =>
        {
            var free = SizeText.ToBytes(x.Available); var total = SizeText.ToBytes(x.Total);
            var used = free is { } f && total is > 0 ? Math.Clamp(1 - f / total.Value, 0, 1) : 0;
            return new PartitionUsage(x.Type, x.Available, x.Total, used);
        }).ToList();

    /// <summary>The installed package numbers, without the empty entries the 00-padded hex leaves behind.</summary>
    public static List<string> PackageList(InterrogatorResult r) => r.Packages.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

    /// <summary>Human readable summary blocks (details, partitions + packages, as-built), like upstream's three text panes.</summary>
    public static (string Details, string Partitions, string AsBuilt) Describe(InterrogatorResult r, string? friendlyVersion)
    {
        string nl = Environment.NewLine;
        var d = new StringBuilder($"VIN: {r.Vin}{nl}APIM Model: {r.ApimModel}{nl}");
        d.Append($"APIM Type: {(r.Nav ? "Navigation" : "Non-Navigation")}{nl}APIM Size: {r.ApimSizeGb}GB{nl}APIM Free: {r.ApimFree}{nl}");
        if (friendlyVersion != null) d.Append($"Version {friendlyVersion} ({r.SoftwareName}){nl}");
        if (r.Generated != null) d.Append(r.Generated.Value.ToString("u"));

        var p = new StringBuilder($"Partition Type = Free / Total{nl}");
        foreach (var x in r.Partitions) p.Append($"{x.Type} = {x.Available} / {x.Total}{nl}");
        p.Append($"{nl}Installed Packages{nl}");
        foreach (var pkg in r.Packages) p.Append(pkg + nl);

        var a = new StringBuilder("APIM AsBuilt");
        foreach (var x in r.AsBuilt) a.Append($"{nl}{x.Value}: {x.Response.ToUpperInvariant()}");
        return (d.ToString(), p.ToString(), a.ToString());
    }
}

public static class HexToAscii
{
    public static List<string> ConvertPackages(string hex)
    {
        var sb = new StringBuilder();
        for (int a = 0; a + 1 < hex.Length; a += 2)
        {
            var pair = hex.Substring(a, 2);
            sb.Append(pair == "00" ? '_' : (char)Convert.ToInt32(pair, 16));
        }
        return Regex.Replace(sb.ToString(), "_*_", "_").Split('_').ToList();
    }
}
