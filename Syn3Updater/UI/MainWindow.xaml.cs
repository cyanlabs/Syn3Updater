using System;
using System.ComponentModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Cyanlabs.Syn3Updater.Helper;
using Cyanlabs.Syn3Updater.Model;
using Microsoft.Win32;

namespace Cyanlabs.Syn3Updater.UI
{
    /// <summary>
    ///     Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        #region Methods

        public MainWindow()
        {
            InitializeComponent();
            AppMan.Logger.Debug("MainWindow Initialized");
            try
            {
                if (SystemHelper.GetOsBuildNumber() < 10000)
                {
                    MessageBox.Show("This application requires Windows 10 or newer, It may work but no support will be provided!", "Unsupported Operating System", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception)
            {
            }

            if (CryptoConfig.AllowOnlyFipsAlgorithms)
            {
                // Do not replace with ContentDialog
                MessageBox.Show(
                    "Syn3 Updater has detected that 'Use FIPS Compliant algorithms for encryption, hashing, and signing.' is enforced via Group Policy, Syn3 Updater will be unable to validate any files using MD5 with this policy enforced and therefore is currently unable to function\n\nThe application will now close!",
                    LM.GetValue("String.Notice"), MessageBoxButton.OK, MessageBoxImage.Error);
                AppMan.App.Exit();
            }

            try
            {
                object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access",
                    "EnableControlledFolderAccess", "0");
                if (v != null && v.ToString() != "0")
                    if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows Defender\Windows Defender Exploit Guard\Controlled Folder Access\AllowedApplications",
                        Assembly.GetExecutingAssembly().Location, null) == null)
                        MessageBox.Show(
                            "Syn3 Updater has detected that 'Controlled Folder Access' is enabled on this computer\n\nSyn3 Updater may be unable to read or write to files at certain locations, to prevent potential issues please exclude Syn3 Updater from Controlled Folder Access or ensure you are using a folder that is not protected.",
                            LM.GetValue("String.Notice"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch
            {
                // ignored
            }
        }

        private MainWindowViewModel Vm => (MainWindowViewModel) DataContext;

        private void Grid_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if ((sender as Grid)?.DataContext is MainWindowViewModel.TabItem lvm)
            {
                if (string.IsNullOrWhiteSpace(lvm.Key))
                    Vm.HamburgerExtended = !Vm.HamburgerExtended;
                else
                    Vm.CurrentTab = lvm.Key;
            }
        }

        private void Grid_MouseDown_1(object sender, MouseButtonEventArgs e)
        {
            Vm.CurrentTab = "settings";
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            AppMan.App.Exit();
        }

        #endregion
    }
}