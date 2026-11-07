using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cyanlabs.Syn3Updater.Core.Services.Updates.Swap;

/// <summary>
/// Remembers which files an update installed, so the next one can remove exactly the ones the new version no longer ships. The file name and JSON
/// ({"Version":"3.0.0.0","Files":["..."]}) are the Launcher's own, so the Launcher and this updater can follow each other's installs.
/// </summary>
public sealed class InstallManifest
{
    public const string FileName = "install-manifest.json";
    public string Version { get; set; } = "";
    public List<string> Files { get; set; } = new();

    public static InstallManifest? Load(string installDir)
    {
        try
        {
            var path = Path.Combine(installDir, FileName);
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o) return null;
            var m = new InstallManifest { Version = o["Version"]?.GetValue<string>() ?? "" };
            if (o["Files"] is JsonArray a) foreach (var f in a) if (f?.GetValue<string>() is { } s) m.Files.Add(s);
            return m;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public void Save(string installDir)
    {
        var o = new JsonObject { ["Version"] = Version, ["Files"] = new JsonArray(Files.Select(f => (JsonNode)JsonValue.Create(f)!).ToArray()) };
        File.WriteAllText(Path.Combine(installDir, FileName), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}

public static class StalePlanner
{
    /// <summary>
    /// Files in the install folder that the new version does not ship and that should therefore go.
    /// With a manifest: exactly the previously installed files that are no longer part of the package.
    /// Without one (an install made by the old v2 launcher): the old launcher's own rule, the .dll files next to the program and the Languages folder.
    /// Files the updater never installed (user files, the app's own data) are never listed, and nothing outside the install folder ever is.
    /// </summary>
    public static IReadOnlyList<string> Compute(string installDir, InstallManifest? previous, IEnumerable<string> newFiles)
    {
        var keep = new HashSet<string>(newFiles, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> candidates;
        if (previous is { Files.Count: > 0 }) candidates = previous.Files.Where(SafePath.IsSafeRelative);
        else
            // what the v2 layout had: DLLs next to the program and the Languages folder. Never a DLL in a sub-folder (those belong to something else), however it came to be here.
            candidates = PackageStager.ListFiles(installDir)
                .Where(f => !f.StartsWith(InstallTransaction.BackupDirName + "/", StringComparison.OrdinalIgnoreCase) && !f.StartsWith(InstallTransaction.StagingDirName + "/", StringComparison.OrdinalIgnoreCase))
                .Where(f => (!f.Contains('/') && f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) || f.StartsWith("Languages/", StringComparison.OrdinalIgnoreCase));
        return candidates.Where(f => !keep.Contains(f) && File.Exists(Path.Combine(installDir, f.Replace('/', Path.DirectorySeparatorChar)))).ToList();
    }
}
