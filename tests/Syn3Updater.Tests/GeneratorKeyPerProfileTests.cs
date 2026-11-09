using System.Text.Json;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

/// <summary>The Generator key belongs to the profile (one car or customer each). A key kept application-wide by v2 and earlier v3 moves into the active profile on first start.</summary>
public class GeneratorKeyPerProfileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("syn3key").FullName;
    public void Dispose() => Directory.Delete(_dir, true);
    private const string Key = "GEN-KEY-1234567890";

    private AppPaths Paths() => new(_dir);
    private static string ReadKey(string file, string name = "LicenseKey") => JsonDocument.Parse(File.ReadAllText(file)).RootElement.TryGetProperty(name, out var p) ? p.GetString() ?? "<null>" : "<absent>";

    private SettingsStore LoadWith(string mainJson, string? profileJson = null, string profile = "default")
    {
        var paths = Paths();
        File.WriteAllText(paths.MainConfigFile, mainJson);
        if (profileJson != null) File.WriteAllText(paths.ProfileFile(profile), profileJson);
        var s = new SettingsStore(paths); s.Load(); return s;
    }

    [Fact]
    public void A_global_key_moves_into_the_active_profile_on_first_start_and_is_gone_from_the_main_file()
    {
        var s = LoadWith($$"""{"Profile":"default","LicenseKey":"{{Key}}"}""");
        Assert.Equal(Key, s.Profile.LicenseKey); Assert.Null(s.Main.LegacyLicenseKey);
        Assert.Equal(Key, ReadKey(Paths().ProfileFile("default")));                         // written at once: it is in the profile file...
        Assert.Equal("<absent>", ReadKey(Paths().MainConfigFile));                          // ...and no longer in the main one
    }

    [Fact]
    public void The_move_happens_once_and_a_second_start_changes_nothing()
    {
        LoadWith($$"""{"Profile":"default","LicenseKey":"{{Key}}"}""");
        var again = new SettingsStore(Paths()); again.Load();
        Assert.Equal(Key, again.Profile.LicenseKey); Assert.Null(again.Main.LegacyLicenseKey);
        again.Profile.LicenseKey = "changed"; again.Save();
        var third = new SettingsStore(Paths()); third.Load();
        Assert.Equal("changed", third.Profile.LicenseKey);                                  // not overwritten by anything left behind
    }

    [Fact]
    public void The_key_goes_to_the_active_profile_only_not_to_the_others()
    {
        var paths = Paths();
        File.WriteAllText(paths.ProfileFile("other"), """{"CurrentRegion":"NA"}""");
        var s = LoadWith($$"""{"Profile":"work","LicenseKey":"{{Key}}"}""");
        Assert.Equal("work", s.Main.Profile); Assert.Equal(Key, s.Profile.LicenseKey);
        s.SwitchProfile("other");
        Assert.Equal("", s.Profile.LicenseKey);                                              // another profile has no key until one is set for it
        s.SwitchProfile("work");
        Assert.Equal(Key, s.Profile.LicenseKey);
    }

    [Fact]
    public void A_key_the_profile_already_has_wins_and_the_old_global_one_is_dropped()
    {
        var s = LoadWith("""{"Profile":"default","LicenseKey":"OLD-GLOBAL-KEY-111"}""", """{"LicenseKey":"PROFILE-OWN-KEY-222"}""");
        Assert.Equal("PROFILE-OWN-KEY-222", s.Profile.LicenseKey);
        Assert.Equal("<absent>", ReadKey(Paths().MainConfigFile));
    }

    [Theory]
    [InlineData("\"LicenseKey\":\"\"")]          // what v3 wrote for a key never entered
    [InlineData("\"LicenseKey\":\"   \"")]
    [InlineData("\"LicenseKey\":null")]
    public void An_empty_global_key_is_just_cleared_and_the_profile_stays_without_one(string field)
    {
        var s = LoadWith("{\"Profile\":\"default\"," + field + "}");
        Assert.Equal("", s.Profile.LicenseKey); Assert.Null(s.Main.LegacyLicenseKey);
        s.Save();                                                                              // a null needs no rewrite at start; the next save leaves the field out for good
        Assert.Equal("<absent>", ReadKey(Paths().MainConfigFile));
    }

    [Fact]
    public void A_new_install_with_no_old_key_has_nothing_to_move_and_writes_no_stray_field()
    {
        var s = new SettingsStore(Paths()); s.Load(); s.Save();
        Assert.Equal("", s.Profile.LicenseKey); Assert.Equal("<absent>", ReadKey(Paths().MainConfigFile));
        Assert.Equal("", ReadKey(Paths().ProfileFile("default")));                           // the profile's own field is always written, empty when unset
    }

    [Fact]
    public void The_key_is_trimmed_when_it_moves()
    {
        var s = LoadWith("{\"Profile\":\"default\",\"LicenseKey\":\"  " + Key + "  \"}");
        Assert.Equal(Key, s.Profile.LicenseKey);
    }

    [Fact]
    public void Saving_writes_the_profile_before_the_main_file_so_a_failed_write_cannot_lose_a_moved_key()
    {
        var src = File.ReadAllText(FindRepoFile("src/Syn3Updater.Core/Services/SettingsStore.cs"));
        var save = src[src.IndexOf("public void Save()", StringComparison.Ordinal)..];
        Assert.True(save.IndexOf("ProfileFile(Main.Profile)", StringComparison.Ordinal) < save.IndexOf("MainConfigFile", StringComparison.Ordinal), "the profile must be written first");
    }

    private static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }
}

public class GeneratorKeyInTheAppTests
{
    private const string KeyA = "KEY-FOR-CAR-A-0001", KeyB = "KEY-FOR-CAR-B-0002";

    private static async Task<string> MapQuery(AppEnv env)
    {
        env.Api.Queries.Clear();
        await Ctx.Api.GetMapReleases("NA", "3.4");
        return env.Api.Queries.Single(q => q.Contains("map_releases(sort"));
    }

    [AvaloniaFact]
    public async Task The_backend_is_asked_with_the_active_profiles_key_and_follows_a_profile_switch()
    {
        using var env = new AppEnv();
        Ctx.Profile.LicenseKey = KeyA; Ctx.Save();
        Assert.Contains(KeyA, await MapQuery(env));
        Ctx.Store.SwitchProfile("customer-b"); Ctx.Profile.LicenseKey = KeyB;
        var b = await MapQuery(env); Assert.Contains(KeyB, b); Assert.DoesNotContain(KeyA, b);
        Ctx.Store.SwitchProfile("default");
        var a = await MapQuery(env); Assert.Contains(KeyA, a); Assert.DoesNotContain(KeyB, a);
    }

    [AvaloniaFact]
    public async Task The_releases_query_uses_the_profiles_key_too_and_a_profile_without_one_asks_for_the_public_ones_only()
    {
        using var env = new AppEnv();
        Ctx.Profile.LicenseKey = KeyA;
        env.Api.Queries.Clear(); await Ctx.Api.GetReleases("NA");
        Assert.Contains(KeyA, env.Api.Queries.Single(q => q.Contains("releases(sort")));
        Ctx.Store.SwitchProfile("no-key");
        env.Api.Queries.Clear(); await Ctx.Api.GetReleases("NA");
        var q = env.Api.Queries.Single(x => x.Contains("releases(sort"));
        Assert.DoesNotContain(KeyA, q); Assert.Contains("\"public\"", q);
    }

    [AvaloniaFact]
    public async Task The_Settings_page_shows_the_active_profiles_key_and_Apply_saves_it_into_that_profile_not_the_main_settings()
    {
        using var env = new AppEnv(); var s = env.Main.Settings;
        Ctx.Profile.LicenseKey = KeyA; s.OnShown();
        Assert.Equal(KeyA, s.LicenseKey);
        s.LicenseKey = "  " + KeyB + "  ";
        await s.ApplyClickCommand.ExecuteAsync(null);                                         // waited for, so nothing is left running when the test ends
        Assert.Equal(KeyB, Ctx.Profile.LicenseKey); Assert.Null(Ctx.Main.LegacyLicenseKey);
        var main = File.ReadAllText(Path.Combine(env.Dir, "data", "settings.json")); var profile = File.ReadAllText(Path.Combine(env.Dir, "data", "Profiles", "default.json"));
        Assert.DoesNotContain(KeyB, main); Assert.Contains(KeyB, profile);
    }

    [AvaloniaFact]
    public void Switching_profile_changes_what_the_Settings_page_shows()
    {
        using var env = new AppEnv(); var s = env.Main.Settings;
        Ctx.Profile.LicenseKey = KeyA; s.OnShown(); Assert.Equal(KeyA, s.LicenseKey);
        Ctx.Store.SwitchProfile("other"); s.OnShown();
        Assert.Equal("", s.LicenseKey);
        Ctx.Profile.LicenseKey = KeyB; Ctx.Store.SwitchProfile("default"); s.OnShown();
        Assert.Equal(KeyA, s.LicenseKey);
    }

    [AvaloniaFact]
    public async Task Changing_the_key_reloads_the_lists_even_when_leaving_Settings_because_it_decides_what_is_offered()
    {
        using var env = new AppEnv(); var home = env.Main.Home; home.ReloadSummary();
        await AppEnv.Until(() => home.Releases.Count > 0);
        home.SelectedRelease = home.Releases[0]; await AppEnv.Until(() => home.MapVersions.Count > 0);
        Assert.NotNull(home.SelectedRelease);
        var s = env.Main.Settings; s.OnShown();
        s.LicenseKey = KeyB;                                                                  // only the key changed, then the page is left (the quiet autosave)
        s.OnLeaving();
        await AppEnv.Until(() => home.SelectedRelease == null && Ctx.Profile.LicenseKey == KeyB);          // the choices were cleared and loaded afresh with the new key
        await Task.Delay(100);                                                                 // let the reload that follows finish before the test ends
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, relative))) return Path.Combine(dir.FullName, relative);
        throw new FileNotFoundException(relative);
    }

    [Fact]
    public void On_the_Settings_page_the_key_has_its_own_card_in_the_profile_section_away_from_the_car_fields_and_the_advanced_warning()
    {
        var view = File.ReadAllText(RepoFile("src/Syn3Updater.App/Views/SettingsView.axaml"));
        var car = view.IndexOf("Settings.CarConfiguration", StringComparison.Ordinal);
        var key = view.IndexOf("Text=\"{Binding LicenseKey}\"", StringComparison.Ordinal);
        var advanced = view.IndexOf("Settings.AdvancedOptions", StringComparison.Ordinal);
        var app = view.IndexOf("application settings", StringComparison.OrdinalIgnoreCase);
        Assert.True(car > 0 && car < key && key < advanced && advanced < app, "order: car configuration, then the key's card, then advanced options, then the application settings");
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(view, "Binding LicenseKey"));                              // one field, not two
        var between = view[car..key];
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(between, "<Border Classes=\"card"));                       // a card opens after the car card's title: the key's own (none would mean it is still inside the car card)
        Assert.Contains("Settings.MapKey", between); Assert.Contains("Settings.GeneratorKeyHint", between);                 // titled, with its one-line hint
        Assert.DoesNotContain("AdvancedWarning", between);                                                                     // and nowhere near the warning about bricking the unit
    }

    [Fact]
    public void The_hint_string_exists_and_says_what_the_key_is_for()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(RepoFile("src/Syn3Updater.Core/Languages/en-US.json")));
        var hint = doc.RootElement.GetProperty("Settings.GeneratorKeyHint").GetString()!;
        Assert.StartsWith("Optional.", hint); Assert.Contains("private releases and maps", hint); Assert.Contains("Each profile has its own key", hint);
    }
}
