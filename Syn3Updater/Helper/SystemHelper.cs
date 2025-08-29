using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Cyanlabs.Syn3Updater.Model;
using Microsoft.Win32;

namespace Cyanlabs.Syn3Updater.Helper
{
    /// <summary>
    ///     Helper class containing methods to retrieve specific file system paths.
    ///     https://stackoverflow.com/a/21953690
    /// </summary>
    public static class SystemHelper
    {
        #region Properties & Fields

        [DllImport("Shell32.dll")]
        private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

        [Flags]
        private enum KnownFolderFlags : uint
        {
            DontVerify = 0x00004000
        }

        private static readonly string[] KnownFolderGuids =
        {
            "{374DE290-123F-4565-9164-39C4925E467B}" // Downloads
        };

        public enum KnownFolder
        {
            Downloads
        }

        #endregion

        #region Methods

        /// <summary>
        ///     Gets the current path to the specified known folder as currently configured. This does
        ///     not require the folder to be existent.
        /// </summary>
        /// <param name="knownFolder">The known folder which current path will be returned.</param>
        /// <returns>The default path of the known folder.</returns>
        /// <exception cref="System.Runtime.InteropServices.ExternalException">
        ///     Thrown if the path
        ///     could not be retrieved.
        /// </exception>
        public static string GetPath(KnownFolder knownFolder)
        {
            return GetPath(knownFolder, false);
        }

        /// <summary>
        ///     Gets the current path to the specified known folder as currently configured. This does
        ///     not require the folder to be existent.
        /// </summary>
        /// <param name="knownFolder">The known folder which current path will be returned.</param>
        /// <param name="defaultUser">
        ///     Specifies if the paths of the default user (user profile
        ///     template) will be used. This requires administrative rights.
        /// </param>
        /// <returns>The default path of the known folder.</returns>
        /// <exception cref="System.Runtime.InteropServices.ExternalException">
        ///     Thrown if the path
        ///     could not be retrieved.
        /// </exception>
        public static string GetPath(KnownFolder knownFolder, bool defaultUser)
        {
            return GetPath(knownFolder, KnownFolderFlags.DontVerify, defaultUser);
        }

        private static string GetPath(KnownFolder knownFolder, KnownFolderFlags flags, bool defaultUser)
        {
            int result = SHGetKnownFolderPath(new Guid(KnownFolderGuids[(int) knownFolder]), (uint) flags, new IntPtr(defaultUser ? -1 : 0), out IntPtr outPath);
            if (result >= 0)
            {
                string path = Marshal.PtrToStringUni(outPath);
                Marshal.FreeCoTaskMem(outPath);
                return path;
            }

            throw new ExternalException("Unable to retrieve the known folder path. It may not be available on this system.", result);
        }

        public static string GetOsFriendlyName()
        {
            string result = string.Empty;
            ManagementObjectSearcher searcher = new("SELECT Caption FROM Win32_OperatingSystem");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                ManagementObject os = (ManagementObject) o;
                result = os["Caption"].ToString();
                break;
            }

            return $"{result} ({Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "ReleaseId", "")})";
        }

        public static int GetOsBuildNumber()
        {
            int result = 0;
            ManagementObjectSearcher searcher = new("SELECT BuildNumber FROM Win32_OperatingSystem");
            foreach (ManagementBaseObject o in searcher.Get())
            {
                ManagementObject os = (ManagementObject)o;
                result = Convert.ToInt16(os["BuildNumber"]);
                break;
            }

            return result;
        }

        public static void WriteRegistryHandler()
        {
            try
            {
                Registry.CurrentUser.CreateSubKey(@"Software\Classes\syn3updater\Shell\Open\Command");
                Registry.CurrentUser.OpenSubKey(@"Software\Classes\syn3updater", true)?.SetValue("", "URL:syn3updater protocol");
                Registry.CurrentUser.OpenSubKey(@"Software\Classes\syn3updater", true)?.SetValue("URL Protocol", "");
                Registry.CurrentUser.OpenSubKey(@"Software\Classes\syn3updater\Shell\Open\Command", true)
                    ?.SetValue("", $"\"{AppDomain.CurrentDomain.BaseDirectory}Launcher.exe\" %1");
            }
            catch
            {
                // ignored
            }
        }

        public static void DeleteRegistryHandler()
        {
            Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\syn3updater");
        }

        public static async Task OpenWebPage(string url)
        {
            try
            {
                Process.Start(url);
            }
            catch (System.ComponentModel.Win32Exception)
            {
                Clipboard.SetText(url);
                await Application.Current.Dispatcher.BeginInvoke(() => UIHelper.ShowDialog(LM.GetValue("MessageBox.NoBrowser"),LM.GetValue("String.Notice"),LM.GetValue("Download.CancelButton")));
            }
        }

        #endregion
    }
}