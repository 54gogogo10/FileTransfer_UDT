using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("TrFileTransfer")]
[assembly: System.Reflection.AssemblyProduct("TrFileTransfer")]
[assembly: System.Reflection.AssemblyVersion("2.6.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.6.0.0")]

namespace TrFileTransfer
{
    static class Program
    {
        private const string SingleInstanceMutexName = "Local\\TrFileTransfer.SingleInstance.9e5b65a2";
        private const string ActivateEventName = "Local\\TrFileTransfer.Activate.9e5b65a2";

        /// <summary>Entry point: single-instance guard, global exception logging, main form.</summary>
        [STAThread]
        static void Main()
        {
            bool createdNew;
            using (var mutex = new Mutex(true, SingleInstanceMutexName, out createdNew))
            {
                if (!createdNew)
                {
                    // A copy is already running — wake its window instead of starting twice
                    try
                    {
                        using (var evt = EventWaitHandle.OpenExisting(ActivateEventName))
                            evt.Set();
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.ThreadException += (s, e) => HandleUiException(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => WriteCrashLog(e.ExceptionObject as Exception);

                // Listen for "activate" requests from second launches (background thread,
                // so it never keeps the process alive after Run returns)
                var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                ThreadPool.QueueUserWorkItem(delegate
                {
                    while (activateEvent.WaitOne())
                    {
                        var form = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
                        if (form != null && form.IsHandleCreated)
                        {
                            try
                            {
                                form.BeginInvoke((MethodInvoker)delegate
                                {
                                    form.Show();
                                    form.WindowState = FormWindowState.Normal;
                                    form.Activate();
                                });
                            }
                            catch { }
                        }
                    }
                });

                Application.Run(new MainForm());
            }
        }

        private static void HandleUiException(Exception ex)
        {
            WriteCrashLog(ex);
            try
            {
                MessageBox.Show(L.CrashPrompt(ex.Message), L.DlgError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
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
