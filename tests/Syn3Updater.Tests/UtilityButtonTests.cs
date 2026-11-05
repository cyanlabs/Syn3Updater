using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cyanlabs.Syn3Updater.App.Services;
using Cyanlabs.Syn3Updater.App.Views;
using Cyanlabs.Syn3Updater.Core.Drives;
using Xunit;

namespace Cyanlabs.Syn3Updater.Tests;

public class UtilityButtonTests
{
    private static readonly DriveInfoModel Stick = new() { DeviceId = "/dev/sdx", MountPath = "/run/media/scott/CYANLABS", Name = "Stick" };

    [AvaloniaFact]
    public void CanPrepare_NeedsADrive_AndNoJobBeingStarted()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; var raised = new List<string?>();
        u.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.False(u.CanPrepare);
        u.SelectedDrive = Stick; Assert.True(u.CanPrepare); Assert.Contains(nameof(u.CanPrepare), raised);
        u.ButtonsEnabled = false; Assert.False(u.CanPrepare);                      // while another job is starting
        u.ButtonsEnabled = true; Assert.True(u.CanPrepare);
        u.SelectedDrive = null; Assert.False(u.CanPrepare);
        u.SelectedDrive = new DriveInfoModel { IsFolder = true, MountPath = "/x", Name = "f" }; Assert.True(u.CanPrepare);      // a folder is a valid destination too
    }

    [AvaloniaFact]
    public void TheButtons_AreGreyUntilTheirCondition_IsMet()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        var view = new UtilityView { DataContext = u }; var window = new Window { Content = view }; window.Show();
        var prep = view.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("prep")).ToList();
        var upload = prep.Single(b => b.Command == u.UploadLogCommand);
        var prepare = prep.Where(b => b != upload).ToList();

        Assert.Equal(6, prepare.Count);                                            // interrogator, RW cleaner, gracenotes, voice, downgrade, 2.2 upgrade
        Assert.Equal(new[] { "teal", "lime", "pink", "purple", "red", "green" }, prepare.Select(b => new[] { "teal", "lime", "pink", "purple", "red", "green" }.Single(c => b.Classes.Contains(c))));
        Assert.All(prepare, b => Assert.False(b.IsEnabled)); Assert.False(upload.IsEnabled);        // nothing selected: all grey

        // ...and a grey button explains itself, twice: its label and a tooltip that also shows while it is disabled
        Assert.All(prepare, b => { Assert.Equal("Select a USB drive", b.Content); Assert.Equal("Select a USB drive above to enable this button.", ToolTip.GetTip(b)); Assert.True(ToolTip.GetShowOnDisabled(b)); });
        Assert.Equal("Open an interrogator log (XML) first to enable this button.", ToolTip.GetTip(upload)); Assert.True(ToolTip.GetShowOnDisabled(upload));

        u.SelectedDrive = Stick;
        Assert.All(prepare, b => { Assert.True(b.IsEnabled); Assert.Equal("Prepare USB", b.Content); Assert.Null(ToolTip.GetTip(b)); });   // a drive: coloured, labelled normally, no tooltip
        Assert.False(upload.IsEnabled);                                            // ...but Upload Log waits for an interrogator file

        u.HasLog = true; Assert.True(upload.IsEnabled); Assert.Null(ToolTip.GetTip(upload));
        u.SelectedDrive = null; Assert.All(prepare, b => Assert.False(b.IsEnabled)); Assert.True(upload.IsEnabled);     // the two rules are independent
        window.Close();
    }

    [AvaloniaFact]
    public async Task OpeningAnInterrogatorFile_EnablesUploadLog()
    {
        using var env = new AppEnv(); var u = env.Main.Utility; Assert.False(u.HasLog);
        var xml = Path.Combine(env.Dir, "Sync_1_VIN.xml");
        File.WriteAllText(xml, """<p:OTAModuleSnapShot xmlns:p="u" xmlns:d="d"><p:VIN>1FA</p:VIN><d:DID didType="Embedded Consumer Operating System Part Number" didValue="D133"><d:Response>GB5T-14G381-AB</d:Response></d:DID><d:DID didType="ECU Delivery Assembly Number" didValue="F111"><d:Response>LB5T-14G371-AF</d:Response></d:DID><d:AdditionalAttributes logGeneratedDateTime="2024-01-02T03:04:05+00:00"><d:PartitionHealth type="/fs/images/" total="28.5G" available="12.0G"/></d:AdditionalAttributes></p:OTAModuleSnapShot>""");
        env.Dialogs.FileToPick = xml; await u.ParseLogCommand.ExecuteAsync(null);
        Assert.True(u.HasLog);
        var bad = Path.Combine(env.Dir, "bad.xml"); File.WriteAllText(bad, "<nope/>"); env.Dialogs.FileToPick = bad;
        await u.ParseLogCommand.ExecuteAsync(null); Assert.True(u.HasLog);         // a later bad file does not take away the log that was already opened
    }

    private static IEnumerable<string> TextsIn(Avalonia.Visual v) => v.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "");

    [AvaloniaFact]
    public void InterrogatorSteps_AreThreeSeparateBoxes()
    {
        using var env = new AppEnv(); var u = env.Main.Utility;
        var view = new UtilityView { DataContext = u }; var window = new Window { Content = view }; window.Show();
        var step1 = view.FindControl<Border>("Step1")!; var step2 = view.FindControl<Border>("Step2")!; var step3 = view.FindControl<Border>("Step3")!;
        Assert.All(new[] { step1, step2, step3 }, b => Assert.NotNull(b));

        Assert.Contains("1. Prepare drive", TextsIn(step1));
        Assert.DoesNotContain(TextsIn(step1), t => t.Contains("Insert Drive in car"));                    // the car step is no longer squeezed into box 1
        Assert.Equal(u.CreateLogUsbCommand, step1.GetVisualDescendants().OfType<Button>().Single().Command);   // box 1: just the Prepare USB button

        var step2Texts = TextsIn(step2).ToList();
        Assert.Equal(new[] { "2. In the car", "Insert Drive in car", "Await \"Install Complete\" on screen", "Reinsert Drive into computer" }, step2Texts);   // three separate lines, in order
        Assert.Empty(step2.GetVisualDescendants().OfType<Button>());                                      // box 2: nothing to press

        Assert.Contains("3. Open the Interrogator Log", TextsIn(step3));
        var step3Commands = step3.GetVisualDescendants().OfType<Button>().Select(b => b.Command).ToList();
        Assert.Equal(new[] { u.ParseLogCommand, u.UploadLogCommand }, step3Commands);                     // box 3: open the log, then upload it
        Assert.Equal(1, view.GetVisualDescendants().OfType<Button>().Count(b => b.Command == u.UploadLogCommand));   // and no second Upload Log button elsewhere
        window.Close();
    }
}
