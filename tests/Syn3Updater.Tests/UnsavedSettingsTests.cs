using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
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
