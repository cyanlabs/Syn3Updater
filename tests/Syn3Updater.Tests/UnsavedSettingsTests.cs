using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.Core.Services;
using Xunit;
namespace Cyanlabs.Syn3Updater.Tests;

public class UnsavedSettingsTests
{
    [AvaloniaFact]
    public async Task LeavingWithoutChanges_DoesNotAsk()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings; await Task.Yield();
        Assert.True(await m.GoToAsync(m.Home));
        Assert.Same(m.Home, m.Current); Assert.Empty(env.Dialogs.Shown);
    }

    [AvaloniaFact]
    public async Task Save_AppliesTheChanges_AndLeaves()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings;
        m.Settings.Version = "3.3.19052"; Assert.True(m.Settings.IsDirty);
        env.Dialogs.Choices.Enqueue(2);
        Assert.True(await m.GoToAsync(m.Home));
        Assert.Same(m.Home, m.Current); Assert.Equal(3319052, Ctx.Profile.CurrentVersion);
        Assert.Equal(new[] { "Keep editing", "Discard changes", "Save changes" }, env.Dialogs.Shown.Single().Buttons);
    }

    [AvaloniaFact]
    public async Task TheUpdateChannel_DefaultsToTheRunningBuild_AndIsSavedWhenChanged()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings;
        Assert.Equal(Channels.Of(Ctx.Version).ToString(), m.Settings.Channel!.Value);                 // nothing saved yet: follows the build
        Assert.Equal(new[] { "Stable", "Beta", "Nightly" }, m.Settings.ChannelOptions.Select(o => o.Value));
        Assert.False(m.Settings.IsDirty);
        m.Settings.Channel = m.Settings.ChannelOptions.Single(o => o.Value == "Nightly"); Assert.True(m.Settings.IsDirty);
        env.Dialogs.Choices.Enqueue(2);
        Assert.True(await m.GoToAsync(m.Home));
        Assert.Equal("Nightly", Ctx.Main.UpdateChannel); Assert.Equal(UpdateChannel.Nightly, Ctx.UpdateChannel);
    }

    private static string PrefsFile(string json) { var d = Path.Combine(Path.GetTempPath(), "s3u-prefs-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(d); var f = Path.Combine(d, "launcherPrefs.json"); File.WriteAllText(f, json); return f; }

    [AvaloniaFact]
    public async Task ChangingTheChannel_TellsTheLauncher_AndLeavesItsInstallRecordAlone()
    {
        using var env = new AppEnv(); var m = env.Main;
        var f = PrefsFile("{\"ReleaseBranch\":0,\"ReleaseInstalled\":\"2.14.2.1\",\"ReleaseTypeInstalled\":0}"); Ctx.LauncherPrefsPath = f;
        m.Current = m.Settings;
        Assert.Equal("Stable", m.Settings.Channel!.Value);                                           // nothing saved in the app: the Launcher's channel shows
        m.Settings.Channel = m.Settings.ChannelOptions.Single(o => o.Value == "Beta");
        env.Dialogs.Choices.Enqueue(2); Assert.True(await m.GoToAsync(m.Home));
        var o2 = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(f))!.AsObject();
        Assert.Equal(1, (int)o2["ReleaseBranch"]!); Assert.Equal("2.14.2.1", (string)o2["ReleaseInstalled"]!); Assert.Equal(0, (int)o2["ReleaseTypeInstalled"]!);
        Assert.Equal(UpdateChannel.Beta, Ctx.UpdateChannel);
    }

    [AvaloniaFact]
    public async Task Nightly_IsWrittenAsBeta_ButStaysNightlyInTheApp()
    {
        using var env = new AppEnv(); var m = env.Main;
        var f = PrefsFile("{\"ReleaseBranch\":0,\"ReleaseInstalled\":\"2.14.2.1\",\"ReleaseTypeInstalled\":0}"); Ctx.LauncherPrefsPath = f;
        m.Current = m.Settings; m.Settings.Channel = m.Settings.ChannelOptions.Single(o => o.Value == "Nightly");
        env.Dialogs.Choices.Enqueue(2); Assert.True(await m.GoToAsync(m.Home));
        Assert.Equal(1, LauncherPrefs.ReadBranch(f)); Assert.Equal(UpdateChannel.Nightly, Ctx.UpdateChannel);
    }

    [AvaloniaFact]
    public async Task WithoutALauncher_NothingIsWritten()
    {
        using var env = new AppEnv(); var m = env.Main;
        var missing = Path.Combine(Path.GetTempPath(), "s3u-none-" + Guid.NewGuid().ToString("N"), "launcherPrefs.json"); Ctx.LauncherPrefsPath = missing;   // no file, no Launcher.exe beside the test app
        m.Current = m.Settings; m.Settings.Channel = m.Settings.ChannelOptions.Single(o => o.Value == "Beta");
        env.Dialogs.Choices.Enqueue(2); Assert.True(await m.GoToAsync(m.Home));
        Assert.False(File.Exists(missing)); Assert.Equal(UpdateChannel.Beta, Ctx.UpdateChannel);
        Ctx.LauncherPrefsPath = null;                                                                  // what every non-Windows system has
        m.Current = m.Settings; m.Settings.Channel = m.Settings.ChannelOptions.Single(o => o.Value == "Stable");
        env.Dialogs.Choices.Enqueue(2); Assert.True(await m.GoToAsync(m.Home)); Assert.Equal(UpdateChannel.Stable, Ctx.UpdateChannel);
    }

    [AvaloniaFact]
    public void AChannelChosenInTheLauncherFile_BeatsADifferentOneSavedInTheApp()
    {
        using var env = new AppEnv(); Ctx.Main.UpdateChannel = "Stable";
        Ctx.LauncherPrefsPath = PrefsFile("{\"ReleaseBranch\":1}");
        Assert.Equal(UpdateChannel.Beta, Ctx.UpdateChannel);
    }

    [AvaloniaFact]
    public void TheSwitchBanner_NamesTheChannel_ButAPlainUpdateDoesNot()
    {
        using var env = new AppEnv(); var m = env.Main; Ctx.LauncherPrefsPath = null;
        Ctx.Main.UpdateChannel = "Beta";
        m.Update = new UpdateInfo("3.0.0-test10", "https://example/x", true, Switch: true);
        Assert.Equal($"Syn3 Updater 3.0.0-test10 is available on the Beta update channel (you have {Ctx.Version}).", m.UpdateText);
        Ctx.Main.UpdateChannel = "Nightly"; m.Update = m.Update with { };                                  // a new value so the text is read again
        Assert.Contains("on the Nightly update channel", m.UpdateText);
        Ctx.Main.UpdateChannel = "Stable";
        Assert.Contains("on the Stable update channel", m.UpdateText);
        m.Update = new UpdateInfo("3.0.0-test10", "https://example/x", true);
        Assert.Equal($"Syn3 Updater 3.0.0-test10 is available (you have {Ctx.Version}).", m.UpdateText);   // same channel as the running build: no channel named
    }

    [AvaloniaFact]
    public async Task Discard_LeavesTheSavedSettingsAlone_AndRevertsThePage()
    {
        using var env = new AppEnv(); var m = env.Main; var before = Ctx.Profile.CurrentVersion; m.Current = m.Settings;
        m.Settings.Version = "3.3.19052"; m.Settings.Nav = !m.Settings.Nav;
        env.Dialogs.Choices.Enqueue(1);
        Assert.True(await m.GoToAsync(m.Home));
        Assert.Same(m.Home, m.Current); Assert.Equal(before, Ctx.Profile.CurrentVersion);
        m.Current = m.Settings; Assert.False(m.Settings.IsDirty);
        Assert.Equal(Ctx.Profile.CurrentVersion is > 0 ? Ctx.Profile.VersionString : "", m.Settings.Version);   // the edit is gone from the page too
        Assert.Equal(Ctx.Profile.CurrentNav, m.Settings.Nav);
    }

    [AvaloniaFact]
    public async Task KeepEditing_StaysOnTheSettingsPage_WithTheEditIntact()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings;
        m.Settings.Version = "3.3.19052";
        env.Dialogs.Choices.Enqueue(0);
        Assert.False(await m.GoToAsync(m.Home));
        Assert.Same(m.Settings, m.Current); Assert.Equal("3.3.19052", m.Settings.Version); Assert.True(m.Settings.IsDirty);
    }

    [AvaloniaFact]
    public async Task Save_WithAnInvalidVersion_StaysAndShowsWhy()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings;
        m.Settings.Version = "3.4.2";                       // half typed
        env.Dialogs.Choices.Enqueue(2);
        Assert.False(await m.GoToAsync(m.Home));
        Assert.Same(m.Settings, m.Current); Assert.Contains("Version must look like", m.Settings.Message);
    }

    [AvaloniaFact]
    public async Task ApplyThenLeave_DoesNotAskAgain()
    {
        using var env = new AppEnv(); var m = env.Main; m.Current = m.Settings;
        m.Settings.Version = "3.3.19052"; await m.Settings.ApplyClickCommand.ExecuteAsync(null);
        Assert.Same(m.Home, m.Current); Assert.Empty(env.Dialogs.Shown);
    }
}
