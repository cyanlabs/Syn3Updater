using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class DialogHostTests
{
    private static DialogHostViewModel Fresh()
    {
        var host = DialogHostViewModel.Instance;
        while (host.Current != null) host.Cancel();      // drain anything left by another test
        host.IsAttached = true;
        return host;
    }

    private static DialogRequest Req(string msg, int def = 0, bool prompt = false) => new()
    {
        Title = "T", Message = msg, DefaultIndex = def, IsPrompt = prompt, Text = "init",
        Buttons = new[] { new DialogButton("No", 0, false), new DialogButton("Yes", 1, true) },
    };

    [AvaloniaFact]
    public async Task Choose_CompletesTheRequest_WithTheButtonIndex()
    {
        var host = Fresh(); var t = host.ShowAsync(Req("a"));
        Assert.True(host.HasDialog); Assert.Equal("a", host.Current!.Message); Assert.False(t.IsCompleted);
        host.ChooseCommand.Execute(1);
        Assert.Equal(1, await t); Assert.False(host.HasDialog);
    }

    [AvaloniaFact]
    public async Task Dialogs_AreQueued_NeverStacked()
    {
        var host = Fresh(); var first = host.ShowAsync(Req("first")); var second = host.ShowAsync(Req("second")); var third = host.ShowAsync(Req("third"));
        Assert.Equal("first", host.Current!.Message);
        host.ChooseCommand.Execute(1); Assert.Equal("second", host.Current!.Message);
        host.ChooseCommand.Execute(0); Assert.Equal("third", host.Current!.Message);
        host.ChooseCommand.Execute(1); Assert.Null(host.Current);
        Assert.Equal(new[] { 1, 0, 1 }, new[] { await first, await second, await third });
    }

    [AvaloniaFact]
    public async Task Escape_IsTheFirstButton_EnterIsTheDefault()
    {
        var host = Fresh();
        var t1 = host.ShowAsync(Req("x", def: 1)); host.AcceptDefault(); Assert.Equal(1, await t1);
        var t2 = host.ShowAsync(Req("y", def: 1)); host.Cancel(); Assert.Equal(0, await t2);
        host.Cancel(); host.AcceptDefault();                 // nothing open: harmless
        Assert.False(host.HasDialog);
    }

    [AvaloniaFact]
    public void Tone_FlagsDriveTheTitleColour()
    {
        Assert.True(new DialogRequest { Tone = DialogTone.Error }.IsError); Assert.True(new DialogRequest { Tone = DialogTone.Warning }.IsWarning);
        var normal = new DialogRequest(); Assert.False(normal.IsError || normal.IsWarning);
    }

    [AvaloniaFact]
    public async Task Backend_ShowsInsideTheWindow_WhenAttached()
    {
        var host = Fresh(); var backend = new AvaloniaDialogBackend();
        var t = backend.Show("Format this?", "WARNING", new[] { "No", "Yes" }, warning: true, error: false, defaultIndex: 0);
        Assert.True(host.HasDialog); Assert.Equal("WARNING", host.Current!.Title); Assert.True(host.Current.IsWarning);
        Assert.Equal(new[] { false, true }, host.Current.Buttons.Select(b => b.IsPrimary));          // the last button is the primary one
        host.ChooseCommand.Execute(1); Assert.Equal(1, await t);

        var single = backend.Show("Oops", "Error", new[] { "OK" }, false, true, 0);
        Assert.True(host.Current!.IsError); Assert.False(host.Current.Buttons[0].IsPrimary); host.AcceptDefault(); Assert.Equal(0, await single);
    }

    [AvaloniaFact]
    public async Task Prompt_ReturnsTheTypedText_OrNullWhenCancelled()
    {
        var host = Fresh(); var backend = new AvaloniaDialogBackend();
        var t = backend.Prompt("New profile", "Profile Name", "start");
        Assert.True(host.Current!.IsPrompt); Assert.Equal("start", host.Current.Text);
        host.Current.Text = "Wife's car"; host.AcceptDefault();                                        // Enter = OK
        Assert.Equal("Wife's car", await t);
        var cancelled = backend.Prompt("New profile", "Profile Name", ""); host.Cancel(); Assert.Null(await cancelled);
    }

    [AvaloniaFact]
    public async Task ShowFromAnotherThread_IsMarshalledOntoTheUiThread()
    {
        var host = Fresh(); Task<int>? t = null;
        await Task.Run(() => { t = host.ShowAsync(Req("bg")); });
        await AppEnv.Until(() => host.HasDialog); host.ChooseCommand.Execute(1); Assert.Equal(1, await t!);
    }
}

public class DisclaimerGateTests
{
    [AvaloniaFact]
    public async Task AlreadyAccepted_ShowsNothing()
    {
        using var env = new AppEnv(disclaimer: true);
        Assert.True(await DisclaimerGate.EnsureAcceptedAsync(() => throw new InvalidOperationException("must not exit")));
        Assert.Empty(env.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task FirstStart_ShowsThePopup_AndAcceptingIsRemembered()
    {
        using var env = new AppEnv(disclaimer: false); env.Dialogs.Say(m => m.Contains(Ctx.T("MessageBox.DisclaimerNotAccepted")), true);
        Assert.True(await DisclaimerGate.EnsureAcceptedAsync(() => throw new InvalidOperationException("must not exit")));
        var shown = Assert.Single(env.Dialogs.Messages);
        Assert.Contains("Please read and accept the disclaimer before continuing", shown);            // the explanation is part of the popup
        Assert.Contains(Ctx.T("About.Disclaimer"), shown); Assert.Contains(Ctx.T("About.Endorsed"), shown);
        Assert.True(Ctx.Main.DisclaimerAccepted);
        Assert.Contains("\"DisclaimerAccepted\": true", File.ReadAllText(Path.Combine(env.Dir, "data", "settings.json")));   // saved: it does not come back next time
        env.Dialogs.Messages.Clear();
        Assert.True(await DisclaimerGate.EnsureAcceptedAsync(() => throw new InvalidOperationException()));                  // and is not asked again
        Assert.Empty(env.Dialogs.Messages);
    }

    [AvaloniaFact]
    public async Task Declining_ExitsTheApp_AndStaysUnaccepted()
    {
        using var env = new AppEnv(disclaimer: false); bool exited = false;                           // default answer: first button (Exit)
        Assert.False(await DisclaimerGate.EnsureAcceptedAsync(() => { exited = true; return Task.CompletedTask; }));
        Assert.True(exited); Assert.False(Ctx.Main.DisclaimerAccepted);
    }
}
