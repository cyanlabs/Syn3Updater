using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cyanlabs.Launcher.Updating;
using Cyanlabs.Updater.Common;
using Newtonsoft.Json;

namespace Cyanlabs.Launcher
{
    /// <summary>
    ///     Interaction logic for UpgradingWindow.xaml
    /// </summary>
    public partial class UpgradingWindow
    {
        #region Constructors

        public UpgradingWindow()
        {
            InitializeComponent();
            Vm = DataContext as UpgradingViewModel;
            Core.UpgradingWindow = this;
        }

        #endregion

        #region Properties & Fields

        public static string BaseFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        public UpgradingViewModel Vm;

        #endregion

        #region Methods

        private async void AcrylicWindow_Loaded(object sender, RoutedEventArgs e)
        {
            await Task.Delay(100);
            await StartCheck();
        }

        private static readonly string CommonDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CyanLabs", "Syn3Updater");
        private static readonly string LocalDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CyanLabs", "Syn3Updater");

        /// <summary>Forcefully closes every running Syn3 Updater (they would hold the files that are about to be replaced).</summary>
        private static async Task CloseRunningApp()
        {
            foreach (Process proc in Process.GetProcessesByName("Syn3Updater"))
                try
                {
                    proc.Kill();
                    proc.WaitForExit(5000);
                }
                catch
                {
                    // ignored, the update will retry locked files
                }
                finally
                {
                    proc.Dispose();
                }

            await Task.Delay(300);
        }

        private async Task StartCheck()
        {
            // Allow Windows 7 and old .NET Framework versions to reach TLS 1.2 sites
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            LauncherLog log = new(Path.Combine(CommonDataFolder, "launcher.log"));
            log.Write($"Launcher starting (Windows build {OsInfo.BuildNumber()})");
            string appPath = Path.Combine(BaseFolder, "Syn3Updater.exe");

            await CloseRunningApp();

            // Undo a half-finished update from a previous run (power cut, crash) and remove the old launcher's leftover
            try
            {
                if (InstallTransaction.Recover(BaseFolder)) log.Write("Rolled back an interrupted update");
                string legacy = Path.Combine(BaseFolder, "Launcher_OldVersion.exe");
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch (Exception ex)
            {
                log.Write("Cleanup failed: " + ex.Message);
            }

            // Load saved preferences; a corrupt file is replaced with defaults
            string prefsFile = Path.Combine(CommonDataFolder, "launcherPrefs.json");
            if (File.Exists(prefsFile))
                try
                {
                    Core.LauncherPrefs = JsonConvert.DeserializeObject<LauncherPrefs>(File.ReadAllText(prefsFile)) ?? new LauncherPrefs();
                }
                catch (JsonException)
                {
                    File.Delete(prefsFile);
                    Core.LauncherPrefs = new LauncherPrefs();
                }

            ReleaseChannel channel = ToChannel(Core.LauncherPrefs.ReleaseBranch);
            UpdateRequest request = new()
            {
                InstallDir = BaseFolder,
                Channel = channel,
                InstalledTag = Core.LauncherPrefs.ReleaseInstalled,
                InstalledChannel = ToChannel(Core.LauncherPrefs.ReleaseTypeInstalled),
                OsBuild = OsInfo.BuildNumber(),
                Arch = Compatibility.ArchLabel(),
                DataDirs = new[] { LocalDataFolder, CommonDataFolder },
                BackupRoot = Path.Combine(CommonDataFolder, "Backups"),
                CloseApp = CloseRunningApp,
                Log = log.Write
            };

            // Start and wait for the update check to complete
            bool updated = false;
            using (GitHubReleaseSource source = new("cyanlabs", "Syn3Updater"))
            {
                Progress<UpdateProgress> progress = new(p =>
                {
                    Vm.Message = p.Message;
                    if (p.Percent.HasValue) Vm.Percentage = 100 - p.Percent.Value;
                });
                UpdateOutcome outcome = await new UpdateEngine(source).RunAsync(request, progress, CancellationToken.None);
                log.Write($"Result: {outcome.Kind} - {outcome.Message}");

                switch (outcome.Kind)
                {
                    case OutcomeKind.Installed:
                        updated = true;
                        // Record the update only now that every file is in place (always four numeric parts so old launchers can still read it)
                        Core.LauncherPrefs.ReleaseInstalled = outcome.Version.NumericString;
                        Core.LauncherPrefs.ReleaseTypeInstalled = (LauncherPrefs.ReleaseType)(int)channel;
                        Directory.CreateDirectory(CommonDataFolder);
                        File.WriteAllText(prefsFile, JsonConvert.SerializeObject(Core.LauncherPrefs));
                        break;

                    case OutcomeKind.Skipped:
                        Vm.Message = outcome.Message;
                        await Task.Delay(2500);
                        break;

                    case OutcomeKind.UpToDate when !string.IsNullOrEmpty(outcome.Message) && outcome.Message != "Up to date.":
                        Vm.Message = outcome.Message;
                        await Task.Delay(3500);
                        break;

                    case OutcomeKind.Failed:
                        MessageBox.Show(outcome.Message, "Syn3 Updater", MessageBoxButton.OK, MessageBoxImage.Warning);
                        if (!File.Exists(appPath))
                        {
                            Application.Current.Shutdown();
                            return;
                        }

                        break;
                }
            }

            // Update complete (or not needed): run Syn3Updater.exe
            string magnet = "";
            foreach (string value in Environment.GetCommandLineArgs())
                if (value.Contains("syn3updater://"))
                    magnet = value;

            Process p = new()
            {
                StartInfo =
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = appPath,
                    WorkingDirectory = BaseFolder ?? string.Empty,
                    Arguments = "/launcher" + (updated ? " /updated" : "") + (!string.IsNullOrEmpty(magnet) ? " " + magnet : ""),
                    UseShellExecute = false
                }
            };
            p.Start();

            await Task.Delay(2000);

            // Exit Launcher
            Application.Current.Shutdown();
        }

        private static ReleaseChannel ToChannel(LauncherPrefs.ReleaseType type) => type == LauncherPrefs.ReleaseType.Beta ? ReleaseChannel.Beta : type == LauncherPrefs.ReleaseType.Alpha ? ReleaseChannel.Alpha : ReleaseChannel.Stable;

        #endregion
    }
}