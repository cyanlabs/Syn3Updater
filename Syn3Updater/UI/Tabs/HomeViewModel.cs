using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using AsyncAwaitBestPractices.MVVM;
using Cyanlabs.Syn3Updater.Helper;
using Cyanlabs.Syn3Updater.Model;
using Cyanlabs.Syn3Updater.Services;
using GraphQL;
using ModernWpf.Controls;
using Nito.AsyncEx;
using Ookii.Dialogs.Wpf;
using Windows.System;

namespace Cyanlabs.Syn3Updater.UI.Tabs
{
    internal class HomeViewModel : LanguageAwareBaseViewModel
    {
        #region Constructors

        private AsyncCommand<string> _startButton;
        private ActionCommand _refreshUSB;
        private AsyncCommand _regionInfo;
        private AsyncCommand<string> _visitFeedbackThread;
        public ActionCommand RefreshUSB => _refreshUSB ??= new ActionCommand(RefreshUsb);
        public AsyncCommand RegionInfo => _regionInfo ??= new AsyncCommand(RegionInfoAction);
        public AsyncCommand<string> StartButton => _startButton ??= new AsyncCommand<string>(StartAction);

        public AsyncCommand<string> VisitFeedbackThread => _visitFeedbackThread ??= new AsyncCommand<string>(VisitFeedbackThreadAction);

        #endregion

        #region Properties & Fields

        private string _apiAppReleases, _apiMapReleases;
        private string _stringCompatibility;
        private Api.ReleasesRoot _jsonReleases, _jsonMapReleases;
        private string _driveLetter;

        public string DriveLetter
        {
            get => _driveLetter;
            set => SetProperty(ref _driveLetter, value);
        }

        private string _driveName;

        public string DriveName
        {
            get => _driveName;
            set => SetProperty(ref _driveName, value);
        }

        private string _driveFileSystem;

        public string DriveFileSystem
        {
            get => _driveFileSystem;
            set => SetProperty(ref _driveFileSystem, value);
        }

        private USBHelper.Drive _selectedDrive;

        public USBHelper.Drive SelectedDrive
        {
            get => _selectedDrive;
            set
            {
                SetProperty(ref _selectedDrive, value);
                UpdateDriveInfo();
            }
        }

        private SModel.SRegion _selectedRegion;

        public SModel.SRegion SelectedRegion
        {
            get => _selectedRegion;
            set
            {
                SetProperty(ref _selectedRegion, value);
                if (value != null)
                    //we don't need to run this code using the WPF dispatcher, so use Cleary's libary 
                    AsyncContext.Run(async () => await UpdateSelectedRegion());
            }
        }

        private Api.Release _selectedRelease;

        public Api.Release SelectedRelease
        {
            get => _selectedRelease;
            set
            {
                SetProperty(ref _selectedRelease, value);
                if (value != null) AsyncContext.Run(async () => await UpdateSelectedRelease());
            }
        }

        private Api.Release _selectedMapVersion;

        public Api.Release SelectedMapVersion
        {
            get => _selectedMapVersion;
            set
            {
                SetProperty(ref _selectedMapVersion, value);
                if (value != null) AsyncContext.Run(async () => await UpdateSelectedMapVersion());
            }
        }

        private int _selectedMapVersionIndex;

        public int SelectedMapVersionIndex
        {
            get => _selectedMapVersionIndex;
            set => SetProperty(ref _selectedMapVersionIndex, value);
        }

        private int _selectedReleaseIndex;

        public int SelectedReleaseIndex
        {
            get => _selectedReleaseIndex;
            set => SetProperty(ref _selectedReleaseIndex, value);
        }

        private int _selectedRegionIndex;

        public int SelectedRegionIndex
        {
            get => _selectedRegionIndex;
            set => SetProperty(ref _selectedRegionIndex, value);
        }

        private ObservableCollection<USBHelper.Drive> _driveList;

        public ObservableCollection<USBHelper.Drive> DriveList
        {
            get => _driveList;
            set => SetProperty(ref _driveList, value);
        }

        private ObservableCollection<SModel.SRegion> _sRegions;

        public ObservableCollection<SModel.SRegion> SRegions
        {
            get => _sRegions;
            set => SetProperty(ref _sRegions, value);
        }

        private ObservableCollection<Api.Release> _sVersion;

        public ObservableCollection<Api.Release> SVersion
        {
            get => _sVersion;
            set => SetProperty(ref _sVersion, value);
        }

        private ObservableCollection<Api.Release> _sMapVersion;

        public ObservableCollection<Api.Release> SMapVersion
        {
            get => _sMapVersion;
            set => SetProperty(ref _sMapVersion, value);
        }

        private bool _sVersionsEnabled;

        public bool SVersionsEnabled
        {
            get => _sVersionsEnabled;
            set => SetProperty(ref _sVersionsEnabled, value);
        }

        private bool _sMapVersionsEnabled;

        public bool SMapVersionsEnabled
        {
            get => _sMapVersionsEnabled;
            set => SetProperty(ref _sMapVersionsEnabled, value);
        }

        private string _notes;

        public string Notes
        {
            get => _notes;
            set => SetProperty(ref _notes, value);
        }

        private bool _notesVisibility;

        public bool NotesVisibility
        {
            get => _notesVisibility;
            set => SetProperty(ref _notesVisibility, value);
        }

        private bool _feedbackVisibility;

        public bool FeedbackVisibility
        {
            get => _feedbackVisibility;
            set => SetProperty(ref _feedbackVisibility, value);
        }

        private ObservableCollection<SModel.Ivsu> _ivsuList;

        public ObservableCollection<SModel.Ivsu> IvsuList
        {
            get => _ivsuList;
            set => SetProperty(ref _ivsuList, value);
        }

        private string _currentRegion;

        public string CurrentRegion
        {
            get => _currentRegion;
            set
            {
                if (value == null) return;
                SetProperty(ref _currentRegion, value);
                AppMan.App.Settings.CurrentRegion = value;
            }
        }

        private string _currentVersion;

        public string CurrentVersion
        {
            get => _currentVersion;
            set => SetProperty(ref _currentVersion, value);
        }

        private string _currentNav;

        public string CurrentNav
        {
            get => _currentNav;
            set => SetProperty(ref _currentNav, value);
        }

        private string _currentProfile;

        public string CurrentProfile
        {
            get => _currentProfile;
            set => SetProperty(ref _currentProfile, value);
        }

        private string _downloadLocation;

        public string DownloadLocation
        {
            get => _downloadLocation;
            set
            {
                if (value != null) SetProperty(ref _downloadLocation, value);
            }
        }

        private string _installMode;

        public string InstallMode
        {
            get => _installMode;
            set => SetProperty(ref _installMode, value);
        }

        private string _installModeForced;

        public string InstallModeForced
        {
            get => _installModeForced;
            set => SetProperty(ref _installModeForced, value);
        }

        private string _my20Mode;

        public string My20Mode
        {
            get => _my20Mode;
            set => SetProperty(ref _my20Mode, value);
        }

        private bool _startEnabled;

        public bool StartEnabled
        {
            get => _startEnabled;
            set => SetProperty(ref _startEnabled, value);
        }

        private bool _downloadOnlyEnabled;

        public bool DownloadOnlyEnabled
        {
            get => _downloadOnlyEnabled;
            set => SetProperty(ref _downloadOnlyEnabled, value);
        }

        private string _feedbackUrl;

        public string FeedbackUrl
        {
            get => _feedbackUrl;
            set => SetProperty(ref _feedbackUrl, value);
        }

        private Dictionary<string, string> _magnetActions = new();

        #endregion

        #region Methods
        public void ReloadUSB()
        {
            AppMan.App.DriveLetter = "";
            RefreshUsb();
        }

        public void ReloadSettings()
        {
            StartEnabled = false;
            DownloadOnlyEnabled = false;
            CurrentNav = AppMan.App.Settings.CurrentNav ? LM.GetValue("String.Yes") : LM.GetValue("String.No");
            CurrentRegion = AppMan.App.Settings.CurrentRegion;
            CurrentVersion = AppMan.App.SVersion;
            DownloadLocation = AppMan.App.DownloadPath;
            My20Mode = AppMan.App.Settings.My20v2 switch
            {
                null => LM.GetValue("String.AutoDetect"),
                true => LM.GetValue("String.Enabled"),
                false => LM.GetValue("String.Disabled")
            };
            InstallModeForced = AppMan.App.ModeForced ? LM.GetValue("String.Yes") : LM.GetValue("String.No");
            InstallMode = AppMan.App.Settings.InstallMode;
            CurrentProfile = AppMan.App.MainSettings.Profile;
            AppMan.Logger.Info($"Current Details - Region: {CurrentRegion} - Version: {CurrentVersion} - Navigation: {CurrentNav}");
            UpdateInstallMode();

            if (AppMan.App.ClearSelections)
            {
                NotesVisibility = false;
                if (!string.IsNullOrEmpty(AppMan.App.Magnet))
                    _magnetActions = AppMan.App.Magnet
                        .Split(';')
                        .Select(part => part.Split('='))
                        .Where(part => part.Length == 2)
                        .ToDictionary(sp => sp[0], sp => sp[1]);

                SRegions = new ObservableCollection<SModel.SRegion>
                {
                    new() {Code = "EU", Name = "Europe"},
                    new() {Code = "NA", Name = "United States, Canada & Mexico"},
                    new() {Code = "CN", Name = "China"},
                    new() {Code = "ANZ", Name = "Australia, New Zealand, South America, Turkey & Taiwan"},
                    new() {Code = "ROW", Name = "Middle East, Africa, India, Sri Lanka, Israel, South East Asia, Caribbean & Central America"}
                };
                SVersion = new ObservableCollection<Api.Release>();
                SMapVersion = new ObservableCollection<Api.Release>();
                IvsuList = new ObservableCollection<SModel.Ivsu>();

                if (_magnetActions != null && _magnetActions?.Count != 0 && _magnetActions.ContainsKey("Region"))
                {
                    bool exists = SRegions.Any(x => x.Code == _magnetActions["Region"]);
                    if (exists) SelectedRegion = SRegions.FirstOrDefault(x => x.Code == _magnetActions["Region"]);
                }
                else
                {
                    SelectedRegionIndex = -1;
                    SelectedReleaseIndex = -1;
                    SelectedMapVersionIndex = -1;
                    SVersionsEnabled = false;
                    SMapVersionsEnabled = false;
                }
                
                RefreshUsb();
                AppMan.App.ClearSelections = false;
            }
        }

        private static async Task RegionInfoAction()
        {
            try
            {
                string regioninfo = await AppMan.Client.GetStringAsync(new Uri("https://api.cyanlabs.net/Syn3Updater/regioninfo/text"));
                if (await UIHelper.ShowDialog(regioninfo.Replace(@"\n",Environment.NewLine), LM.GetValue("Home.Region"), LM.GetValue("String.OK"), "Translate") != ContentDialogResult.Primary)
                { 
                    return;
                }
            }
            catch (Exception)
            {
                // ignored
            }
            await SystemHelper.OpenWebPage(AppMan.App.MainSettings.Lang.Contains("en")
                ? "https://api.cyanlabs.net/Syn3Updater/regioninfo"
                : $"https://translate.google.co.uk/translate?hl=&sl=en&tl={AppMan.App.MainSettings.Lang}&u=https%3A%2F%2Fapi.cyanlabs.net%2FSyn3Updater%2Fregioninfo");
        }

        private async void RefreshUsb()
        {
            try
            {
                DriveLetter = null;
                DriveList = USBHelper.RefreshDevices(true);
            }
            catch (Exception e)
            {
                UIHelper.ShowErrorDialog(e.GetFullMessage());
                AppMan.Logger.Info("ERROR: " + e.GetFullMessage());
            }
        }

        private async void UpdateDriveInfo()
        {
            StartEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null && SelectedDrive != null;
            DownloadOnlyEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null;
            if (SelectedDrive?.Encrypted != null && SelectedDrive.Encrypted)
            {
                await UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.EncryptedDrive"));
                RefreshUsb();
                return;
            }
            if (SelectedDrive?.Name == LM.GetValue("Home.NoUSBDir"))
            {
                VistaFolderBrowserDialog dialog = new();
                if (dialog.ShowDialog().GetValueOrDefault())
                {
                    string destination = dialog.SelectedPath;
                    
                    
                    if (!destination.StartsWith(@"\\")) //Not UNC Path
                    {
                        DriveInfo driveInfo = new(destination);
                        if (driveInfo.DriveType != DriveType.Fixed)
                            if (await UIHelper.ShowDialog(LM.GetValue("MessageBox.RemovableDriveFolder"), LM.GetValue("String.Notice"), LM.GetValue("String.No"),
                                    LM.GetValue("String.Yes")) == ContentDialogResult.None)
                                return;
                    }
                    if ((AppMan.App.DownloadPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).Contains(destination.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar))
                    {
                        await UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.CancelDownloadIsFolder"));
                        ReloadSettings();
                    }
                    else
                    {
                        DriveLetter = destination + "\\";
                        DriveFileSystem = "";
                        DriveName = "";
                        AppMan.App.DriveName = SelectedDrive?.Name;
                    }
                }
                else
                {
                    RefreshUsb();
                }
            }
            else if (SelectedDrive?.Name != null)
            {
                // Update app level vars
                AppMan.App.DriveFileSystem = SelectedDrive?.FileSystem;
                AppMan.App.DrivePartitionType = SelectedDrive?.PartitionType;
                AppMan.App.DriveName = SelectedDrive?.Name;
                AppMan.App.SkipFormat = SelectedDrive.SkipFormat;
                AppMan.App.DriveSize = SelectedDrive.Size;

                // Update local level vars
                DriveFileSystem = SelectedDrive?.PartitionType + " " + SelectedDrive?.FileSystem;
                DriveName = SelectedDrive?.Name;
                DriveLetter = SelectedDrive?.Letter;
            }
        }
        bool Launch = true;
        private async Task UpdateSelectedRegion()
        {
            NotesVisibility = false;
            SVersion?.Clear();
            SMapVersion?.Clear();
            if(Launch)
            {
                Launch = false;
            }
            else
            {
                IvsuList?.Clear();
                SelectedMapVersion = null;
                SelectedRelease = null;
                try
                {
                    GraphQLResponse<Api.ReleasesRoot> graphQlResponse = await AppMan.App.GraphQlClient.SendQueryAsync<Api.ReleasesRoot>(GraphQlRequests.GetReleases(SelectedRegion.Code));
                    _jsonReleases = graphQlResponse.Data;
                }
                catch (GraphQL.Client.Http.GraphQLHttpRequestException ex)
                {
                    string message = "Syn3 Updater requires a connection to the backend to function. Please ensure you are running the latest version from cyanlabs.net. If the issue persists, check our website for service status updates.";
                    MessageBox.Show(message, "Unable To Connect To CyanLabs", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (AppMan.App.Settings.CurrentNav && AppMan.App.Settings.CurrentVersion >= Api.ReformatVersion)
                    SVersion?.Add(new Api.Release { Name = LM.GetValue("String.OnlyMaps") });

            foreach (Api.Release item in _jsonReleases.Releases)
                    SVersion?.Add(item);

                SVersionsEnabled = true;
                StartEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null && SelectedDrive != null;
                DownloadOnlyEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null;

                if (SVersion != null && _magnetActions != null && _magnetActions?.Count != 0 && _magnetActions.ContainsKey("Release") && _magnetActions.ContainsKey("Region"))
                {
                    if (SVersion.Any(r => r.Name == "Sync " + _magnetActions?["Release"]))
                       SelectedRelease.Name = "Sync " + _magnetActions?["Release"];
                }
            }
        }

        private async Task UpdateSelectedRelease()
        {
            SMapVersion?.Clear();
            if (!AppMan.App.Settings.CurrentNav)
                SMapVersion?.Add(new Api.Release { Name = LM.GetValue("String.NonNavAPIM")});
            else if (AppMan.App.Settings.CurrentVersion >= Api.ReformatVersion && _selectedRelease.Name != LM.GetValue("String.OnlyMaps"))
                SMapVersion?.Add(new Api.Release { Name = LM.GetValue("String.KeepExistingMaps")});

            string license = string.Empty;
            if (AppMan.App.MainSettings.LicenseKey?.Length > 10) license = @"{licensekeys:{_contains:""" + AppMan.App.MainSettings.LicenseKey + @"""}},";

            if (!string.IsNullOrWhiteSpace(SelectedRelease.Name))
            {
                SelectedMapVersion = null;
                IvsuList?.Clear();

                foreach (Api.Release item in _jsonReleases.Releases)
                    if (item.Name == SelectedRelease.Name)
                    {
                        _stringCompatibility = item.Version.Substring(0, 3);


                        if (item.Feedbackurl == null)
                        {
                            FeedbackVisibility = false;
                            continue;
                        }

                        FeedbackUrl = item.Feedbackurl;
                        FeedbackVisibility = true;
                    }
                if (SelectedRelease.Name == LM.GetValue("String.OnlyMaps")) _stringCompatibility = "3.4";

                if (AppMan.App.Settings.CurrentNav)
                {
                    var graphQlResponse = await AppMan.App.GraphQlClient.SendQueryAsync<Api.ReleasesRoot>(GraphQlRequests.GetMapReleases(SelectedRegion.Code,license,_stringCompatibility));
                    _jsonMapReleases = graphQlResponse.Data;

                    foreach (Api.Release item in _jsonMapReleases.MapReleases)
                    {
                        if (SelectedRelease.Name == LM.GetValue("String.OnlyMaps") || AppMan.App.Settings.My20v2 == true)
                        {
                            if (item.ESN == true) { item.IsEnabled = false; }
                        }

                        SMapVersion.Add(item);
                    }
                
                        
                }

                SMapVersionsEnabled = true;
                StartEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null && SelectedDrive != null;
                DownloadOnlyEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null;

                if (SVersion != null && _magnetActions != null && _magnetActions?.Count != 0 && _magnetActions.ContainsKey("Maps") && _magnetActions.ContainsKey("Region"))
                {
                    string mapReleaseTmp = _magnetActions?["Maps"].Replace("_", " ");
                    if (SMapVersion != null && _magnetActions?.Count != 0 && SMapVersion.Any(x => x.Name == mapReleaseTmp)) 
                        SelectedMapVersion.Name = mapReleaseTmp;
                }
                if (SelectedRelease.Notes != null)
                {
                    Notes = "Release Notes:" + Environment.NewLine + SelectedRelease.Notes.Replace("\n", Environment.NewLine);
                    NotesVisibility = true;
                }
            }
        }

        private async Task UpdateSelectedMapVersion()
        {
            if (!string.IsNullOrWhiteSpace(SelectedMapVersion.Name))
            {
                if (SelectedMapVersion.Notes != null) {
                    Notes = "Map Release Notes:" + Environment.NewLine + SelectedMapVersion.Notes.Replace("\n", Environment.NewLine) + Environment.NewLine + Environment.NewLine + Notes;
                    NotesVisibility = true;
                }

                IvsuList?.Clear();

                UpdateInstallMode();
                
                string navtype = AppMan.App.Settings.CurrentNav ? "nav" : "nonnav";
                if (SelectedRelease.Name != LM.GetValue("String.OnlyMaps"))
                {
                    var graphQlResponse = await AppMan.App.GraphQlClient.SendQueryAsync<Api.ReleasesRoot>(GraphQlRequests.GetReleaseIvsus(SelectedRelease.Name, navtype));
                    Api.ReleasesRoot jsonIvsUs = graphQlResponse.Data;

                    foreach (Api.ReleasesIvsus item in jsonIvsUs.Releases[0].IvsusList.Where(ivsus => ivsus.Ivsu != null))
                        if (item.Ivsu.Regions.Contains("ALL") || item.Ivsu.Regions.Contains(SelectedRegion.Code))
                            IvsuList?.Add(new SModel.Ivsu
                            {
                                Type = item.Ivsu.Type,
                                Name = item.Ivsu.Name,
                                Version = item.Ivsu.Version,
                                Notes = item.Ivsu.Notes,
                                Url = item.Ivsu.Url,
                                Md5 = item.Ivsu.Md5,
                                Selected = true,
                                FileName = FileHelper.url_to_filename(item.Ivsu.Url),
                                FileSize = item.Ivsu.FileSize
                            });
                }
                else
                {
                    InstallMode = "autoinstall";
                    AppMan.App.InstallMode = InstallMode;
                }
                
                var graphQlResponse2 = await AppMan.App.GraphQlClient.SendQueryAsync<Api.ReleasesRoot>(GraphQlRequests.GetMapReleaseIvsus(SelectedMapVersion.Name));
                Api.ReleasesRoot jsonMapIvsUs = graphQlResponse2.Data;

                if (SelectedMapVersion.Name != LM.GetValue("String.NoMaps") && SelectedMapVersion.Name != LM.GetValue("String.NonNavAPIM") &&
                    SelectedMapVersion.Name != LM.GetValue("String.KeepExistingMaps"))
                    foreach (Api.ReleasesIvsus item in jsonMapIvsUs.MapReleases[0].IvsusList.Where(ivsus => ivsus.MapIvsu != null))
                        if (item.MapIvsu.Regions.Contains("ALL") || item.MapIvsu.Regions.Contains(SelectedRegion.Code))
                            IvsuList?.Add(new SModel.Ivsu
                            {
                                Type = item.MapIvsu.Type,
                                Name = item.MapIvsu.Name,
                                Version = item.MapIvsu.Version,
                                Notes = item.MapIvsu.Notes,
                                Url = item.MapIvsu.Url,
                                Md5 = item.MapIvsu.Md5,
                                Selected = true,
                                FileName = FileHelper.url_to_filename(item.MapIvsu.Url),
                                FileSize = item.MapIvsu.FileSize,
                                Source = item.MapIvsu.Source
                            });
            }
        }

        public void UpdateInstallMode()
        {
            bool cancelled = false;
            if (!string.IsNullOrWhiteSpace(SelectedMapVersion?.Name) && !string.IsNullOrWhiteSpace(SelectedRelease.Name) && !string.IsNullOrWhiteSpace(SelectedRegion.Code))
            {
                //MY20 and Less than 3.2
                if(AppMan.App.Settings.CurrentVersion < Api.ReformatVersion && AppMan.App.Settings.My20v2 == true)
                {
                    InstallMode = "autoinstall"; //Option H
                    Application.Current.Dispatcher.Invoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.MY20InvalidConfiguration")));
                    cancelled = true;
                }

                //Not My20 LESS THAN 3.2
                else if (AppMan.App.Settings.CurrentVersion < Api.ReformatVersion)
                {
                    if (!AppMan.App.ModeForced) 
                        InstallMode = "reformat"; //Option A
                }

                //Above 3.2 and  Below 3.4.19274
                else if (AppMan.App.Settings.CurrentVersion >= Api.ReformatVersion &&
                         AppMan.App.Settings.CurrentVersion < Api.BlacklistedVersion)
                {
                    //Update Nav?
                    if (SelectedMapVersion.Name == LM.GetValue("String.NoMaps") || SelectedMapVersion.Name == LM.GetValue("String.NonNavAPIM") ||
                        SelectedMapVersion.Name == LM.GetValue("String.KeepExistingMaps"))
                    {
                        if (!AppMan.App.ModeForced) 
                            InstallMode = "autoinstall"; //Option B
                    }
                    else if (AppMan.App.Settings.My20v2 == true && SelectedMapVersion.ESN)
                    {
                        Application.Current.Dispatcher.Invoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.MY20InvalidConfiguration")));
                        cancelled = true;
                    }
                    else if (AppMan.App.Settings.My20v2 == true || SelectedRelease.Name == LM.GetValue("String.OnlyMaps"))
                    {
                        InstallMode = "autoinstall"; //Option C
                    }
                    else if (!AppMan.App.ModeForced)
                    {
                        InstallMode = "reformat"; //Option D
                    }
                }

                //3.4.19274 or above
                else if (AppMan.App.Settings.CurrentVersion >= Api.BlacklistedVersion)
                {
                    //Update Nav?
                    if (SelectedMapVersion.Name == LM.GetValue("String.NoMaps") || SelectedMapVersion.Name == LM.GetValue("String.NonNavAPIM") ||
                        SelectedMapVersion.Name == LM.GetValue("String.KeepExistingMaps"))
                    {
                        if (!AppMan.App.ModeForced)
                            InstallMode = "autoinstall"; //Option E
                    }
                    else if (AppMan.App.Settings.My20v2 == true && SelectedMapVersion.ESN)
                    {
                        Application.Current.Dispatcher.Invoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.MY20InvalidConfiguration")));
                        cancelled = true;
                    }
                    else if (AppMan.App.Settings.My20v2 == true || SelectedRelease.Name == LM.GetValue("String.OnlyMaps"))
                    {
                        InstallMode = "autoinstall"; //Option F
                    }
                    else if (!AppMan.App.ModeForced)
                    {
                        InstallMode = "downgrade"; //Option G
                    }
                }

                AppMan.App.Action = "main";
                AppMan.App.InstallMode = InstallMode;
                StartEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null && SelectedDrive != null && !cancelled;
                DownloadOnlyEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null;
            }
        }

        private async Task StartAction(string type)
        {
            bool startEnabledOld = StartEnabled;
            StartEnabled = false;
            DownloadOnlyEnabled = false;
            if (type == "DownloadOnly")
            {
                if (await UIHelper.ShowWarningDialog(LM.GetValue("MessageBox.DownloadOnlyMode"), LM.GetValue("String.Warning") + "!",
                    LM.GetValue("Download.CancelButton"),
                    LM.GetValue("String.Yes")) == ContentDialogResult.Primary)
                {
                    AppMan.App.DownloadOnly = true;
                }
                else
                {
                    StartEnabled = startEnabledOld;
                    DownloadOnlyEnabled = true;
                    return;
                }
            }
            else if (string.IsNullOrEmpty(DriveLetter))
            {
                return;
            }
            else
            {
                AppMan.App.DownloadOnly = false;
            }

            if (!await HomeViewModelService.Download(InstallMode, IvsuList, SelectedRegion, SelectedRelease.Name, SelectedMapVersion.Name, DriveLetter, SelectedDrive))
            {
                StartEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null && SelectedDrive != null;
                DownloadOnlyEnabled = SelectedRelease != null && SelectedRegion != null && SelectedMapVersion != null;  
            }
        }

        public async Task VisitFeedbackThreadAction(string url)
        {
            if (!string.IsNullOrEmpty(url))
                await SystemHelper.OpenWebPage(url);
        }

        #endregion
    }
}