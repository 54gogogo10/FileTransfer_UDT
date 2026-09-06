using System;
using System.IO;
using System.Reflection;
using System.Windows;

namespace TrFileTransfer
{
    /// <summary>Application entry: global exception logging and the dark-themed
    /// resource dictionaries. Multiple instances may run side by side (the two
    /// processes share the same Config file — last save wins).</summary>
    public partial class App : Application
    {
        /// <summary>Full path of the running exe (used by AutoStart / Updater / firewall hint).</summary>
        public static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            // Repaint the token brushes before any window parses (Config is loaded
            // again — harmlessly — inside MainWindow)
            Config.Load();
            ThemeManager.Initialize();

            DispatcherUnhandledException += (s, args) =>
            {
                HandleUiException(args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrashLog(args.ExceptionObject as Exception);

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        private static void HandleUiException(Exception ex)
        {
            WriteCrashLog(ex);
            try
            {
                MessageBox.Show(L.CrashPrompt(ex.Message), L.DlgError,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch { }
        }

        /// <summary>Appends the exception to %AppData%\TrFileTransfer\logs\crash.log; silent on failure.</summary>
        private static void WriteCrashLog(Exception ex)
        {
            if (ex == null) return;
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrFileTransfer", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "crash.log"),
                    string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}: {2}\r\n{3}\r\n\r\n",
                        DateTime.Now, ex.GetType().Name, ex.Message, ex.StackTrace));
            }
            catch { }
        }
    }
}
