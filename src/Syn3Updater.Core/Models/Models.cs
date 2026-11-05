using System.Text.Json.Serialization;

namespace Cyanlabs.Syn3Updater.Core.Models;

/// <summary>A package (IVSU) as used by the download queue.</summary>
public class Ivsu
{
    public bool Selected { get; set; } = true;
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Md5 { get; set; }
    public string FileName { get; set; } = "";
    public string? Source { get; set; }
    public long FileSize { get; set; }
}

public record Region(string Code, string Name)
{
    public static readonly IReadOnlyList<Region> All = new[]
    {
        new Region("EU", "Europe"),
        new Region("NA", "United States, Canada & Mexico"),
        new Region("CN", "China"),
        new Region("ANZ", "Australia, New Zealand, South America, Turkey & Taiwan"),
        new Region("ROW", "Middle East, Africa, India, Sri Lanka, Israel, South East Asia, Caribbean & Central America"),
    };
    public override string ToString() => $"{Code} - {Name}";
}

/// <summary>Install modes the updater can generate files for.</summary>
public static class InstallModes
{
    public const string Autoinstall = "autoinstall", Reformat = "reformat", Downgrade = "downgrade";
}

/// <summary>Special-purpose package keys on the API.</summary>
public static class SpecialPackages
{
    public const string DowngradeApp = "downgradeapp",
        DowngradeTool = "gracenotesremoval",
        LogToolAD = "logtool34-2",
        LogToolAC = "logtool34",
        LogToolAB = "logtool32",
        LogToolAA = "logtool30",
        GraceNotesRemoval = "gracenotesremoval",
        SmallVoice = "smallvoice",
        Reformat = "reformat",
        RWDataCleaner = "rwdatacleaner",
        Upgrade22App = "upgrade22";
}

// ---- API (GraphQL) response shapes ----
public class ReleasesRoot
{
    [JsonPropertyName("releases")] public List<Release>? Releases { get; set; }
    [JsonPropertyName("map_releases")] public List<Release>? MapReleases { get; set; }
}

public class IvsuRoot { [JsonPropertyName("ivsu")] public List<ApiIvsu>? Ivsus { get; set; } }
public class NoticesRoot { [JsonPropertyName("notices")] public List<Notice>? Notices { get; set; } }
public class UserAgentRoot { [JsonPropertyName("useragents")] public List<UserAgent>? UserAgents { get; set; } }
public class My20ModelsRoot { [JsonPropertyName("my20models")] public List<My20Model>? Models { get; set; } }
public class My20Model { [JsonPropertyName("model")] public string Model { get; set; } = ""; }

public class Release
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("map_version")] public string? MapVersion { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("regions")] public List<string>? Regions { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("feedbackurl")] public string? FeedbackUrl { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("ivsus")] public List<ReleaseIvsu>? Ivsus { get; set; }
    [JsonPropertyName("esn")] public bool Esn { get; set; }
    [JsonIgnore] public bool IsEnabled { get; set; } = true;
    public override string ToString() => Name;
}

public class ReleaseIvsu
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("ivsu")] public ApiIvsu? Ivsu { get; set; }
    [JsonPropertyName("map_ivsu")] public ApiIvsu? MapIvsu { get; set; }
}

public class ApiIvsu
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("regions")] public List<string>? Regions { get; set; }
    [JsonPropertyName("md5")] public string? Md5 { get; set; }
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("filesize")] public long FileSize { get; set; }
    [JsonPropertyName("source")] public string? Source { get; set; }
    [JsonPropertyName("specialpackage")] public string? SpecialPackage { get; set; }

    public Ivsu ToIvsu() => new()
    {
        Type = Type, Name = Name, Version = Version ?? "", Notes = Notes ?? "", Url = Url, Md5 = Md5,
        Selected = true, FileSize = FileSize, Source = Source,
        FileName = UrlToFileName(Url),
    };

    public static string UrlToFileName(string url)
    {
        var noQuery = url.Split('?')[0];
        return noQuery[(noQuery.LastIndexOf('/') + 1)..];
    }
}

public class UserAgent
{
    [JsonPropertyName("useragent")] public string Value { get; set; } = "";
    [JsonPropertyName("min")] public int Min { get; set; }
    [JsonPropertyName("max")] public int Max { get; set; }
}

public class Notice
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("date_created")] public string? DateCreated { get; set; }
    [JsonPropertyName("date_updated")] public string? DateUpdated { get; set; }
    [JsonPropertyName("notice")] public string? Content { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("important")] public bool Important { get; set; }
}

public class ChangelogRoot { [JsonPropertyName("data")] public List<Changelog>? Data { get; set; } }

public class Changelog
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("changelog")] public string? ReleaseNotes { get; set; }
    [JsonPropertyName("branch")] public string? Branch { get; set; }
}

// ---- Settings ----
public class MainSettings
{
    public string? Lang { get; set; }
    public string? DownloadPath { get; set; }
    public string LicenseKey { get; set; } = "";
    public bool DisclaimerAccepted { get; set; }
    public string? Theme { get; set; }
    public string? LogPath { get; set; }
    public string Profile { get; set; } = "default";
    /// <summary>Bearer token for the CyanLabs API. SYN3_API_TOKEN overrides this.</summary>
    public string? ApiToken { get; set; }
    /// <summary>Sponsor/ad banner: image to show and the page it opens. When both are empty a placeholder is shown if ShowAdSlot is true.</summary>
    public string? AdImageUrl { get; set; }
    public string? AdLinkUrl { get; set; }
    public bool ShowAdSlot { get; set; } = true;
}

public class ProfileSettings
{
    public string? CurrentRegion { get; set; }
    public int? CurrentVersion { get; set; } = 0;
    public bool CurrentNav { get; set; }
    public bool? My20v2 { get; set; }
    public string InstallMode { get; set; } = "autodetect";
    public int DownloadConnections { get; set; } = 1;

    /// <summary>Formats 3200000-style version as 3.2.00000 (7 digits) else 0.0.00000.</summary>
    public string VersionString => FormatVersion(CurrentVersion);

    public static string FormatVersion(int? v)
    {
        var s = (v ?? 0).ToString();
        return s.Length == 7 ? $"{s[0]}.{s[1]}.{s[2..]}" : "0.0.00000";
    }
}
