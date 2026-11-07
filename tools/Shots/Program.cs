// Renders app pages to PNG with Avalonia's headless backend (no window is ever shown).
// usage: dotnet run --project tools/Shots -- <outDir> [width] [height] [Page ...] [--light]
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Styling;
using Cyanlabs.Syn3Updater.App;
using Cyanlabs.Syn3Updater.App.ViewModels;
using Cyanlabs.Syn3Updater.App.Views;
using Cyanlabs.Syn3Updater.Core.Drives;
using Cyanlabs.Syn3Updater.Core.Models;
using Cyanlabs.Syn3Updater.App.Services;

var outDir = args.Length > 0 ? args[0] : "shots";
int w = args.Length > 1 ? int.Parse(args[1]) : 1100, h = args.Length > 2 ? int.Parse(args[2]) : 800;
var pages = args.Skip(3).Where(a => !a.StartsWith("--")).ToList();
bool light = args.Contains("--light");
bool demo = args.Contains("--demo");
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

var cts = new CancellationTokenSource();
var work = Dispatcher.UIThread.InvokeAsync(async () =>
{
  try {
    Application.Current!.RequestedThemeVariant = light ? ThemeVariant.Light : ThemeVariant.Dark;      // a headless run has no OS theme, so say which one the screenshots use
    var vm = new MainWindowViewModel();
    _ = vm.LoadAdAsync(); vm.Home.OnShown();
    var win = new MainWindow { DataContext = vm, Width = w, Height = h };
    win.Show();
    if (args.FirstOrDefault(a => a.StartsWith("--update=")) is { } upd)      // --update=install|progress|howto|notify: the update banner in each state
    {
        var state = upd["--update=".Length..];
        var inst = new Cyanlabs.Syn3Updater.Core.Services.Updates.InstallInfo(Cyanlabs.Syn3Updater.Core.Services.Updates.InstallKind.LinuxTar, "/home/me/.local/share/syn3updater", Cyanlabs.Syn3Updater.Core.Services.Updates.UpdateRoute.InPlace, "demo");
        vm.Update = new Cyanlabs.Syn3Updater.Core.Services.UpdateInfo("3.1.0", "https://github.com/CyanLabs/Syn3Updater/releases/tag/v3.1.0", false);
        var kind = state switch { "howto" => Cyanlabs.Syn3Updater.Core.Services.Updates.PlanKind.Manual, "notify" => Cyanlabs.Syn3Updater.Core.Services.Updates.PlanKind.NotifyOnly, _ => Cyanlabs.Syn3Updater.Core.Services.Updates.PlanKind.Install };
        vm.Plan = new Cyanlabs.Syn3Updater.Core.Services.Updates.UpdatePlan(kind, null, null, inst);
        if (state == "progress") { vm.UpdateStatus = Ctx.T("App.UpdateDownloading", "3.1.0", 45); vm.UpdateProgress = 0.45; vm.InstallingUpdate = true; }
    }
    if (args.Contains("--dialog"))      // first-start disclaimer popup
    {
        Cyanlabs.Syn3Updater.App.Services.Ctx.Main.DisclaimerAccepted = false;
        _ = Cyanlabs.Syn3Updater.App.Services.DisclaimerGate.EnsureAcceptedAsync(() => Task.CompletedTask);
        if (args.Contains("--second")) { await Task.Delay(500); Cyanlabs.Syn3Updater.App.Services.DialogHostViewModel.Instance.Cancel(); }   // dismiss the welcome to show the disclaimer
    }
        void ApplyDemo()
        {
            // sample state so the "active" layouts can be reviewed without touching real drives or the network
            var drive = new DriveInfoModel { DeviceId = "/dev/sdb", MountPath = "/run/media/scott/CYANLABS", Name = "SanDisk Ultra", VolumeName = "CYANLABS", SizeBytes = 31_914_983_424, FreeBytes = 31_000_000_000, FileSystem = "exFAT", PartitionType = "MBR" };
            vm.Settings.Advanced = true;
        vm.Home.Drives.Add(drive); vm.Home.SelectedDrive = drive; vm.Home.NoDrives = false;
            vm.Home.Releases.Add(new ReleaseItem { Display = "Sync 3.4.26057" });
            vm.Home.Packages.Clear();
        foreach (var (t, n, v, sz, sel) in new[] {
            ("APPS", "PU5T-14G381-AB", "3.4.26057", 1_400_000_000L, true), ("VOICE", "PU5T-14G391-AC", "", 310_000_000L, true),
            ("GRACENOTES", "PU5T-14G423-AB", "", 90_000_000L, false), ("ENH_DAB", "PU5T-14G658-AF", "", 48_000_000L, true),
            ("MAP_LICENSE", "4U5T-14G424-AA", "", 12_000L, true), ("MAP", "4U5T-14G421-CAC", "", 2_300_000_000L, true) })
        { var row = new PackageRow(new Ivsu { Type = t, Name = n, Version = v, FileSize = sz }) { Selected = sel }; vm.Home.Packages.Add(row); }
        vm.Home.InstallMode = Environment.GetEnvironmentVariable("DEMO_MODE") ?? "downgrade"; vm.Home.StartEnabled = true; vm.Home.DownloadOnlyEnabled = true;
        vm.Home.Notes = "**Sync 3.4.26057** brings:\n- Improved Bluetooth stability\n- New `AppLink` fixes\n\nFull details: https://community.cyanlabs.net/t/sync-3-4/123 and [the forum](https://community.cyanlabs.net).";
        Directory.CreateDirectory(Ctx.Main.LogPath!);
        File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, "log-2026-11-05-18-20-11.txt"), "CYANLABS - SYN3 UPDATER - V3.0.0\n[2026-11-05 18:20:11] INFO: Downloading: PU5T-14G381-AB\n[2026-11-05 18:21:40] INFO: Downloaded: PU5T-14G381-AB\n[2026-11-05 18:21:41] WARN: slow server\n[2026-11-05 18:22:00] ERROR: Unable to validate x after 3 attempts\n[2026-11-05 18:23:00] INFO: ALL FILES DOWNLOADED AND COPIED SUCCESSFULLY!");
        File.WriteAllText(Path.Combine(Ctx.Main.LogPath!, "Sync_12345678_VIN.xml"), "<xml/>");
        Ctx.Log.Info("Starting process (Sync 3.4.26057)"); Ctx.Log.Error("demo error line");
        vm.Logs.OnShown(); vm.Logs.Selected = vm.Logs.Files.FirstOrDefault(f => f.Kind == Cyanlabs.Syn3Updater.Core.Services.LogKind.UpdateRun);
        vm.News.Changelogs.Clear();
        vm.News.Changelogs.Add(new Changelog { Version = "2.14.0.0", Date = "29-08-2025", ReleaseNotes = "Added: displaying of Map Release notes\nNotice: **All old versions** will stop working shortly\n\n**Full Changelog**: https://github.com/cyanlabs/Syn3Updater/compare/2.13.3.0...2.14.0.0" });
        vm.Downloads.Headline = "Sync 3.4.26057 - NA - Keep Existing Maps"; vm.Downloads.IsRunning = true;
            vm.Downloads.Status = "Downloading: PU5T-14G381-AB_1722.TAR.GZ"; vm.Downloads.CurrentFile = "PU5T-14G381-AB_1722.TAR.GZ";
            vm.Downloads.SpeedText = "12.3MB/s"; vm.Downloads.EtaText = "2 min 10 s left"; vm.Downloads.ConnectionsText = "8 connections"; vm.Downloads.CanEject = true;
        vm.Downloads.FileProgress = 63; vm.Downloads.TotalProgress = 31; vm.Downloads.FileCounter = "File 2 of 4";
            vm.Downloads.Completed.Add(new QueueItem("PU5T-14G386-BB.TAR.GZ", "3 KB", 0) { IsDone = true });
            vm.Downloads.PhaseLabel = "Downloading files"; vm.Downloads.Queue.Add(new QueueItem("PU5T-14G381-AB_1722.TAR.GZ", "1.3 GB", 1) { IsActive = true });
            vm.Downloads.Queue.Add(new QueueItem("PU5T-14G391-AC.TAR.GZ", "296 MB", 2));
            vm.Downloads.Log = "[2026-11-05 18:30:01] INFO: Validated: PU5T-14G386-BB (Skipping Download)\n[2026-11-05 18:30:02] INFO: Downloading: PU5T-14G381-AB_1722.TAR.GZ\n";
        }
    foreach (var p in pages.Count > 0 ? pages : vm.Pages.Select(x => x.GetType().Name.Replace("ViewModel", "")).ToList())
    {
        var page = vm.Pages.First(x => x.GetType().Name.StartsWith(p.TrimEnd('s'), StringComparison.OrdinalIgnoreCase));
        vm.Current = page;
        await Task.Delay(3500);                       // let live API calls finish
        if (demo) { ApplyDemo(); await Task.Delay(400); }
        Dispatcher.UIThread.RunJobs();
        if (Environment.GetEnvironmentVariable("SYN3_MEASURE") == "1")
            foreach (var v in win.GetVisualDescendants().Where(v => v is ScrollViewer or ContentControl or UserControl || (v is Border b && b.Classes.Contains("card")) || (v is StackPanel sp && sp.MaxWidth < 2000)).Take(14))
                Console.WriteLine($"  [measure] {v.GetType().Name,-16} {((Visual)v).Bounds.Width,6:F0} x {((Visual)v).Bounds.Height,5:F0}  x={((Visual)v).Bounds.X:F0}");
        var frame = win.CaptureRenderedFrame();
        var file = Path.Combine(outDir, $"{p}_{w}x{h}{(light ? "_light" : "")}.png");
        frame?.Save(file);
        Console.WriteLine(file);
    }
    win.Close();
  }
  catch (Exception e) { Console.Error.WriteLine("FAILED: " + e.Message); }
  finally { cts.Cancel(); }   // always stop the pump, even on error
});
Dispatcher.UIThread.MainLoop(cts.Token);   // pump the UI thread until the work is done
work.GetAwaiter().GetResult();
