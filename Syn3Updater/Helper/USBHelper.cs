using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using System.Xml.Linq;
using Cyanlabs.Syn3Updater.Converter;
using Cyanlabs.Syn3Updater.Model;
using GraphQL;
using ModernWpf.Controls;
using Newtonsoft.Json;
using Ookii.Dialogs.Wpf;
using Formatting = Newtonsoft.Json.Formatting;

namespace Cyanlabs.Syn3Updater.Helper
{
    /// <summary>
    ///     Helper class for USB related methods such as reading disk information via WMI
    /// </summary>
    public class USBHelper
    {
        #region Properties & Fields

        public class Drive
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public string Size { get; set; }
            public string Letter { get; set; }
            public string FileSystem { get; set; }
            public string PartitionType { get; set; }
            public string FreeSpace { get; set; }
            public bool SkipFormat { get; set; }
            public string VolumeName { get; set; }
            public string Model { get; set; }
            public bool Fake { get; set; }
            public bool Encrypted { get; set; }
            public string EncryptionStatus { get; set; }
            
        }

        #endregion

        #region Methods

        /// <summary>
        ///     Refreshes device list using WMI queries
        /// </summary>
        /// <param name="fakeusb">Set to true to add 'Download Only' option to the list</param>
        /// <returns>ObservableCollection of all USB Drives as type Drive</returns>
        public static ObservableCollection<Drive> RefreshDevices(bool fakeusb)
        {
            ObservableCollection<Drive> driveList = new();
            ManagementObjectSearcher driveQuery = new("select * from Win32_DiskDrive Where InterfaceType = \"USB\" OR MediaType = \"External hard disk media\"");
            foreach (ManagementBaseObject n in driveQuery.Get())
            {
                ManagementObject d = (ManagementObject)n;
                string friendlySize = MathHelper.BytesToString(Convert.ToInt64(d.Properties["Size"].Value));

                if (friendlySize == "0B") continue;

                Drive drive = new();
                string partitionQueryText = $@"associators of {{{d.Path.RelativePath}}} where AssocClass = Win32_DiskDriveToDiskPartition";
                ManagementObjectSearcher partitionQuery = new(partitionQueryText);
                foreach (ManagementBaseObject o in partitionQuery.Get())
                {
                    ManagementObject p = (ManagementObject)o;
                    string logicalDriveQueryText = $@"associators of {{{p.Path.RelativePath}}} where AssocClass = Win32_LogicalDiskToPartition";
                    ManagementObjectSearcher logicalDriveQuery = new(logicalDriveQueryText);
                    foreach (ManagementBaseObject managementBaseObject in logicalDriveQuery.Get())
                    {
                        ManagementObject ld = (ManagementObject)managementBaseObject;
                        ManagementObjectSearcher encryptedDriveQuery = new("\\\\.\\ROOT\\CIMV2\\Security\\MicrosoftVolumeEncryption",$"select * from Win32_EncryptableVolume Where DriveLetter = \"{ld.Properties["DeviceId"].Value}\"");
                        foreach (ManagementBaseObject encryptedDriveObject in encryptedDriveQuery.Get())
                        {
                            uint encryptionStatus = (uint)encryptedDriveObject.GetPropertyValue("ProtectionStatus");
                            drive.Encrypted = encryptionStatus != 0;
                            if (drive.Encrypted) drive.EncryptionStatus = LM.GetValue("String.Encrypted");
                        }

                        if (!drive.Encrypted)
                        {
                            drive.FileSystem += ld.GetPropertyValue("FileSystem");
                            drive.VolumeName = string.IsNullOrWhiteSpace((string)ld.GetPropertyValue("VolumeName")) ? "" : ld.GetPropertyValue("VolumeName").ToString();
                        }

                        drive.Letter = ld.GetPropertyValue("DeviceId").ToString();
                        drive.PartitionType = p.GetPropertyValue("Type").ToString().Contains("GPT:") ? "GPT" : "MBR";
                        
                        drive.Name = d.GetPropertyValue("Caption").ToString();
                        
                        drive.Path = d.Path.RelativePath;
                        drive.FreeSpace = MathHelper.BytesToString(Convert.ToInt64(ld.GetPropertyValue("FreeSpace")));
                        drive.Model = d.GetPropertyValue("Model").ToString();
                        drive.Size = friendlySize;
                        drive.Fake = false;
                        if (drive.FileSystem == "exFAT" && drive.PartitionType == "MBR" && drive.Name == "CYANLABS")
                            drive.SkipFormat = true;
                        else
                            drive.SkipFormat = false;
                    }
                }

                // Add to array of drives
                driveList.Add(drive);
            }

            if (fakeusb)
                driveList.Add(new Drive { Path = "", Name = LM.GetValue("Home.NoUSBDir"), Fake = true });

            // Return a list of drives
            return driveList;
        }

        /// <summary>
        ///     Generates a log.txt file on the root of the USB Drive and a log-date.txt file in LogPath
        /// </summary>
        /// <param name="log">Additional log to append, usually the log textbox</param>
        /// <param name="upload">Set to true to upload log file <see cref="UploadLog" />, else false to only save it to USB drive</param>
        public async static Task GenerateLog(string log, bool upload)
        {
            StringBuilder data = new($@"CYANLABS - SYN3 UPDATER - V{Assembly.GetExecutingAssembly().GetName().Version}{Environment.NewLine}");
            data.Append(@"Branch: ").Append(AppMan.App.LauncherPrefs.ReleaseTypeInstalled).Append(Environment.NewLine)
                .Append(@"Operating System: ").Append(SystemHelper.GetOsFriendlyName()).Append(Environment.NewLine)
                .Append(Environment.NewLine)
                .Append($@"PREVIOUS CONFIGURATION{Environment.NewLine}")
                .Append($@"Version: {AppMan.App.SVersion}{Environment.NewLine}")
                .Append($@"Region: {AppMan.App.Settings.CurrentRegion}{Environment.NewLine}")
                .Append($@"Navigation: {AppMan.App.Settings.CurrentNav}{Environment.NewLine}")
                .Append($@"Install Mode: {AppMan.App.Settings.InstallMode} ({AppMan.App.InstallMode}){Environment.NewLine}")
                .Append($@"Install Mode Overridden: {AppMan.App.ModeForced}{Environment.NewLine}");

            if (AppMan.App.Settings.My20v2 == null)
                data.Append($@"My20 Protection Enabled: {LM.GetValue("String.AutoDetect")}{Environment.NewLine}");
            else if (AppMan.App.Settings.My20v2 == true)
                data.Append($@"My20 Protection Enabled: {LM.GetValue("String.Enabled")}{Environment.NewLine}");
            else if (AppMan.App.Settings.My20v2 == false)
                data.Append($@"My20 Protection Enabled: {LM.GetValue("String.Disabled")}{Environment.NewLine}");

            data.Append(Environment.NewLine).Append("DESTINATION DETAILS").Append(Environment.NewLine);
            if (AppMan.App.DownloadToFolder)
                data.Append("Mode: Directory").Append(Environment.NewLine)
                    .Append(@"Path: ").Append(AppMan.App.DriveLetter).Append(Environment.NewLine);
            else
                data.Append("Mode: Drive").Append(Environment.NewLine)
                    .Append("Model: ").Append(AppMan.App.DriveName).Append(Environment.NewLine)
                    .Append("Size: ").Append(AppMan.App.DriveSize).Append(Environment.NewLine)
                    .Append("FileSystem: ").Append(AppMan.App.DriveFileSystem).Append(Environment.NewLine)
                    .Append("Partition Type: ").Append(AppMan.App.DrivePartitionType).Append(Environment.NewLine);

            string driveletter = AppMan.App.DriveLetter;
            if (File.Exists($@"{driveletter}\reformat.lst"))
                data.Append(Environment.NewLine)
                    .Append("REFORMAT.LST").Append(Environment.NewLine)
                    .Append(File.ReadAllText($@"{driveletter}\reformat.lst")).Append(Environment.NewLine);

            if (File.Exists($@"{driveletter}\autoinstall.lst"))
                data.Append(Environment.NewLine)
                    .Append("AUTOINSTALL.LST").Append(Environment.NewLine)
                    .Append(File.ReadAllText($@"{driveletter}\autoinstall.lst")).Append(Environment.NewLine);

            if (Directory.Exists($@"{driveletter}\SyncMyRide"))
            {
                data.Append(Environment.NewLine);
                DirectoryInfo di = new($@"{driveletter}\SyncMyRide");
                FileInfo[] allFiles = di.GetFiles("*", SearchOption.AllDirectories);
                data.Append($"FILES ({allFiles.Length}){Environment.NewLine}");
                foreach (FileInfo file in allFiles)
                    data.Append($"{file.Name} ({MathHelper.BytesToString(file.Length)}){Environment.NewLine}");
                data.Append(Environment.NewLine);
            }

            data.Append("LOG").Append(Environment.NewLine)
                .Append(log);
            string complete = data.ToString();
            string currentDate = DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss");
            try
            {
                File.WriteAllText($@"{driveletter}\log.txt", complete);
                File.WriteAllText($@"{AppMan.App.MainSettings.LogPath}log-{currentDate}.txt", complete);
            }
            catch (DirectoryNotFoundException e)
            {
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(e.GetFullMessage()));
            }
            catch (UnauthorizedAccessException e)
            {
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(e.GetFullMessage()));
            }

            if (upload)
                await UploadLog(complete);
        }

        /// <summary>
        ///     Uploads log to our API server for easy diagnostics
        /// </summary>
        /// <param name="log">Contents of log file</param>
        public async static Task UploadLog(string log)
        {
            try
            {
                Dictionary<string, string> values = new()
                {
                    { "computername", Environment.MachineName },
                    { "contents", log }
                };
                HttpRequestMessage httpRequestMessage = new()
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(Api.LogPost),
                    Headers = {
                        { nameof(HttpRequestHeader.Authorization), $"Bearer {ApiSecret.GetToken()}" },
                    },
                    Content = new FormUrlEncodedContent(values)
                };
                HttpResponseMessage response = await AppMan.Client.SendAsync(httpRequestMessage);
                string responseString = await response.Content.ReadAsStringAsync();           
                var output = JsonConvert.DeserializeAnonymousType(responseString, new { uuid = "", status = "" });
                await SystemHelper.OpenWebPage(Api.LogUrl + output.uuid);
            }
            catch (Exception e)
            {
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(e.GetFullMessage()));
            }
        }

        //TODO: Fma965: refactor/move this
        public async Task UploadFile()
        {
            if (_node != null &&
                await Application.Current.Dispatcher.Invoke(() => UIHelper.ShowDialog(LM.GetValue("MessageBox.AsBuiltVinWarning"), LM.GetValue("String.Notice"), LM.GetValue("Download.CancelButton"),
                    LM.GetValue("String.Upload"))) == ContentDialogResult.Primary)
            {
                FormUrlEncodedContent formContent = new(new[]
                {
                    new KeyValuePair<string, string>("xml", _node.ToString()),
                    new KeyValuePair<string, string>("apim", _apimDetails.PartNumber),
                    new KeyValuePair<string, string>("nav", _apimDetails.Nav.ToString()),
                    new KeyValuePair<string, string>("size", _apimDetails.Size.ToString()),
                    new KeyValuePair<string, string>("vin", _apimDetails.VIN)
                });

                HttpRequestMessage httpRequestMessage = new()
                {
                    Method = HttpMethod.Post,
                    RequestUri = new Uri(Api.AsBuiltPost),
                    Headers = {
                        { nameof(HttpRequestHeader.Authorization), $"Bearer {ApiSecret.GetToken()}" },
                    },
                    Content = formContent
                };

                HttpResponseMessage response = await AppMan.Client.SendAsync(httpRequestMessage);
                var output = JsonConvert.DeserializeAnonymousType(await response.Content.ReadAsStringAsync(), new { filename = "", status = "" });
                await SystemHelper.OpenWebPage(Api.AsBuiltOutput + output.filename);
            }
        }

        //TODO: Fma965: refactor/move this
        public struct ApimDetails
        {
            public int Size;
            public bool Nav;
            public string PartNumber;
            public string VIN;
        }

        private ApimDetails _apimDetails;
        private XDocument _node;

        public async Task<string[]> LogParseXmlAction()
        {
            string LogXmlDetails = "";
            string LogXmlDetails2 = "";
            string LogXmlDetails3 = "";
            string convertedsversion = "";
            VistaFileDialog dialog = new VistaOpenFileDialog { Filter = "Interrogator Log XML Files|*.xml" };
            if (!dialog.ShowDialog().GetValueOrDefault())
            {
                AppMan.App.Cancelled = true;
                return new[] { "" };
            }

            try
            {
                AppMan.App.Cancelled = false;
                _apimDetails = new();
                XmlDocument doc = new();
                //TODO: swtich to Async once code moves to dotnet 5+ 
                string xml = FileHelper.RemoveInvalidXmlChars(File.ReadAllText(dialog.FileName));
                if (dialog.FileName != null)
                    File.Copy(dialog.FileName, AppMan.App.MainSettings.LogPath + "\\" + Path.GetFileName(dialog.FileName), true);
                doc.Load(new StringReader(xml));
                string json = JsonConvert.SerializeXmlNode(doc, Formatting.Indented);
                Interrogator.InterrogatorModel interrogatorLog = JsonConvert.DeserializeObject<Interrogator.InterrogatorModel>(json);
                _apimDetails.VIN = interrogatorLog?.POtaModuleSnapShot.PVin;
                LogXmlDetails = $"VIN: {interrogatorLog?.POtaModuleSnapShot.PVin}{Environment.NewLine}";

                Interrogator.D2P1Did[] d2P1Did = interrogatorLog?.POtaModuleSnapShot.PNode.D2P1EcuAcronym.D2P1State.D2P1Gateway.D2P1Did;
                string sappname = d2P1Did!.Where(x => x.DidType == "Embedded Consumer Operating System Part Number").Select(x => x.D2P1Response).Single();
                
                string apimmodel = d2P1Did.Where(x => x.DidType == "ECU Delivery Assembly Number").Select(x => x.D2P1Response).Single();
                if (apimmodel == "" && AppMan.App.Action == "logutilitymy20")
                {
                    throw new AssemblyModelNotFound();
                }
                else if (apimmodel == "")
                {
                    apimmodel = "Unknown";
                }
                LogXmlDetails += $"{LM.GetValue("Utility.APIMModel")}: {apimmodel}{Environment.NewLine}";
                
                GraphQLResponse<Api.My20ModelsRoot> graphQlResponse = await AppMan.App.GraphQlClient.SendQueryAsync<Api.My20ModelsRoot>(GraphQlRequests.GetMy20Models());
                Api.My20ModelsRoot output = graphQlResponse.Data;
                
                foreach (Api.My20Models unused in output.My20Models.Where(my20 => apimmodel.StartsWith(my20.Model)))
                {
                    AppMan.App.Settings.My20v2 = true;
                }

                if (AppMan.App.Settings.My20v2 != true) AppMan.App.Settings.My20v2 = false;
                string apimsize = "Unknown", apimfree = "Unknown";
                if (interrogatorLog.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.D2P1PartitionHealth.Where(x => x.Type == "/fs/images/").Any())
                {
                    apimsize = interrogatorLog.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.D2P1PartitionHealth.Where(x => x.Type == "/fs/images/").Select(x => x.Total).Single();

                    _apimDetails.PartNumber = apimmodel;
                    if (double.TryParse(apimsize.Remove(apimsize.Length - 1), NumberStyles.Any, CultureInfo.InvariantCulture, out double apimsizeint))
                    {
                        if (apimsizeint >= 0 && apimsizeint <= 8)
                        {
                            _apimDetails.Nav = false;
                            _apimDetails.Size = 8;
                        }
                        else if (apimsizeint >= 9 && apimsizeint <= 16)
                        {
                            _apimDetails.Nav = false;
                            _apimDetails.Size = 16;
                        }
                        else if (apimsizeint >= 17 && apimsizeint <= 32)
                        {
                            _apimDetails.Nav = true;
                            _apimDetails.Size = 32;
                        }
                        else if (apimsizeint >= 33 && apimsizeint <= 64)
                        {
                            _apimDetails.Nav = true;
                            _apimDetails.Size = 64;
                        }
                    }

                    apimfree = interrogatorLog?.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.D2P1PartitionHealth.Where(x => x.Type == "/fs/images/").Select(x => x.Available).Single();
                }

                if (_apimDetails.Nav)
                    LogXmlDetails += $"{LM.GetValue("Utility.APIMType")}: Navigation {Environment.NewLine}";
                else
                    LogXmlDetails += $"{LM.GetValue("Utility.APIMType")}: Non-Navigation {Environment.NewLine}";

                LogXmlDetails += $"{LM.GetValue("Utility.APIMSize")}: {_apimDetails.Size}GB {Environment.NewLine}";

                
                LogXmlDetails += $"{LM.GetValue("Utility.APIMFree")}: {apimfree} {Environment.NewLine}";

                LogXmlDetails2 += $"Partition Type = Free / Total{Environment.NewLine}";
                if (interrogatorLog != null)
                {
                    foreach (Interrogator.D2P1PartitionHealth d2P1PartitionHealth in interrogatorLog.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.D2P1PartitionHealth)
                        LogXmlDetails2 += $"{d2P1PartitionHealth.Type} = {d2P1PartitionHealth.Available} / {d2P1PartitionHealth.Total}{Environment.NewLine}";

                    
                    List<string> packages = SyncHexToAscii.ConvertPackages(d2P1Did.Where(x => x.DidValue == "8060").Select(x => x.D2P1Response).Single());
                    List<string> packagescont = SyncHexToAscii.ConvertPackages(d2P1Did.Where(x => x.DidValue == "8061").Select(x => x.D2P1Response).Single()); 
                    packages.AddRange(packagescont);
                    
                    LogXmlDetails2 += $"{Environment.NewLine}Installed Packages{Environment.NewLine}";
                    LogXmlDetails2 = packages.Aggregate(LogXmlDetails2, (current, package) => current + (package + Environment.NewLine));

                    List<AsBuilt.DID> asBuiltValues = new();

                    LogXmlDetails3 += "APIM AsBuilt";
                    foreach (Interrogator.D2P1Did d2P1Didchild in d2P1Did.Where(x => x.DidType.Contains("Direct Configuraation DID DE")))
                    {
                        LogXmlDetails3 += $"{Environment.NewLine}{d2P1Didchild.DidValue}: {d2P1Didchild.D2P1Response.ToUpper()}";
                        asBuiltValues.Add(new AsBuilt.DID { ID = d2P1Didchild.DidValue, Text = d2P1Didchild.D2P1Response.ToUpper() });
                    }
                    try
                    {
                        var graphQlResponse2 = await AppMan.App.GraphQlClient.SendQueryAsync<Api.IvsuRoot>(GraphQlRequests.IvsuVersionLookup(sappname));
                        Api.IvsuRoot sversion = graphQlResponse2.Data;
   
                        convertedsversion = sversion.Ivsus[0].Version;
                        if (AppMan.App.Action == "logutilitymy20")
                        {
                            AppMan.App.Settings.CurrentVersion = Convert.ToInt32(sversion.Ivsus[0].Version.Replace(".", ""));
                            AppMan.App.SVersion = convertedsversion;
                        }
                        else if (convertedsversion != AppMan.App.SVersion)
                        {
                            if (await Application.Current.Dispatcher.Invoke(() => UIHelper.ShowDialog(string.Format(LM.GetValue("MessageBox.UpdateCurrentVersionUtility"), AppMan.App.SVersion, convertedsversion),
                                LM.GetValue("String.Notice"), LM.GetValue("String.No"), LM.GetValue("String.Yes"))) == ContentDialogResult.Primary)
                            {
                                AppMan.App.Settings.CurrentVersion = Convert.ToInt32(sversion.Ivsus[0].Version.Replace(".", ""));
                                AppMan.App.SVersion = convertedsversion;
                            }
                        }
                        LogXmlDetails += $"{LM.GetValue("Home.Version")} {convertedsversion} ({sappname}){Environment.NewLine}{Environment.NewLine}";

                        LogXmlDetails += interrogatorLog?.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.LogGeneratedDateTime;
                    }
                    catch (Exception)
                    {
                        //likely no internet connection ignore
                    }

                    if (_apimDetails.Size == 0) throw new AssemblyVersionIncompatible();

                    AsBuilt.DirectConfiguration asbult = new()
                    {
                        VEHICLE = new AsBuilt.VEHICLE
                        {
                            MODULE = "Syn3Updater",
                            VIN = "",
                            VEHICLEID = apimmodel,
                            VEHICLEYEAR = interrogatorLog.POtaModuleSnapShot.PNode.D2P1AdditionalAttributes.LogGeneratedDateTime.ToString(),
                            DID = asBuiltValues
                        }
                    };
                    json = JsonConvert.SerializeObject(asbult, Formatting.Indented);
                }

                _node = JsonConvert.DeserializeXNode(json, "DirectConfiguration");
            }
            catch (NullReferenceException)
            {
                AppMan.App.Cancelled = true;
                AppMan.App.InvalidLog = true;
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.LogUtilityInvalidFile")));
            }
            catch (XmlException)
            {
                AppMan.App.Cancelled = true;
                AppMan.App.InvalidLog = true;
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.LogUtilityInvalidFile")));
            }
            catch (InvalidOperationException)
            {
                AppMan.App.Cancelled = true;
                AppMan.App.InvalidLog = true;
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.LogUtilityInvalidFile")));
            }
            catch (AssemblyModelNotFound)
            {
                AppMan.App.Cancelled = true;
                AppMan.App.InvalidLog = true;
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(LM.GetValue("MessageBox.LogUtilityNoModelFound")));
            }
            catch (AssemblyVersionIncompatible)
            {
                AppMan.App.Cancelled = true;
                AppMan.App.InvalidLog = true;
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(string.Format(LM.GetValue("MessageBox.LogUtilityIncompatibleVersion"), convertedsversion)));
            }

            return new[] { LogXmlDetails, LogXmlDetails2, LogXmlDetails3 };
        }

        public static async Task LogPrepareUSBAction(Drive selectedDrive, string driveLetter, string action = "logutility")
        {
            //Reset ApplicationManager variables
            AppMan.App.Ivsus.Clear();
            AppMan.App.DriveLetter = driveLetter;
            AppMan.App.Action = action;
            AppMan.App.SelectedRelease = "Interrogator Log Utility";
            AppMan.App.DriveNumber = selectedDrive.Path.Replace("Win32_DiskDrive.DeviceID=\"\\\\\\\\.\\\\PHYSICALDRIVE", "").Replace("\"", "");

            int? currentversion = AppMan.App.Settings.CurrentVersion;
            try
            {
                switch (AppMan.App.Settings.CurrentVersion)
                {
                    //3.0
                    case < 3200000:
                        Api.InterrogatorTool = await ApiHelper.GetSpecialIvsu(Api.SpecialPackages.LogToolAA);
                        break;
                    
                    //3.2 and 3.3
                    case >= 3200000 and < 3400000:
                        Api.InterrogatorTool = await ApiHelper.GetSpecialIvsu(Api.SpecialPackages.LogToolAB);
                        break;
                    
                    //3.4.18437 to 3.4.20237
                    case >= 3418347 and <= 3420237:
                        Api.InterrogatorTool = await ApiHelper.GetSpecialIvsu(Api.SpecialPackages.LogToolAC);
                        break;
                    
                    //3.4.20282+
                    case >= 3420282:
                        Api.InterrogatorTool = await ApiHelper.GetSpecialIvsu(Api.SpecialPackages.LogToolAD);
                        break;
                }
            }
            catch (TaskCanceledException e)
            {
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowErrorDialog(e.GetFullMessage()));
                return;
            }

            AppMan.App.Ivsus.Add(Api.InterrogatorTool);
            AppMan.App.InstallMode = "autoinstall";

            if (await SanityCheckHelper.CancelDownloadCheck(selectedDrive) || Api.InterrogatorTool == null)
                return;

            AppMan.App.IsDownloading = true;
            AppMan.Logger.Info("Starting process (Logging Utility");
            AppMan.App.FireDownloadsTabEvent();
        }

        #endregion
    }
}