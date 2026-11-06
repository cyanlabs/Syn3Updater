using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Models;

namespace Cyanlabs.Syn3Updater.Core.Services;

/// <summary>Loads and saves main settings plus the active profile as JSON. Corrupt files are replaced with defaults.</summary>
public class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly AppPaths _paths;
    public MainSettings Main { get; private set; } = new();
    public ProfileSettings Profile { get; private set; } = new();

    public SettingsStore(AppPaths paths) { _paths = paths; }

    public void Load()
    {
        Main = ReadOrNew<MainSettings>(_paths.MainConfigFile);
        if (string.IsNullOrWhiteSpace(Main.Profile)) Main.Profile = "default";
        Profile = ReadOrNew<ProfileSettings>(_paths.ProfileFile(Main.Profile));
        MigrateProfile();
        if (string.IsNullOrWhiteSpace(Main.DownloadPath)) Main.DownloadPath = AppPaths.DefaultDownloadDir();
        if (string.IsNullOrWhiteSpace(Main.LogPath)) Main.LogPath = _paths.DefaultLogDir;
        TryCreate(Main.DownloadPath); TryCreate(Main.LogPath);
    }

    public void SwitchProfile(string name)
    {
        Save();
        Main.Profile = AppPaths.FileSafe(name);
        Profile = ReadOrNew<ProfileSettings>(_paths.ProfileFile(Main.Profile));
        MigrateProfile();
        Save();
    }

    /// <summary>
    /// Brings a profile written by v2 into line. (An unknown install mode is already turned into auto-detect by ProfileSettings.) The download connections v2 kept in
    /// the profile move to the application settings, unless those already hold a value of their own (then the profile's is simply dropped).
    /// </summary>
    private void MigrateProfile()
    {
        if (Profile.LegacyDownloadConnections is not int legacy) return;
        if (!HasKey(_paths.MainConfigFile, nameof(MainSettings.DownloadConnections))) Main.DownloadConnections = Math.Clamp(legacy, 1, 8);
        Profile.LegacyDownloadConnections = null;
    }

    private static bool HasKey(string file, string name)
    {
        try { return File.Exists(file) && JsonDocument.Parse(File.ReadAllText(file)).RootElement.TryGetProperty(name, out _); }
        catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { return false; }
    }

    public IEnumerable<string> ListProfiles() =>
        Directory.EnumerateFiles(_paths.ProfileDir, "*.json").Select(Path.GetFileNameWithoutExtension).Select(n => n!).Order();

    public void DeleteProfile(string name)
    {
        var f = _paths.ProfileFile(name);
        if (File.Exists(f)) File.Delete(f);
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(_paths.MainConfigFile, JsonSerializer.Serialize(Main, Json));
            File.WriteAllText(_paths.ProfileFile(Main.Profile), JsonSerializer.Serialize(Profile, Json));
        }
        catch (IOException) { /* another process holds the file; try again on next save */ }
    }

    public void Reset()
    {
        if (File.Exists(_paths.MainConfigFile)) File.Delete(_paths.MainConfigFile);
        if (File.Exists(_paths.ProfileFile(Main.Profile))) File.Delete(_paths.ProfileFile(Main.Profile));
        Main = new(); Profile = new();
        Load();
    }

    private static T ReadOrNew<T>(string file) where T : new()
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<T>(File.ReadAllText(file)) ?? new T();
        }
        catch (JsonException) { try { File.Delete(file); } catch (IOException) { } }
        return new T();
    }

    private static void TryCreate(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch (Exception) { /* surfaced later when used */ }
    }
}
