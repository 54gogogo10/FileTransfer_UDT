using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Per-window DWM chrome: dark title bar (Win10 1809+), rounded corners
    /// (Win11), and Mica backdrop (Win11 22H2+). Every call degrades silently on
    /// systems that don't support the attribute — older Windows keeps the opaque
    /// dark theme.</summary>
    internal static class UiChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int cxLeftWidth, cxRightWidth, cyTopHeight, cyBottomHeight;
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_PRE = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;   // DWMWCP_ROUND = 2
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;        // DWMSBT_MAINWINDOW = 2 (Mica)

        /// <summary>Call once from the window constructor. When <paramref name="mica"/>
        /// is true and the OS supports the Mica backdrop, the window background is made
        /// translucent so the system material shows through.</summary>
        internal static void ApplyDark(Window window, bool mica = false)
        {
            window.SourceInitialized += (s, e) =>
            {
                try
                {
                    var hwnd = new WindowInteropHelper(window).Handle;
                    int on = 1;
                    if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int)) != 0)
                        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE, ref on, sizeof(int));

                    int round = 2; // DWMWCP_ROUND
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

                    if (mica)
                    {
                        int backdrop = 2; // DWMSBT_MAINWINDOW
                        if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
                        {
                            var margins = new Margins
                            {
                                cxLeftWidth = -1,
                                cxRightWidth = -1,
                                cyTopHeight = -1,
                                cyBottomHeight = -1
                            };
                            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) == 0)
                            {
                                // Translucent base so the Mica material tints through;
                                // cards stay opaque on top of it
                                window.Background = new SolidColorBrush(Color.FromArgb(0xA0, 0x17, 0x19, 0x1F));
                            }
                        }
                    }
                }
                catch { }
            };
        }
    }
}
