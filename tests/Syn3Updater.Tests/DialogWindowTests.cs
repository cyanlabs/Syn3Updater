using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Cyanlabs.Syn3Updater.App.Services;
using Xunit;

namespace Syn3Updater.Tests;

public class DialogWindowTests
{
    /// <summary>The Home page can report a drive-listing error before the main window is on screen; Avalonia throws "Cannot show window with non-visible owner" for a dialog over it.</summary>
    [AvaloniaFact]
    public async Task A_dialog_over_an_owner_that_is_not_visible_yet_opens_on_its_own_and_ends_when_closed()
    {
        var owner = new Window();                       // created, never shown: IsVisible is false
        Assert.False(owner.IsVisible);
        var dialog = new Window();

        var shown = DialogsImpl.ShowAndWaitAsync(dialog, owner);
        Assert.True(dialog.IsVisible);
        Assert.False(shown.IsCompleted);
        Assert.True(dialog.ShowInTaskbar);              // stand-alone, so it must be findable

        dialog.Close();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public async Task A_dialog_with_no_owner_at_all_ends_when_closed_instead_of_waiting_forever()
    {
        var dialog = new Window();
        var shown = DialogsImpl.ShowAndWaitAsync(dialog, null);
        dialog.Close();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public async Task A_dialog_over_a_visible_owner_stays_modal()
    {
        var owner = new Window(); owner.Show();
        var dialog = new Window();
        var shown = DialogsImpl.ShowAndWaitAsync(dialog, owner);
        Assert.True(dialog.IsVisible);
        Assert.Same(owner, dialog.Owner);
        dialog.Close();
        await shown.WaitAsync(TimeSpan.FromSeconds(5));
        owner.Close();
    }
}
