using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AMCStudios.Installer
{
    public partial class App : System.Windows.Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            DispatcherUnhandledException += (s, args) =>
            {
                LogCrash(args.Exception);
                TryToastCrash(args.Exception);
                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                LogCrash(args.ExceptionObject as Exception);

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
            {
                LogCrash(args.Exception);
                args.SetObserved();
            };
        }

        private static void TryToastCrash(Exception ex)
        {
            try
            {
                if (Current?.MainWindow is MainWindow mw && mw.IsLoaded)
                {
                    mw.ShowCrashToast(ex.Message);
                }
            }
            catch { }
        }

        private static void LogCrash(Exception ex)
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppPrefs.DataRoot, "crash.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
            }
            catch { }
        }
    }
}
