using System;
using System.Windows;

namespace Cyanlabs.Launcher
{
    /// <summary>
    ///     Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            // report the problem, then let it crash as before
            DispatcherUnhandledException += (s, args) => CrashReports.Report("unhandled-exception", args.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, args) => CrashReports.Report("unhandled-exception", args.ExceptionObject as Exception);
            base.OnStartup(e);
        }
    }
}
