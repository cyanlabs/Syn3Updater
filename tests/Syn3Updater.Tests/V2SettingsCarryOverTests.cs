using System.Text.Json;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>Settings written by v2 must mean the same thing in v3: the install mode (a wrong one can build the wrong USB), MY20 protection, and where the download connections live.</summary>
public class V2SettingsCarryOverTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3v2").FullName;
    public void Dispose() => Directory.Delete(_dir, true);

    // v2's own profile file, as pasted: the install mode is the combobox text "Auto-Detect"
    private const string V2Profile = """
        {
          "CurrentRegion": "EU",
          "CurrentVersion": 3426057,
          "CurrentNav": false,
          "My20v2": null,
          "InstallMode": "Auto-Detect",
          "DownloadConnections": 8,
          "VersionString": "3.4.26057"
        }
        """;

    private SettingsStore LoadWith(string profileJson, string? mainJson = null)
    {
        var paths = new AppPaths(_dir);
        File.WriteAllText(paths.ProfileFile("default"), profileJson);
        if (mainJson != null) File.WriteAllText(paths.MainConfigFile, mainJson);
        var store = new SettingsStore(paths); store.Load(); return store;
    }

    [Fact]
    public void TheV2ProfileSample_LoadsWithAutoDetect_NotAForcedModeCalledAutoDetect()
    {
        var s = LoadWith(V2Profile);
        Assert.Equal(InstallModes.AutoDetect, s.Profile.InstallMode); Assert.False(InstallModes.IsForced(s.Profile.InstallMode));
        Assert.Equal("EU", s.Profile.CurrentRegion); Assert.Equal(3426057, s.Profile.CurrentVersion); Assert.False(s.Profile.CurrentNav); Assert.Null(s.Profile.My20v2);
    }

    [Theory]
    [InlineData("Auto-Detect")] [InlineData("autodetect")] [InlineData("AUTODETECT")] [InlineData(" Auto-Detect ")] [InlineData("auto detect")] [InlineData("")] [InlineData("   ")]
    [InlineData("something else")] [InlineData("autoinstall now")] [InlineData("re-format")] [InlineData("0")] [InlineData("true")] [InlineData("null")]
    public void AnyValueThatIsNotARealMode_MeansAutoDetect(string raw)
    {
        Assert.Equal(InstallModes.AutoDetect, InstallModes.Normalize(raw)); Assert.False(InstallModes.IsForced(raw));
        Assert.Equal(InstallModes.AutoDetect, new ProfileSettings { InstallMode = raw }.InstallMode);
        Assert.Equal(InstallModes.AutoDetect, InstallModes.Normalize(null));
    }

    [Theory]
    [InlineData("reformat", "reformat")] [InlineData("Reformat", "reformat")] [InlineData(" REFORMAT ", "reformat")]
    [InlineData("autoinstall", "autoinstall")] [InlineData("AutoInstall", "autoinstall")] [InlineData("downgrade", "downgrade")] [InlineData("Downgrade", "downgrade")]
    public void TheRealModes_AreKept_InAnyCase(string raw, string expected)
    {
        Assert.Equal(expected, InstallModes.Normalize(raw)); Assert.True(InstallModes.IsForced(raw));
        var s = LoadWith(V2Profile.Replace("\"Auto-Detect\"", $"\"{raw}\"").Replace("\"My20v2\": null", "\"My20v2\": false"));      // v2 only let you fix a mode with MY20 protection off
        Assert.Equal(expected, s.Profile.InstallMode);                                                              // a mode somebody really chose is NOT lost
    }

    [Fact]
    public void EveryLanguagesAutoDetectText_IsAutoDetect()
    {
        // v2 saved whatever the Settings page showed, so a profile can hold the translated "Auto-Detect" of any of the 30 languages
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null && !Directory.Exists(Path.Combine(d.FullName, "src", "Syn3Updater.Core", "Languages"))) d = d.Parent;
        var files = Directory.GetFiles(Path.Combine(d!.FullName, "src", "Syn3Updater.Core", "Languages"), "*.json");
        Assert.True(files.Length >= 30);
        foreach (var f in files)
        {
            var text = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(f))!["String.AutoDetect"];
            Assert.True(InstallModes.Normalize(text) == InstallModes.AutoDetect, $"{Path.GetFileName(f)}: '{text}' was not read as auto-detect");
            Assert.False(Ctx_ModeForced(text));
        }
    }
    private static bool Ctx_ModeForced(string mode) => InstallModes.IsForced(mode);

    [Fact]
    public void SavingWritesTheCleanValue_SoTheFileIsFixedForGood()
    {
        var paths = new AppPaths(_dir); var s = LoadWith(V2Profile); s.Save();
        var saved = JsonDocument.Parse(File.ReadAllText(paths.ProfileFile("default"))).RootElement;
        Assert.Equal("autodetect", saved.GetProperty("InstallMode").GetString());
    }

    [Theory]
    [InlineData("null", null)] [InlineData("true", true)] [InlineData("false", false)]
    [InlineData("\"true\"", true)] [InlineData("\"False\"", false)] [InlineData("1", true)] [InlineData("0", false)]
    [InlineData("\"maybe\"", null)] [InlineData("7", null)] [InlineData("[]", null)] [InlineData("{\"a\":1}", null)] [InlineData("\"\"", null)]
    public void My20_CarriesOver_AndABadValueFallsBackToAutoDetect_WithoutLosingTheProfile(string json, bool? expected)
    {
        var s = LoadWith(V2Profile.Replace("\"My20v2\": null", $"\"My20v2\": {json}"));
        Assert.Equal(expected, s.Profile.My20v2);
        Assert.Equal("EU", s.Profile.CurrentRegion); Assert.Equal(3426057, s.Profile.CurrentVersion);               // the rest of the profile survived
    }

    [Theory]
    [InlineData(null)] [InlineData(true)] [InlineData(false)]
    public void My20_RoundTripsThroughSave(bool? value)
    {
        var paths = new AppPaths(_dir); var s = LoadWith(V2Profile); s.Profile.My20v2 = value; s.Save();
        var again = new SettingsStore(paths); again.Load();
        Assert.Equal(value, again.Profile.My20v2);
        var raw = JsonDocument.Parse(File.ReadAllText(paths.ProfileFile("default"))).RootElement.GetProperty("My20v2");
        Assert.Equal(value switch { null => JsonValueKind.Null, true => JsonValueKind.True, false => JsonValueKind.False }, raw.ValueKind);   // the same JSON v2 wrote
    }

    [Fact]
    public void DownloadConnections_MoveToTheApplicationSettings_AndLeaveTheProfileFile()
    {
        var paths = new AppPaths(_dir);
        var s = LoadWith(V2Profile.Replace("\"DownloadConnections\": 8", "\"DownloadConnections\": 3"), mainJson: "{ \"Lang\": \"de\" }");   // v2 main settings had no such key
        Assert.Equal(3, s.Main.DownloadConnections); Assert.Null(s.Profile.LegacyDownloadConnections);
        s.Save();
        Assert.False(JsonDocument.Parse(File.ReadAllText(paths.ProfileFile("default"))).RootElement.TryGetProperty("DownloadConnections", out _));   // gone from the profile
        Assert.Equal(3, JsonDocument.Parse(File.ReadAllText(paths.MainConfigFile)).RootElement.GetProperty("DownloadConnections").GetInt32());         // kept in the app settings
        var again = new SettingsStore(paths); again.Load();
        Assert.Equal(3, again.Main.DownloadConnections); Assert.Null(again.Profile.LegacyDownloadConnections);                                         // and it only happens once
    }

    [Fact]
    public void AValueAlreadyInTheApplicationSettings_IsNotOverwrittenByTheProfilesOldOne()
    {
        var s = LoadWith(V2Profile.Replace("\"DownloadConnections\": 8", "\"DownloadConnections\": 3"), mainJson: "{ \"DownloadConnections\": 5 }");
        Assert.Equal(5, s.Main.DownloadConnections); Assert.Null(s.Profile.LegacyDownloadConnections);                // the profile's is just dropped
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(-4, 1)] [InlineData(1, 1)] [InlineData(6, 6)] [InlineData(8, 8)] [InlineData(99, 8)]
    public void AMigratedConnectionCount_IsKeptInTheAllowedRange(int legacy, int expected)
    {
        var s = LoadWith(V2Profile.Replace("\"DownloadConnections\": 8", $"\"DownloadConnections\": {legacy}"), mainJson: "{}");
        Assert.Equal(expected, s.Main.DownloadConnections);
    }

    [Fact]
    public void ANewProfile_NeverWritesDownloadConnections_AndSwitchingToAV2ProfileMigratesIt()
    {
        var paths = new AppPaths(_dir); var s = new SettingsStore(paths); s.Load(); s.Save();
        Assert.False(JsonDocument.Parse(File.ReadAllText(paths.ProfileFile("default"))).RootElement.TryGetProperty("DownloadConnections", out _));
        File.WriteAllText(paths.ProfileFile("car2"), V2Profile.Replace("\"Auto-Detect\"", "\"Reformat\"").Replace("\"My20v2\": null", "\"My20v2\": false").Replace("\"DownloadConnections\": 8", "\"DownloadConnections\": 2"));
        s.SwitchProfile("car2");
        Assert.Equal("reformat", s.Profile.InstallMode); Assert.Null(s.Profile.LegacyDownloadConnections);
        Assert.Equal(8, s.Main.DownloadConnections);                          // the app settings already had a value (written on the first save), so the old per-profile 2 does not override it
    }

    [Fact]
    public void ABrokenFieldNoLongerThrowsAwayTheWholeProfile()
    {
        var s = LoadWith(V2Profile.Replace("\"My20v2\": null", "\"My20v2\": \"yes\""));
        Assert.Equal("EU", s.Profile.CurrentRegion);                          // before: any wrongly typed field deleted the file and everything in it
    }
}
