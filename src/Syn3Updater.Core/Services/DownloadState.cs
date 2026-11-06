using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>
///     What is needed to continue an interrupted download: which file it was, and which chunks are already safely on disk.
///     Lives next to the partial file as "&lt;name&gt;.part.json". A missing, unreadable or inconsistent state just means "start over".
/// </summary>
internal sealed class DownloadState
{
    public int Version { get; set; } = 1;
    /// <summary>The URL without its query string (signed links change between runs).</summary>
    public string Url { get; set; } = "";
    public long Length { get; set; }
    public long ChunkSize { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    /// <summary>Indexes of chunks that were completely written and flushed to disk.</summary>
    public List<int> Done { get; set; } = new();

    public static string PathFor(string partFile) => partFile + ".json";

    public static string Identity(string url)
    {
        try { return new Uri(url).GetLeftPart(UriPartial.Path); }
        catch (UriFormatException) { return url; }
    }

    [JsonIgnore] public int ChunkCount => ChunkSize <= 0 ? 0 : (int)((Length + ChunkSize - 1) / ChunkSize);
    public long ChunkStart(int index) => index * ChunkSize;
    public long ChunkLength(int index) => Math.Min(ChunkSize, Length - ChunkStart(index));

    public static DownloadState? Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<DownloadState>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Written to a temporary file and renamed, so a crash while saving can never leave half a state file.</summary>
    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        File.Move(tmp, path, true);
    }

    /// <summary>True when the stored state is internally consistent and describes this very remote file.</summary>
    public bool Matches(string url, RemoteInfo remote) =>
        Version == 1 && Identity(url) == Url && Length == remote.Length && ChunkSize > 0 && ChunkCount > 0
        && Done.All(i => i >= 0 && i < ChunkCount) && Done.Distinct().Count() == Done.Count
        && (!string.IsNullOrEmpty(ETag) && !string.IsNullOrEmpty(remote.ETag) ? ETag == remote.ETag : ETag == remote.ETag && LastModified == remote.LastModified);
}

/// <summary>What a HEAD request tells us about the file.</summary>
internal sealed record RemoteInfo(long Length, string? ETag, string? LastModified);

/// <summary>The server answered a "continue from here" request with something that means the partial file cannot be used.</summary>
internal sealed class ResumeRejectedException : IOException
{
    public ResumeRejectedException(string why) : base("Cannot resume: " + why) { }
}
