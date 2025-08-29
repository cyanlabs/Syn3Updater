using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using Cyanlabs.Syn3Updater.Helper;
using Cyanlabs.Syn3Updater.Model;
using Newtonsoft.Json;

namespace Cyanlabs.Syn3Updater.UI
{
    /// <summary>
    ///     Interaction logic for CrashWindow.xaml
    /// </summary>
    public partial class CrashWindow : Window
    {
        public string ErrorReportUrl;

        public CrashWindow()
        {
            InitializeComponent();
        }

        private void Close_OnClick(object sender, RoutedEventArgs e)
        {
            AppMan.App.Exit();
        }

        public string SendReport(Exception exception)
        {
            if (SystemHelper.GetOsBuildNumber() < 10000)
            {
                // Disable logging on Windows versions below 10
                MessageBox.Show("Something went wrong, As you are on a unsupported OS no support will be provided", "Unsupported Operating System", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            } 
            else
            {
                try
                {
                    CrashContainer crashContainer = new();
                    StackTrace st = new(exception, true);
                    StackFrame frame = st.GetFrame(st.FrameCount - 1);

                    crashContainer.ErrorName = exception.GetType().ToString();
                    if (frame != null)
                        crashContainer.ErrorLocation = $"{frame.GetFileName()}/{frame.GetMethod().Name}/{frame.GetFileLineNumber()}";
                    crashContainer.Logs = AppMan.Logger.Log;

                    Dictionary<string, string> values = new()
                {
                    {"computername", Environment.MachineName},
                    {"detail", JsonConvert.SerializeObject(crashContainer)},
                    {"version", Assembly.GetEntryAssembly()?.GetName().Version.ToString()},
                    {"error", crashContainer.ErrorName},
                    {"message", exception.Message},
                    {"operatingsystem", SystemHelper.GetOsFriendlyName()},
                    {"branch", AppMan.App.LauncherPrefs.ReleaseTypeInstalled.ToString()}
                };

                    HttpRequestMessage httpRequestMessage = new()
                    {
                        Method = HttpMethod.Post,
                        RequestUri = new Uri(Api.CrashLogPost),
                        Headers = {
                        { nameof(HttpRequestHeader.Authorization), $"Bearer {ApiSecret.GetToken()}" },
                    },
                        Content = new FormUrlEncodedContent(values)
                    };

                    HttpResponseMessage response = AppMan.Client.SendAsync(httpRequestMessage).GetAwaiter().GetResult();
                    return response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                }
                catch (HttpRequestException)
                {
                    return null;
                }
            }
        }

        private void ClickQRCode(object sender, RoutedEventArgs e)
        {
            SystemHelper.OpenWebPage(ErrorReportUrl);
        }

        private void ViewReport(object sender, RoutedEventArgs e)
        {
            SystemHelper.OpenWebPage(ErrorReportUrl);
        }

        private void ResetSettings_Click(object sender, RoutedEventArgs e)
        {
            AppMan.App.ResetSettings();
            AppMan.App.RestartApp();
        }

        public class CrashContainer
        {
            public string ErrorName { get; set; }
            public string ErrorLocation { get; set; }
            public List<SimpleLogger.LogEntry> Logs { get; set; } = new();
        }
    }
}