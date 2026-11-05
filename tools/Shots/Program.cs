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
    if (light) Application.Current!.RequestedThemeVariant = ThemeVariant.Light;
    var vm = new MainWindowViewModel();
    _ = vm.LoadAdAsync(); vm.Home.OnShown();
    var win = new MainWindow { DataContext = vm, Width = w, Height = h };
    win.Show();
        void ApplyDemo()
        {
            // sample state so the "active" layouts can be reviewed without touching real drives or the network
            var drive = new DriveInfoModel { DeviceId = "/dev/sdb", MountPath = "/run/media/scott/CYANLABS", Name = "SanDisk Ultra", VolumeName = "CYANLABS", SizeBytes = 31_914_983_424, FreeBytes = 31_000_000_000, FileSystem = "exFAT", PartitionType = "MBR" };
            vm.Home.Drives.Add(drive); vm.Home.SelectedDrive = drive; vm.Home.NoDrives = false;
            vm.Home.Releases.Add(new ReleaseItem { Display = "Sync 3.4.26057" });
            vm.Home.Packages.Add(new PackageRow(new Ivsu { Type = "APPS", Name = "PU5T-14G381-AB", Version = "3.4.26057", FileSize = 1_400_000_000 }));
            vm.Home.Packages.Add(new PackageRow(new Ivsu { Type = "VOICE", Name = "PU5T-14G391-AC", FileSize = 310_000_000 }));
            vm.Home.Packages.Add(new PackageRow(new Ivsu { Type = "GRACENOTES", Name = "PU5T-14G423-AB", FileSize = 90_000_000 }));
            vm.Home.InstallMode = "autoinstall"; vm.Home.StartEnabled = true; vm.Home.DownloadOnlyEnabled = true;
            vm.Home.Notes = "Release Notes:\nAdded: improvements."; 
            vm.Downloads.Headline = "Sync 3.4.26057 - NA - Keep Existing Maps"; vm.Downloads.IsRunning = true;
            vm.Downloads.Status = "Downloading: PU5T-14G381-AB_1722.TAR.GZ"; vm.Downloads.CurrentFile = "PU5T-14G381-AB_1722.TAR.GZ";
            vm.Downloads.FileProgress = 63; vm.Downloads.TotalProgress = 31; vm.Downloads.FileCounter = "File 2 of 4";
            vm.Downloads.Queue.Add(new QueueItem("PU5T-14G386-BB.TAR.GZ", "3 KB") { IsDone = true });
            vm.Downloads.Queue.Add(new QueueItem("PU5T-14G381-AB_1722.TAR.GZ", "1.3 GB") { IsActive = true });
            vm.Downloads.Queue.Add(new QueueItem("PU5T-14G391-AC.TAR.GZ", "296 MB"));
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
