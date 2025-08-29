//using DiscordRPC;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Cyanlabs.Syn3Updater.Helper;
using Cyanlabs.Syn3Updater.Model;
using Cyanlabs.Syn3Updater.UI;
using Cyanlabs.Updater.Common;
using Newtonsoft.Json;
using GraphQL;
using GraphQL.Client.Http;
using GraphQL.Client.Serializer.Newtonsoft;

namespace Cyanlabs.Syn3Updater
{
    public class AppMan
    {
        #region Constructors

        private AppMan()
        {
            LauncherPrefs = new LauncherPrefs();
            MainSettings = new JsonMainSettings();
        }

        public static readonly SimpleLogger Logger = new();
        public ObservableCollection<SModel.Ivsu> Ivsus = new();
        public ObservableCollection<SModel.Ivsu> ExtraIvsus = new();
        public static AppMan App { get; } = new();
        public LauncherPrefs LauncherPrefs { get; set; }
        public JsonMainSettings MainSettings { get; set; }
        public JsonSettings Settings { get; set; }

        public static HttpClient Client = new();

        #endregion

        #region Events

        public void FireLanguageChangedEvent()
        {
            LanguageChangedEvent?.Invoke(this, EventArgs.Empty);
        }

        public void FireDownloadsTabEvent()
        {
            ShowDownloadsTab?.Invoke(this, EventArgs.Empty);
        }

        public void FireHomeTabEvent()
        {
            ShowHomeTab?.Invoke(this, EventArgs.Empty);
        }

        public void FireUtilityTabEvent()
        {
            ShowUtilityTab?.Invoke(this, EventArgs.Empty);
        }

        public void FireSettingsTabEvent()
        {
            ShowSettingsTab?.Invoke(this, EventArgs.Empty);
        }

        public void FireNewsTabEvent()
        {
            ShowNewsTab?.Invoke(this, EventArgs.Empty);
        }

        public event EventHandler LanguageChangedEvent;

        public event EventHandler ShowDownloadsTab;

        public event EventHandler ShowHomeTab;

        public event EventHandler ShowUtilityTab;

        public event EventHandler ShowSettingsTab;

        public event EventHandler ShowNewsTab;

        #endregion

        #region Properties & Fields

        private MainWindow _mainWindow;

        public string DownloadPath,
            DriveName,
            DrivePartitionType,
            DriveFileSystem,
            DriveNumber,
            DriveLetter,
            SelectedMapVersion,
            SelectedRelease,
            SelectedRegion,
            InstallMode,
            SVersion,
            Action,
            MainConfigFile,
            ProfileFile,
            ProgramDataPath,
            LocalAppDataPath,
            ProfilePath,
            Header,
            LauncherConfigFile,
            Magnet,
            Outdated,
            DriveSize;


        public bool SkipFormat, IsDownloading, UtilityCreateLogStep1Complete, AppsSelected, DownloadToFolder, ModeForced, Cancelled, DownloadOnly, ClearSelections, InvalidLog;
        
                    
        // Initiate GraphQlClient
        public GraphQLHttpClient GraphQlClient = new(Api.Syn3UpdaterGraphQl, new NewtonsoftJsonSerializer())
        {
            HttpClient = { DefaultRequestHeaders = { Authorization = new AuthenticationHeaderValue("Bearer", ApiSecret.GetToken()) } }
        };
        public int AppUpdated = 0;

        #endregion

        #region Methods

        public void Initialize()
        {
            JsonConvert.DefaultSettings = () => new JsonSerializerSettings { MaxDepth = 128 };
            SystemHelper.WriteRegistryHandler();
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            ServicePointManager.DefaultConnectionLimit = 8;
            ServicePointManager.Expect100Continue = false;
            string[] args = Environment.GetCommandLineArgs();
            if (!args.Contains("/launcher"))
            {
                try
                {
                    Process.Start("Launcher.exe");
                    Application.Current.Shutdown();
                }
                catch (Win32Exception e)
                {
                    Logger.Debug("Something went wrong launching 'Launcher.exe', skipping launcher!");
                    Logger.Debug(e.GetFullMessage());
                }
            }
            
            Logger.Debug($"Syn3 Updater {Assembly.GetEntryAssembly()?.GetName().Version} ({LauncherPrefs.ReleaseTypeInstalled}) is Starting");
            
            foreach (string value in args)
                if (value.Contains("syn3updater://"))
                    Magnet = value.Replace("syn3updater://", "").TrimEnd('/');

            Logger.Debug($"URL Intent: {Magnet}");
            
            ProgramDataPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData) + "\\CyanLabs\\Syn3Updater";
            LocalAppDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) + "\\CyanLabs\\Syn3Updater";
            ProfilePath = LocalAppDataPath + "\\Profiles\\";
            MainConfigFile = LocalAppDataPath + "\\settings.json";

            if (!Directory.Exists(ProgramDataPath))
            {
                Logger.Debug("ProgramDataPath does not exist, creating directory!");
                Directory.CreateDirectory(ProgramDataPath);
            }

            if (!Directory.Exists(LocalAppDataPath))
            {
                Logger.Debug("LocalAppDataPath does not exist, creating directory!");
                Directory.CreateDirectory(LocalAppDataPath);
            }

            if (!Directory.Exists(ProfilePath))
            {
                Logger.Debug("ProfilePath does not exist, creating directory!");
                Directory.CreateDirectory(ProfilePath);
            }

            if (File.Exists(MainConfigFile))
            {
                Logger.Debug("MainSettings found in LocalAppDataPath");
                try
                {
                    MainSettings = JsonConvert.DeserializeObject<JsonMainSettings>(File.ReadAllText(MainConfigFile));
                    Logger.Debug("MainSettings loaded successfully");
                }
                catch (JsonReaderException)
                {
                    File.Delete(MainConfigFile);
                    MainSettings = new JsonMainSettings();
                    Logger.Debug("MainSettings corrupted, creating new MainSettings");
                }
            }
            else if (File.Exists(ProgramDataPath + "\\settings.json"))
            {
                Logger.Debug("MainSettings found in ProgramDataPath but not LocalAppDataPath");
                File.Move(ProgramDataPath + "\\settings.json", MainConfigFile);
                try
                {
                    MainSettings = JsonConvert.DeserializeObject<JsonMainSettings>(File.ReadAllText(MainConfigFile));
                    Logger.Debug("MainSettings loaded successfully");
                }
                catch (JsonReaderException)
                {
                    File.Delete(MainConfigFile);
                    MainSettings = new JsonMainSettings();
                    Logger.Debug("MainSettings corrupted, creating new MainSettings");
                }
            }
            else
            {
                Logger.Debug("MainSettings not found in ProgramDataPath or LocalAppDataPath, Initializing new MainSettings");
                MainSettings = new JsonMainSettings();
            }

            LoadProfile();

            if (string.IsNullOrEmpty(MainSettings.LogPath)) MainSettings.LogPath = LocalAppDataPath + "\\Logs\\";
            try
            {
                if (!Directory.Exists(MainSettings.LogPath)) Directory.CreateDirectory(MainSettings.LogPath);
            }
            catch (DirectoryNotFoundException)
            {
                Logger.Debug("Unable to set desired log path, defaulting path");
                MainSettings.LogPath = LocalAppDataPath + "\\Logs\\";
            }

            if (File.Exists(ProgramDataPath + "\\launcherPrefs.json"))
                try
                {
                    LauncherPrefs = JsonConvert.DeserializeObject<LauncherPrefs>(File.ReadAllText(ProgramDataPath + "\\launcherPrefs.json"));
                }
                catch (JsonReaderException)
                {
                    File.Delete(ProgramDataPath + "\\launcherPrefs.json");
                    LauncherPrefs = new LauncherPrefs();
                }
            else
                LauncherPrefs = new LauncherPrefs();
            
            Logger.Debug("Determining language to use for the application");
            CultureInfo ci = CultureInfo.InstalledUICulture;
            if (string.IsNullOrWhiteSpace(MainSettings.Lang))
            {
                Logger.Debug($"Language is not set, inferring language from system culture. Lang={ci.TwoLetterISOLanguageName}");
                MainSettings.Lang = ci.TwoLetterISOLanguageName;
            }

            Thread.CurrentThread.CurrentUICulture = new CultureInfo(MainSettings.Lang);
            Logger.Debug($"Language is set to {MainSettings.Lang}");

            if (string.IsNullOrWhiteSpace(MainSettings.DownloadPath))
            {
                string downloads = SystemHelper.GetPath(SystemHelper.KnownFolder.Downloads);
                Logger.Debug($"Download location is not set, defaulting to {downloads}\\Syn3Updater\\");
                MainSettings.DownloadPath = $@"{downloads}\Syn3Updater\";
            }

            Logger.Debug("Determining download path to use for the application");
            DownloadPath = App.MainSettings.DownloadPath;

            try
            {
                if (!Directory.Exists(DownloadPath))
                {
                    Logger.Debug("Download location does not exist");
                    Directory.CreateDirectory(DownloadPath);
                }

                Logger.Debug($"Download path is set to {DownloadPath}");
            }
            catch (DirectoryNotFoundException)
            {
                string downloads = SystemHelper.GetPath(SystemHelper.KnownFolder.Downloads);
                Logger.Debug($"Download location was invalid, defaulting to {downloads}\\Syn3Updater\\");
                MainSettings.DownloadPath = $@"{downloads}\Syn3Updater\";
                DownloadPath = App.MainSettings.DownloadPath;
            }
            catch (IOException)
            {
                string downloads = SystemHelper.GetPath(SystemHelper.KnownFolder.Downloads);
                Logger.Debug($"Download location was invalid, defaulting to {downloads}\\Syn3Updater\\");
                MainSettings.DownloadPath = $@"{downloads}\Syn3Updater\";
                DownloadPath = App.MainSettings.DownloadPath;
            }

            Randomize();
            App.SaveSettings();
            Client.DefaultRequestHeaders.UserAgent.TryParseAdd(Header);
            Logger.Debug("Launching main window");
            if (_mainWindow == null) _mainWindow = new MainWindow();
            if (!_mainWindow.IsVisible) _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized) _mainWindow.WindowState = WindowState.Normal;
        }

        public void Randomize()
        {
            try
            {
                Random rand = new();

                GraphQLResponse<Api.UseragentRoot> graphQlResponse = Task.Run(async () => await GraphQlClient.SendQueryAsync<Api.UseragentRoot>(GraphQlRequests.GetUserAgents())).Result;
                Api.UseragentRoot UAs = graphQlResponse.Data;

                List<string> header = UAs.UserAgents.Select(ua => ua.Useragent.Replace("[PLACEHOLDER]", rand.Next(ua.Min, ua.Max).ToString())).ToList();

                int index = rand.Next(header.Count);
                Header = header[index];
            }
            catch (Exception)
            {
                Logger.Debug("Unable to access CyanLabs API, defaulting UserAgent");
                Header = "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:70.0) Gecko/20100101 Firefox/86.0";
            }
        }
        
        
        public void LoadProfile()
        {
            if (MainSettings != null && string.IsNullOrEmpty(MainSettings.Profile))
            {
                Logger.Debug("No Profile specified, using default");
                MainSettings.Profile = "default";
                ProfileFile = ProfilePath + "default.json";
            }
            else if (MainSettings != null)
            {
                ProfileFile = ProfilePath + $"{MainSettings.Profile}.json";
            }
            
            if (File.Exists(ProfileFile))
            {
                try
                {
                    Settings = JsonConvert.DeserializeObject<JsonSettings>(File.ReadAllText(ProfileFile));
                    Logger.Debug($"Profile {MainSettings.Profile} loaded");
                }
                catch (JsonReaderException)
                {
                    File.Delete(ProfileFile);
                    Settings = new JsonSettings();
                    Logger.Debug($"Profile {MainSettings.Profile} corrupted, creating new {MainSettings.Profile} Profile");
                }
            }
            else
            {
                Logger.Debug($"Profile {MainSettings.Profile} not found, Initializing new {MainSettings.Profile} Profile");
                Settings = new JsonSettings();
            }

            string version = App.Settings.CurrentVersion.ToString();
            try
            {
                SVersion = version.Length == 7
                    ? $"{version[0]}.{version[1]}.{version.Substring(2, version.Length - 2)}"
                    : "0.0.00000";
            }
            catch (IndexOutOfRangeException e)
            {
                Logger.Debug(e.GetFullMessage());
            }
        }

        public void SaveSettings()
        {
            try
            {
                string mainJson = JsonConvert.SerializeObject(MainSettings, Formatting.Indented);
                File.WriteAllText(MainConfigFile, mainJson);
                Logger.Debug("MainSettings saved successfully");
            }
            catch (IOException)
            {
                Logger.Debug($"Unable to save main settings, another process is accessing {MainConfigFile}");
            }

            try
            {
                string profileJson = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                File.WriteAllText(ProfileFile, profileJson);
                Logger.Debug($"Profile {MainSettings.Profile} saved successfully");
            }
            catch (IOException)
            {
                Logger.Debug($"Unable to save profile settings, another process is accessing {ProfileFile}");
            }
        }

        public void ResetSettings()
        {
            try
            {
                Logger.Debug($"ALL SETTINGS RESET");
                if (File.Exists(MainConfigFile)) File.Delete(MainConfigFile);
                if (File.Exists(ProfileFile)) File.Delete(ProfileFile);
                if (File.Exists(LauncherConfigFile)) File.Delete(LauncherConfigFile);
            }
            catch (Exception e)
            {
                Logger.Debug(e.GetFullMessage());
            }
        }

        public async void UpdateLauncherSettings()
        {
            string json = JsonConvert.SerializeObject(LauncherPrefs);
            LauncherConfigFile = ProgramDataPath + "\\launcherPrefs.json";
            if (!Directory.Exists(ProgramDataPath)) Directory.CreateDirectory(ProgramDataPath);
            try
            {
                File.WriteAllText(ProgramDataPath + "\\launcherPrefs.json", json);
                Logger.Debug("LauncherPrefs saved successfully");
            }
            catch (IOException e)
            {
                Logger.Debug($"Unable to save LauncherPrefs, another process is accessing {LauncherConfigFile}");
                await UIHelper.ShowErrorDialog(e.GetFullMessage());
            }
        }

        public void RestartApp()
        {
            Logger.Debug($"Syn3 Updater {Assembly.GetEntryAssembly()?.GetName().Version} is Restarting");
            Process.Start(Application.ResourceAssembly.Location);
            Application.Current.Shutdown();
        }

        public void Exit()
        {
            Logger.Debug("Saving settings before shutdown");
            try
            {
                App.SaveSettings();
            }
            catch (Exception e)
            {
                Logger.Debug(e.GetFullMessage());
            }

            Logger.Debug("Writing log to disk before shutdown");
            try
            {
                File.WriteAllText(LocalAppDataPath + "\\applog.txt", JsonConvert.SerializeObject(Logger.Log));
            }
            catch (UnauthorizedAccessException e)
            {
                Logger.Debug(e.GetFullMessage());
            }
            catch (IOException e)
            {
                Logger.Debug(e.GetFullMessage());
            }

            Logger.Debug("Syn3 Updater is shutting down");
            Application.Current.Shutdown();
        }

        #endregion
    }
}