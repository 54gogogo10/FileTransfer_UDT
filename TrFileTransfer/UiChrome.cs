using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Per-window DWM chrome: dark/light title bar, rounded corners (Win11),
    /// and Mica backdrop (Win11 22H2+). Registered windows are re-applied when the
    /// theme switches; every DWM call degrades silently on older systems.</summary>
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

        private sealed class Entry
        {
            public WeakReference Window;
            public bool Mica;
        }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static bool _hooked;

        /// <summary>Call once from the window constructor. Title-bar darkness follows
        /// ThemeManager; when <paramref name="mica"/> is true the window gets a
        /// translucent themed background over the system backdrop.</summary>
        internal static void ApplyDark(Window window, bool mica = false)
        {
            var entry = new Entry { Window = new WeakReference(window), Mica = mica };
            Entries.Add(entry);

            if (!_hooked)
            {
                _hooked = true;
                ThemeManager.Changed += () =>
                {
                    for (int i = Entries.Count - 1; i >= 0; i--)
                    {
                        var w = Entries[i].Window.Target as Window;
                        if (w == null) { Entries.RemoveAt(i); continue; }
                        Repaint(w, Entries[i].Mica);
                    }
                };
            }

            window.SourceInitialized += (s, e) => Repaint(window, mica);
        }

        private static void Repaint(Window window, bool mica)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                // Title bar darkness follows the active theme (attr 20 on newer
                // Windows, 19 on 1809-era builds)
                int dark = ThemeManager.IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_PRE, ref dark, sizeof(int));

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
                            window.Background = ThemeManager.MicaBackground();
                        }
                    }
                }
            }
            catch { }
        }
    }
}
