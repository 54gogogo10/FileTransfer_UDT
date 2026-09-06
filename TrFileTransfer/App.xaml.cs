using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;

namespace TrFileTransfer
{
    /// <summary>Application entry: single-instance guard, global exception logging,
    /// dark-themed resource dictionaries. Replaces the old WinForms Program.cs.</summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = "Local\\TrFileTransfer.SingleInstance.9e5b65a2";
        private const string ActivateEventName = "Local\\TrFileTransfer.Activate.9e5b65a2";

        private Mutex _mutex;

        /// <summary>Full path of the running exe (used by AutoStart / Updater / firewall hint).</summary>
        public static string ExePath
        {
            get { return Assembly.GetExecutingAssembly().Location; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _mutex = new Mutex(true, SingleInstanceMutexName, out createdNew);
            if (!createdNew)
            {
                // A copy is already running — wake its window instead of starting twice
                try
                {
                    using (var evt = EventWaitHandle.OpenExisting(ActivateEventName))
                        evt.Set();
                }
                catch { }
                Shutdown(0);
                return;
            }

            DispatcherUnhandledException += (s, args) =>
            {
                HandleUiException(args.Exception);
                args.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
                WriteCrashLog(args.ExceptionObject as Exception);

            // Listen for "activate" requests from second launches (background thread,
            // so it never keeps the process alive after shutdown)
            var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            ThreadPool.QueueUserWorkItem(delegate
            {
                while (activateEvent.WaitOne())
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            var w = MainWindow;
                            if (w != null)
                            {
                                w.Show();
                                w.WindowState = WindowState.Normal;
                                w.Activate();
                            }
                        }));
                    }
                    catch { }
                }
            });

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _mutex?.ReleaseMutex();
                _mutex?.Dispose();
            }
            catch { }
            base.OnExit(e);
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
