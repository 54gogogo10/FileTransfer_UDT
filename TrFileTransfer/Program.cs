using System;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("TrFileTransfer")]
[assembly: System.Reflection.AssemblyProduct("TrFileTransfer")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("2.0.0.0")]

namespace TrFileTransfer
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
