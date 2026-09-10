using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WinForms = System.Windows.Forms;

namespace TrFileTransfer
{
    /// <summary>Main window (WPF port of MainForm) — mode/protocol selector, server/client
    /// panels, progress cards, and the log console. All transfer logic is unchanged;
    /// only the UI shell and thread marshaling (Dispatcher) differ from the WinForms build.</summary>
    public partial class MainWindow : Window
    {
        /// <summary>Default update source: this project's GitHub Releases. The updater
        /// detects api.github.com URLs and reads version/download URL from the release,
        /// SHA256 from the .sha256 sidecar asset.</summary>
        internal const string DefaultUpdateUrl =
            "https://api.github.com/repos/54gogogo10/FileTransfer_UDT/releases/latest";

        /// <summary>Assembly version for display (major.minor.build).</summary>
        private static string AppVersion
        {
            get
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        // Tray & services
        private WinForms.NotifyIcon _notifyIcon;
        private WinForms.ContextMenuStrip _trayMenu;
        private HttpShareServer _httpShare;
        private QrDialog _qrDialog;
        private DiscoveryServer _discoveryServer;
        private bool _trayExit;
        private volatile bool _windowClosed;
        private TextReceivedDialog _textRecvDialog;

        // State
        private readonly List<string> _recentFiles = new List<string>();
        private readonly List<DeviceInfo> _knownDevices = new List<DeviceInfo>();
        private Guid? _pendingResumeSession;
        private TransferServer _server;
        private TransferClient _client;
        private TransferUdtServer _serverUdt;
        private TransferUdtClient _clientUdt;
        /// <summary>Active parallel (concurrency > 1) single-file send, held so the
        /// Cancel button can actually stop it.</summary>
        private ConcurrentTransfer _concurrent;
        private int _serverCount;
        private readonly Dictionary<IPEndPoint, Border> _tcpCards = new Dictionary<IPEndPoint, Border>();
        private readonly Dictionary<IPEndPoint, Border> _udtCards = new Dictionary<IPEndPoint, Border>();
        private readonly StatsStore _stats = new StatsStore(StatsStore.DefaultPath);
        /// <summary>Receive confirmations already granted this session (ip → granted at),
        /// so concurrent-chunk and follow-up sends do not re-prompt.</summary>
        private readonly Dictionary<string, DateTime> _confirmedReceives = new Dictionary<string, DateTime>();

        // Monitor mode
        private CancellationTokenSource _monitorCts;
        private int _monitorSrcPort;
        private int _monitorSpeedBytesPerSec;
        // Snapshot of the panel state taken on the UI thread when monitoring starts —
        // the monitor loop runs on the threadpool and must never touch WPF controls
        // (DependencyObjects enforce thread affinity even for reads)
        private bool _monitorIsTcp;
        private string _monitorPairing;
        private readonly List<string> _monitorQueue = new List<string>();
        private readonly object _monitorLock = new object();

        // Fan-out (one file/folder -> many devices in parallel)
        private readonly List<object> _fanOutClients = new List<object>();
        private volatile bool _fanOutRunning;

        // Pause/resume: snapshot of the running single-connection send, so the resume
        // click can restart it on the same 0x03/0x04 session (server checkpoint does the rest)
        private sealed class PauseState
        {
            public string Path;
            public bool IsFolder;
            public string Ip;
            public int Port;
            public bool IsTcp;
            public int SrcPort;
            public int Concurrency;
            public bool VerifyHash;
            public int SpeedLimit;
            public bool Sync;
            public Guid Session;
        }
        private PauseState _pauseState;
        private bool _paused;                     // paused right now (resume button armed)
        private volatile bool _pauseRequested;    // pause clicked, waiting for the send to wind down

        public MainWindow()
        {
            UiChrome.ApplyDark(this, mica: true);

            // Fit the default window to the screen (small laptops get a smaller but usable window)
            double waW = SystemParameters.WorkArea.Width;
            double waH = SystemParameters.WorkArea.Height;
            Width = Math.Min(1080, waW - 24);
            Height = Math.Min(840, waH - 24);
            MinWidth = Math.Min(900, Width);
            MinHeight = Math.Min(720, Height);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Load config first — control initialization reads settings that must
            // survive restarts (VerifyHash, SpeedLimit, KnownDevices)
            Config.Load();
            Updater.DeleteStaleBackup(App.ExePath);
            InitializeComponent();
            SetupTrayIcon();
            SetupSectionIcons();
            UpdateThemeButton();
            WireFieldValidation();
            LoadKnownDevices();

            Application.Current.SessionEnding += (s, e) => { _trayExit = true; };
            // The HWND exists from SourceInitialized on — that's where the
            // WM_SETTINGCHANGE hook for live theme tracking can attach
            SourceInitialized += (s, e) => HookSystemThemeChanges();
            AllowDrop = true;
            DragEnter += MainWindow_DragEnter;
            Drop += MainWindow_DragDrop;
            StateChanged += (s, e) =>
            {
                // Minimize-to-tray: hide instead of occupying the taskbar
                if (WindowState == WindowState.Minimized && !_trayExit) Hide();
            };

            _cmbLang.Items.Add("English");
            _cmbLang.Items.Add("中文");

            _numHttpPort.Value = Math.Max(1, Math.Min(65535, Config.GetInt("HttpSharePort", HttpShareServer.DefaultPort)));
            _numHttpPort.ValueChanged += (s, e) => Config.SetInt("HttpSharePort", _numHttpPort.Value);
            _numSpeed.Value = Math.Max(0, Math.Min(1048576, Config.GetInt("SpeedLimit", 0)));
            _numSpeed.ValueChanged += (s, e) => Config.SetInt("SpeedLimit", _numSpeed.Value);
            _chkVerifyHash.IsChecked = Config.GetBool("VerifyHash", false);
            _chkVerifyHash.Checked += (s, e) => Config.SetBool("VerifyHash", true);
            _chkVerifyHash.Unchecked += (s, e) => Config.SetBool("VerifyHash", false);
            _chkEncrypt.IsChecked = Config.GetBool("Encrypt", true);
            _chkEncrypt.Checked += (s, e) => Config.SetBool("Encrypt", true);
            _chkEncrypt.Unchecked += (s, e) => Config.SetBool("Encrypt", false);
            _chkCompress.IsChecked = Config.GetBool("Compress", true);
            _chkCompress.Checked += (s, e) => Config.SetBool("Compress", true);
            _chkCompress.Unchecked += (s, e) => Config.SetBool("Compress", false);
            _chkAutoRetry.IsChecked = Config.GetBool("AutoRetry", true);
            _chkAutoRetry.Checked += (s, e) => Config.SetBool("AutoRetry", true);
            _chkAutoRetry.Unchecked += (s, e) => Config.SetBool("AutoRetry", false);

            PopulateBindAddresses();
            ApplyLanguage();
            ApplyConfig();
            AddLog(L.StartedVersion(AppVersion));
            LogRenderEnvironment();
            ScheduleStartupUpdateCheck();
        }

        /// <summary>Rendering environment info goes to the daily log FILE only (not
        /// the console) — it's the surviving evidence when a machine black-screens,
        /// but noise on screen.</summary>
        private void LogRenderEnvironment()
        {
            try
            {
                int tier = System.Windows.Media.RenderCapability.Tier >> 16;
                string build = (Microsoft.Win32.Registry.GetValue(
                    @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "CurrentBuildNumber", "?") ?? "?").ToString();
                string render = Config.GetBool("SoftwareRender", false) ? "software"
                    : (UiChrome.MicaActive ? "GPU + Mica" : "GPU");
                AppendLogFile(L.EnvInfo(tier, build, render));
            }
            catch { }
        }


        /// <summary>Decorates the five section titles with Segoe icon glyphs; hidden
        /// entirely on systems without an icon font (Win7 shows plain titles).</summary>
        private void SetupSectionIcons()
        {
            if (!IconFont.Available) return;
            SetIcon("_icoServer", '');
            SetIcon("_icoClient", '');
            SetIcon("_icoProgressS", '');
            SetIcon("_icoProgressC", '');
            SetIcon("_icoLog", '');
        }

        private void SetIcon(string name, char glyph)
        {
            if (FindName(name) is TextBlock tb)
            {
                tb.Text = glyph.ToString();
                tb.Visibility = Visibility.Visible;
            }
        }

        // ==================== Field validation ====================
        //
        // Live format validation for the text fields: a failing field gets a red
        // border (TxtInput template reacts to Tag="invalid") plus a ToolTip. Empty
        // fields stay neutral — the existing click-time checks remain the hard gate.

        private static readonly char[] InvalidPathChars = Path.GetInvalidPathChars();

        private void WireFieldValidation()
        {
            foreach (var box in new[] { _txtPortS, _txtPortC })
            {
                box.PreviewTextInput += BlockNonDigits;
                box.TextChanged += (s, e) => ValidatePortBox((TextBox)s);
            }
            _txtServerIp.PreviewTextInput += BlockIpChars;
            _txtServerIp.TextChanged += (s, e) => ValidateIpLive(_txtServerIp);
            _txtServerIp.LostFocus += (s, e) => ValidateIpStrict(_txtServerIp);
            _txtPairing.PreviewTextInput += BlockNonDigits;
            _txtPairing.TextChanged += (s, e) => ValidateDigitsOnly(_txtPairing);
            _txtSaveDir.TextChanged += (s, e) => ValidatePathChars(_txtSaveDir);
            _txtFile.TextChanged += (s, e) => ValidatePathChars(_txtFile);
        }

        private static void BlockNonDigits(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c));
        }

        private static void BlockIpChars(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c) && c != '.');
        }

        /// <summary>Flags (or clears) the red invalid state on a field.</summary>
        private static void SetFieldError(TextBox box, bool invalid, string message)
        {
            box.Tag = invalid ? "invalid" : null;
            box.ToolTip = invalid ? message : null;
        }

        private static void ValidatePortBox(TextBox box)
        {
            string t = box.Text.Trim();
            bool ok = t.Length == 0 || (int.TryParse(t, out int v) && v >= 1 && v <= 65535);
            SetFieldError(box, !ok, ok ? null : L.InvalidPort);
        }

        private static void ValidateIpLive(TextBox box)
        {
            string t = box.Text.Trim();
            if (t.Length == 0)
            {
                SetFieldError(box, false, null);
                return;
            }
            if (t.Any(c => !char.IsDigit(c) && c != '.'))
            {
                SetFieldError(box, true, L.FieldIpInvalid);
                return;
            }
            // While typing, only judge complete-looking addresses (3+ dots)
            bool ok = t.Count(c => c == '.') < 3 || IPAddress.TryParse(t, out IPAddress probe);
            SetFieldError(box, !ok, ok ? null : L.FieldIpInvalid);
        }

        private static bool IsValidIpv4(string t)
        {
            return IPAddress.TryParse(t, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        private static void ValidateIpStrict(TextBox box)
        {
            string t = box.Text.Trim();
            if (t.Length == 0) return;
            SetFieldError(box, !IsValidIpv4(t), L.FieldIpInvalid);
        }

        private static void ValidateDigitsOnly(TextBox box)
        {
            bool ok = box.Text.All(char.IsDigit);
            SetFieldError(box, !ok, ok ? null : L.FieldDigitsOnly);
        }

        private static void ValidatePathChars(TextBox box)
        {
            bool ok = box.Text.IndexOfAny(InvalidPathChars) < 0;
            SetFieldError(box, !ok, ok ? null : L.FieldPathInvalid);
        }

        /// <summary>True when the client-panel address is a usable IPv4 (shows the red
        /// field state as a side effect). Empty stays neutral here — callers that
        /// require a value keep their own empty checks.</summary>
        private bool ClientIpIsValid()
        {
            string t = _txtServerIp.Text.Trim();
            if (t.Length == 0) return true;
            bool ok = IsValidIpv4(t);
            SetFieldError(_txtServerIp, !ok, ok ? null : L.FieldIpInvalid);
            return ok;
        }

        // ==================== Help ====================

        private void BtnHelp_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HelpDialog { Owner = this };
            dlg.ShowDialog();
        }

        // ==================== Theme toggle ====================

        private void BtnTheme_Click(object sender, RoutedEventArgs e)
        {
            ThemeManager.Toggle();
            // Cycle feedback in the log — the icon alone can't convey "auto"
            AddLog(ThemeManager.Mode == "auto" ? L.ThemeModeAuto
                : ThemeManager.Mode == "dark" ? L.ThemeModeDark : L.ThemeModeLight);
            UpdateThemeButton();
        }

        /// <summary>Sun glyph in dark mode (switch to light), moon in light mode;
        /// text fallback on systems without an icon font. The tooltip names the mode
        /// because the icon only shows the effective (resolved) theme.</summary>
        private void UpdateThemeButton()
        {
            if (IconFont.Available)
            {
                _btnTheme.Content = (ThemeManager.IsDark ? '\uE706' : '\uE708').ToString();
                _btnTheme.FontFamily = (FontFamily)FindResource("Font.Icon");
                _btnTheme.FontSize = 14;
            }
            else
            {
                _btnTheme.Content = L.ThemeBtn(ThemeManager.IsDark);
            }
            _btnTheme.ToolTip = ThemeManager.Mode == "auto" ? L.ThemeModeAuto
                : ThemeManager.Mode == "dark" ? L.ThemeModeDark : L.ThemeModeLight;
        }

        /// <summary>Live system-theme tracking: Windows broadcasts WM_SETTINGCHANGE
        /// ("ImmersiveColorSet") when the personalization flips — re-resolve auto mode.</summary>
        private void HookSystemThemeChanges()
        {
            var source = System.Windows.Interop.HwndSource.FromHwnd(
                new System.Windows.Interop.WindowInteropHelper(this).Handle);
            if (source == null) return;
            source.AddHook(delegate (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                const int WM_SETTINGCHANGE = 0x001A;
                if (msg == WM_SETTINGCHANGE)
                {
                    try
                    {
                        string section = Marshal.PtrToStringUni(lParam);
                        if (section == "ImmersiveColorSet")
                        {
                            ThemeManager.SystemThemeChanged();
                            UpdateThemeButton();
                        }
                    }
                    catch { }
                }
                return IntPtr.Zero;
            });
        }

        /// <summary>Marshals the action onto the UI thread; silently dropped once the
        /// window is gone or the dispatcher is shutting down.</summary>
        private void RunOnUi(Action a)
        {
            var d = Dispatcher;
            if (d == null || d.HasShutdownStarted || _windowClosed) return;
            try
            {
                if (d.CheckAccess()) a();
                else d.BeginInvoke(a);
            }
            catch { }
        }

        /// <summary>Synchronous UI-thread call that returns a value (monitor-mode card creation).</summary>
        private T RunOnUiSync<T>(Func<T> f)
        {
            var d = Dispatcher;
            if (d == null || d.HasShutdownStarted || _windowClosed) return default(T);
            try
            {
                if (d.CheckAccess()) return f();
                return d.Invoke(f);
            }
            catch
            {
                return default(T);
            }
        }

        // ==================== Tray icon ====================

        private void SetupTrayIcon()
        {
            _notifyIcon = new WinForms.NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = L.AppTitle
            };
            _notifyIcon.DoubleClick += (s, e) => RestoreWindow();
            _trayMenu = new WinForms.ContextMenuStrip();
            _trayMenu.Items.Add(L.TrayShow, null, (s, e) => RestoreWindow());
            var autoStartItem = new WinForms.ToolStripMenuItem(L.TrayAutoStart)
            {
                CheckOnClick = true,
                Checked = AutoStart.IsEnabled()
            };
            autoStartItem.Click += (s, e) =>
            {
                AutoStart.Set(autoStartItem.Checked, App.ExePath);
                AddLog(autoStartItem.Checked ? L.AutoStartOn : L.AutoStartOff);
            };
            _trayMenu.Items.Add(autoStartItem);
            _trayMenu.Items.Add(L.UpdBtn, null, (s, e) => ShowUpdateDialog());
            _trayMenu.Items.Add(L.About, null, (s, e) =>
            {
                MessageBox.Show(this, L.AboutText(AppVersion), L.About,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            });
            _trayMenu.Items.Add(L.TrayExit, null, (s, e) =>
            {
                _trayExit = true;
                Close();
            });
            _notifyIcon.ContextMenuStrip = _trayMenu;
        }

        private void RestoreWindow()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
        }

        /// <summary>Shows a tray notification (configurable) with optional sound.</summary>
        private void Notify(string title, string text)
        {
            if (!Config.GetBool("NotifyEnabled", true)) return;
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.ShowBalloonTip(3000);
                if (Config.GetBool("NotifySound", true))
                    System.Media.SystemSounds.Asterisk.Play();
            }
            catch { }
        }

        // ==================== Language & config ====================

        private void CmbLang_SelectedIndexChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_cmbLang.SelectedIndex < 0) return;
            L.IsChinese = _cmbLang.SelectedIndex == 1;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Title = L.AppTitle;
            _notifyIcon.Text = L.AppTitle;
            _lblHeader.Text = L.AppTitle;

            _hdrServer.Text = L.ServerSettings;
            _lblBind.Text = L.BindAddress;
            _lblPortS.Text = L.Port;
            _lblSaveDir.Text = L.SaveTo;
            _btnBrowseDir.Content = L.Browse;
            _btnStartServer.Content = L.StartServer;
            _btnStopServer.Content = L.StopServer;
            _btnOpenDir.Content = L.OpenSaveDir;
            _btnRecent.Content = L.RecentFiles;

            _hdrClient.Text = L.ClientSettings;
            _lblServerIp.Text = L.ServerIP;
            _lblPortC.Text = L.Port;
            _lblFile.Text = _chkMonitor.IsChecked == true ? L.MonitorLabel
                : (_chkFolder.IsChecked == true ? L.FolderLabel : L.FileLabel);
            _btnBrowseFile.Content = L.Browse;
            _btnSend.Content = _chkMonitor.IsChecked == true ? L.StartMonitor
                : (_chkFolder.IsChecked == true ? L.SendFolder : L.SendFile);
            _btnCancel.Content = L.CancelBtn;
            _chkFolder.Content = L.FolderMode;
            _chkSync.Content = L.SyncModeLabel;
            _btnResumeList.Content = L.ResumeBtn;
            _chkVerifyHash.Content = L.VerifyHashLabel;
            _lblSpeed.Text = L.SpeedLimitLabel;
            _btnQueue.Content = L.QueueBtn;
            _btnScan.Content = L.ScanBtn;
            _chkMonitor.Content = L.MonitorMode;
            _lblConcurrency.Text = L.ConcurrencyLabel;
            _lblSrcPort.Text = L.SrcPortLabel;

            _hdrProgressS.Text = L.ServerProgress;
            _hdrProgressC.Text = L.ClientProgress;

            _hdrLog.Text = L.LogGroup;
            _btnExportLog.Content = L.ExportLog;
            _btnCheckUpdate.Content = L.UpdBtn;
            _btnHelp.Content = L.HelpBtn;
            UpdateThemeButton();
            _chkPairing.Content = L.PairingLabel;
            _btnSendText.Content = L.SendTextBtn;
            _btnFanOut.Content = L.FanOutBtn;
            _lblPairingC.Text = L.PairingClientLabel;
            _lblHttpPort.Text = L.HttpPortLabel;
            if (_httpShare == null || !_httpShare.IsRunning)
                _btnHttpShare.Content = L.HttpShareBtn;
            else
                _btnHttpShare.Content = L.HttpShareStop;
            _btnShareQr.Content = L.ShareQrBtn;
            _btnRecvOptions.Content = L.RecvOptionsBtn;
            _btnStats.Content = L.StatsBtn;
            _btnHistory.Content = L.HistoryBtn;
            _btnDevices.Content = L.DevicesBtn;
            _btnPause.Content = _paused ? L.ResumeText : L.PauseBtn;
            _chkEncrypt.Content = L.EncryptLabel;
            _chkCompress.Content = L.CompressLabel;
            _chkAutoRetry.Content = L.AutoRetryLabel;

            PopulateBindAddresses();
        }

        private void ApplyConfig()
        {
            // Language
            _cmbLang.SelectedIndex = Config.Get("Language", "English") == "中文" ? 1 : 0;

            // Protocol
            _chkServerTcp.IsChecked = Config.GetBool("ServerTCP", true);
            _chkServerUdt.IsChecked = Config.GetBool("ServerUDT", false);
            string clientProto = Config.Get("ClientProtocol", "TCP");
            _rbClientTcp.IsChecked = clientProto != "UDT";
            _rbClientUdt.IsChecked = clientProto == "UDT";

            // Server
            _txtPortS.Text = Config.Get("ServerPort", "8080");
            _txtSaveDir.Text = Config.Get("SaveDir", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            string bind = Config.Get("ServerBind", "");
            if (!string.IsNullOrWhiteSpace(bind))
            {
                for (int i = 0; i < _cmbBind.Items.Count; i++)
                {
                    if ((_cmbBind.Items[i] as string) == bind) { _cmbBind.SelectedIndex = i; break; }
                }
            }

            // Client
            _txtServerIp.Text = Config.Get("ClientIP", "127.0.0.1");
            _txtPortC.Text = Config.Get("ClientPort", "8080");
            _txtFile.Text = Config.Get("LastPath", "");
            _chkFolder.IsChecked = Config.GetBool("FolderMode", false);
            _chkSync.IsChecked = Config.GetBool("SyncMode", false);
            _chkSync.IsEnabled = _chkFolder.IsChecked == true && !Config.GetBool("MonitorMode", false);
            _chkMonitor.IsChecked = Config.GetBool("MonitorMode", false);
            _numConcurrency.Value = Math.Max(1, Math.Min(8, Config.GetInt("Concurrency", 4)));
            _numSrcPort.Value = Math.Max(0, Math.Min(65535, Config.GetInt("SrcPort", 0)));
            _txtPairing.Text = Config.Get("PairingCode", "");
        }

        private void SaveConfig()
        {
            Config.Set("Language", _cmbLang.SelectedIndex == 1 ? "中文" : "English");
            Config.SetBool("ServerTCP", _chkServerTcp.IsChecked == true);
            Config.SetBool("ServerUDT", _chkServerUdt.IsChecked == true);
            Config.Set("ClientProtocol", _rbClientUdt.IsChecked == true ? "UDT" : "TCP");
            Config.Set("ServerPort", _txtPortS.Text.Trim());
            Config.Set("SaveDir", _txtSaveDir.Text.Trim());
            Config.Set("ServerBind", _cmbBind.SelectedItem as string ?? "");
            Config.Set("ClientIP", _txtServerIp.Text.Trim());
            Config.Set("ClientPort", _txtPortC.Text.Trim());
            Config.Set("LastPath", _txtFile.Text.Trim());
            Config.SetBool("FolderMode", _chkFolder.IsChecked == true);
            Config.SetBool("SyncMode", _chkSync.IsChecked == true);
            Config.SetBool("MonitorMode", _chkMonitor.IsChecked == true);
            Config.SetBool("VerifyHash", _chkVerifyHash.IsChecked == true);
            Config.SetInt("Concurrency", _numConcurrency.Value);
            Config.SetInt("SrcPort", _numSrcPort.Value);
            Config.Set("PairingCode", _txtPairing.Text.Trim());
            Config.Save();
        }

        private void PopulateBindAddresses()
        {
            string allText = L.BindAll;
            int previousSelection = _cmbBind.SelectedIndex;

            _cmbBind.Items.Clear();
            _cmbBind.Items.Add(allText);

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up)
                        continue;

                    foreach (var addr in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (addr.Address.AddressFamily == AddressFamily.InterNetwork)
                        {
                            string ip = addr.Address.ToString();
                            if (!_cmbBind.Items.Contains(ip))
                                _cmbBind.Items.Add(ip);
                        }
                    }
                }
            }
            catch { }

            if (_cmbBind.Items.Count == 1)
            {
                _cmbBind.Items.Add("127.0.0.1");
            }

            if (previousSelection >= 0 && previousSelection < _cmbBind.Items.Count)
                _cmbBind.SelectedIndex = previousSelection;
            else
                _cmbBind.SelectedIndex = 0;
        }

        // ==================== Auto update ====================

        private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            ShowUpdateDialog();
        }

        private void ShowUpdateDialog()
        {
            var dlg = new UpdateDialog { Owner = this };
            dlg.ShowDialog();
        }

        /// <summary>Background startup check (URL configured + AutoUpdateCheck enabled).
        /// Failures are silent — only a confirmed newer version opens the update dialog.</summary>
        private void ScheduleStartupUpdateCheck()
        {
            string url = Config.Get("UpdateUrl", DefaultUpdateUrl);
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!Config.GetBool("AutoUpdateCheck", true)) return;
            Task.Run(async delegate
            {
                try
                {
                    await Task.Delay(3000).ConfigureAwait(false);
                    UpdateManifest m = await Updater.CheckAnyAsync(url, 10000).ConfigureAwait(false);
                    if (m.IsNewerThan(Updater.CurrentVersion))
                    {
                        // Respect "skip this version" from the update dialog
                        Version skipped;
                        if (System.Version.TryParse(Config.Get("SkippedVersion", ""), out skipped)
                            && m.Version <= skipped)
                            return;
                        RunOnUi(delegate
                        {
                            AddLog(L.UpdAvailable(AppVersion, m.Version.ToString()));
                            ShowUpdateDialog();
                        });
                    }
                }
                catch { }
            });
        }

        /// <summary>Called by UpdateDialog after a verified download: swap in the new exe
        /// and restart. Invoked on the UI thread.</summary>
        internal void ApplyUpdateAndRestart(string stagedPath)
        {
            ApplyUpdateAndRestart(stagedPath, null);
        }

        internal void ApplyUpdateAndRestart(string stagedPath, string expectedSha256Hex)
        {
            try
            {
                // Re-verify the staged binary right before the swap (TOCTOU defence)
                Updater.Apply(stagedPath, App.ExePath, expectedSha256Hex);
            }
            catch (Exception ex)
            {
                AddLog(L.UpdApplyFailed(ex.Message));
                MessageBox.Show(this, L.UpdApplyFailed(ex.Message), L.UpdTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            AddLog(L.UpdRestarting);
            SaveConfig();
            _trayExit = true;
            try { System.Diagnostics.Process.Start(App.ExePath); }
            catch { /* new exe is in place; user can start it manually */ }
            Close();
        }

        // ==================== Server panel ====================

        private void BtnBrowseDir_Click(object sender, RoutedEventArgs e)
        {
            using (var dlg = new WinForms.FolderBrowserDialog())
            {
                dlg.Description = L.BrowseDirDesc;
                if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                    _txtSaveDir.Text = dlg.SelectedPath;
            }
        }

        private void ChkFolder_CheckedChanged(object sender, RoutedEventArgs e)
        {
            if (_chkMonitor.IsChecked == true) return; // Monitor mode overrides folder mode
            bool isFolder = _chkFolder.IsChecked == true;
            _lblFile.Text = isFolder ? L.FolderLabel : L.FileLabel;
            _btnSend.Content = isFolder ? L.SendFolder : L.SendFile;
            _txtFile.Text = "";
            // Sync builds on folder mode (0x04 diff transfer)
            _chkSync.IsEnabled = isFolder;
        }

        private void ChkMonitor_CheckedChanged(object sender, RoutedEventArgs e)
        {
            bool isMonitor = _chkMonitor.IsChecked == true;
            _lblFile.Text = isMonitor ? L.MonitorLabel
                : (_chkFolder.IsChecked == true ? L.FolderLabel : L.FileLabel);
            _btnSend.Content = isMonitor ? L.StartMonitor
                : (_chkFolder.IsChecked == true ? L.SendFolder : L.SendFile);
            _chkFolder.IsEnabled = !isMonitor;
            _chkSync.IsEnabled = !isMonitor && _chkFolder.IsChecked == true;
            if (isMonitor) _chkSync.IsChecked = false;
            _txtFile.Text = "";
        }

        private void ChkPairing_CheckedChanged(object sender, RoutedEventArgs e)
        {
            // Show a fresh code immediately; Start regenerates one per server session
            if (_chkPairing.IsChecked == true)
                _lblPairingCode.Text = WireAuth.GeneratePairingCode();
            _lblPairingCode.Visibility = _chkPairing.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---- HTTP share (browser download) ----

        private void BtnHttpShare_Click(object sender, RoutedEventArgs e)
        {
            if (_httpShare != null && _httpShare.IsRunning)
            {
                _httpShare.Stop();
                _httpShare = null;
                CloseShareQr();
                AddLog(L.HttpShareOff);
                _btnHttpShare.Content = L.HttpShareBtn;
                _btnShareQr.IsEnabled = false;
                return;
            }

            string dir = _txtSaveDir.Text.Trim();
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(this, L.HttpShareDirMissing, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            int port = _numHttpPort.Value;
            Config.SetInt("HttpSharePort", port);
            var share = new HttpShareServer();
            share.OnLog += msg => RunOnUi(() => AddLog(msg));
            try
            {
                // Pairing enabled -> reuse the pairing code as the share access code
                string token = _chkPairing.IsChecked == true ? _lblPairingCode.Text : null;
                share.Start(dir, port, token);
                _httpShare = share;
                _btnHttpShare.Content = L.HttpShareStop;
                _btnShareQr.IsEnabled = true;
                AddLog(L.HttpShareOn(share.LanUrl));
                ShowShareQr();
            }
            catch (Exception ex)
            {
                AddLog(L.HttpShareStartFailed(ex.Message));
                MessageBox.Show(this, L.HttpShareStartFailed(ex.Message),
                    L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnShareQr_Click(object sender, RoutedEventArgs e)
        {
            ShowShareQr();
        }

        /// <summary>Shows the share QR dialog, or just brings the existing one to the
        /// front — lets the user reopen a closed QR without restarting the share.</summary>
        private void ShowShareQr()
        {
            if (_httpShare == null || !_httpShare.IsRunning) return;
            if (_qrDialog != null && _qrDialog.IsLoaded)
            {
                _qrDialog.Activate();
                return;
            }
            _qrDialog = new QrDialog(HttpShareServer.LanAddresses(), _httpShare.Port) { Owner = this };
            _qrDialog.Show();
        }

        private void CloseShareQr()
        {
            if (_qrDialog == null) return;
            try { _qrDialog.Close(); } catch { }
            _qrDialog = null;
        }

        private void BtnOpenDir_Click(object sender, RoutedEventArgs e)
        {
            string dir = _txtSaveDir.Text.Trim();
            if (Directory.Exists(dir))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", dir); } catch { }
            }
            else
            {
                MessageBox.Show(this, L.DirNotExist, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRecent_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new RecentFilesDialog(_recentFiles.ToArray()) { Owner = this };
            dlg.ShowDialog();
        }

        // ==================== Drag & drop ====================

        private void MainWindow_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void MainWindow_DragDrop(object sender, DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            if (files.Length == 1)
            {
                string path = files[0];
                if (Directory.Exists(path))
                {
                    _chkFolder.IsChecked = true;
                    _txtFile.Text = path;
                }
                else if (File.Exists(path))
                {
                    _chkFolder.IsChecked = false;
                    _txtFile.Text = path;
                }
                else
                {
                    AddLog(L.DragDropInvalid(path));
                }
                return;
            }

            // Multiple items: enqueue every valid file/folder and open the send queue
            var tasks = new List<QueuedTask>();
            int skipped = 0;
            for (int i = 0; i < files.Length; i++)
            {
                string path = files[i];
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path))
                {
                    AddLog(L.DragDropInvalid(path));
                    continue;
                }
                var t = CaptureQueuedTaskFor(path, isDir);
                if (t != null) tasks.Add(t);
                else skipped++;
            }
            if (skipped > 0)
                AddLog(L.DragDropSkipped(skipped));
            if (tasks.Count == 0) return;
            AddLog(L.DragDropQueued(tasks.Count));
            var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask, tasks, CaptureQueuedTaskFor) { Owner = this };
            dlg.ShowDialog();
        }

        // ==================== Resume list ====================

        private void BtnResumeList_Click(object sender, RoutedEventArgs e)
        {
            var states = ResumeState.ListAll();
            var validStates = new List<ResumeState>();
            if (states != null)
            {
                foreach (var s in states) { if (s != null) validStates.Add(s); }
            }
            var folderStates = FolderResumeState.ListAll();
            if (validStates.Count == 0 && folderStates.Count == 0)
            {
                MessageBox.Show(this, L.ResumeListEmpty, L.ResumeListTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var dlg = new ResumeDialog(validStates.ToArray(), folderStates.ToArray()) { Owner = this };
            bool ok = dlg.ShowDialog() == true;
            if (ok && dlg.SelectedFolderState != null)
            {
                var f = dlg.SelectedFolderState;
                _txtServerIp.Text = f.ServerIp;
                _txtPortC.Text = f.Port.ToString();
                // Checked first — the CheckedChanged handler clears the path box
                _chkFolder.IsChecked = true;
                _txtFile.Text = f.FolderPath;
                _numConcurrency.Value = 1;
                _pendingResumeSession = f.SessionId;
                AddLog(L.ResumeQueued);
                if (f.IsUdt)
                {
                    _rbClientTcp.IsChecked = false;
                    _rbClientUdt.IsChecked = true;
                }
                else
                {
                    _rbClientTcp.IsChecked = true;
                    _rbClientUdt.IsChecked = false;
                }
            }
            else if (ok && dlg.SelectedState != null)
            {
                var s = dlg.SelectedState;
                _txtServerIp.Text = s.ServerIp;
                _txtPortC.Text = s.Port.ToString();
                // Checked first — the CheckedChanged handler clears the path box
                _chkFolder.IsChecked = false;
                _txtFile.Text = s.FilePath;
                _numConcurrency.Value = 1;
                _pendingResumeSession = s.SessionId;
                AddLog(L.ResumeQueued);
                if (s.IsUdt)
                {
                    _rbClientTcp.IsChecked = false;
                    _rbClientUdt.IsChecked = true;
                }
                else
                {
                    _rbClientTcp.IsChecked = true;
                    _rbClientUdt.IsChecked = false;
                }
            }
        }

        // ==================== Send queue ====================

        private void BtnQueue_Click(object sender, RoutedEventArgs e)
        {
            if (_chkMonitor.IsChecked == true || _monitorCts != null) return;
            var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask, null, CaptureQueuedTaskFor) { Owner = this };
            dlg.ShowDialog();
        }

        private QueuedTask CaptureQueuedTask()
        {
            string path = _txtFile.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) return null;
            return CaptureQueuedTaskFor(path, _chkFolder.IsChecked == true);
        }

        private QueuedTask CaptureQueuedTaskFor(string path, bool isFolder)
        {
            int port;
            if (!int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
                return null;
            if (string.IsNullOrWhiteSpace(_txtServerIp.Text.Trim()))
                return null;
            if (!IsValidIpv4(_txtServerIp.Text.Trim()))
                return null;
            return new QueuedTask
            {
                FilePath = path,
                IsFolder = isFolder,
                ServerIp = _txtServerIp.Text.Trim(),
                Port = port,
                IsUdp = _rbClientUdt.IsChecked == true,
                SrcPort = _numSrcPort.Value,
                Concurrency = _numConcurrency.Value,
                VerifyHash = _chkVerifyHash.IsChecked == true,
                SpeedLimit = _numSpeed.Value * 1024
            };
        }

        private async Task<bool> ExecuteQueuedTask(QueuedTask t)
        {
            return await StartTransfer(t.FilePath, t.IsFolder, t.ServerIp, t.Port, !t.IsUdp,
                t.SrcPort, t.Concurrency, t.VerifyHash, t.SpeedLimit, null);
        }

        // ==================== Device discovery ====================

        private void BtnScan_Click(object sender, RoutedEventArgs e)
        {
            if (_chkMonitor.IsChecked == true || _monitorCts != null) return;
            var dlg = new DiscoveryDialog(UseDiscoveredDevice, _knownDevices) { Owner = this };
            dlg.ShowDialog();
        }

        private void UseDiscoveredDevice(DeviceInfo d)
        {
            _txtServerIp.Text = d.Ip;
            _txtPortC.Text = d.Port.ToString();
            if (d.SupportsTcp && !d.SupportsUdt)
            {
                _rbClientTcp.IsChecked = true;
                _rbClientUdt.IsChecked = false;
            }
            else if (d.SupportsUdt && !d.SupportsTcp)
            {
                _rbClientTcp.IsChecked = false;
                _rbClientUdt.IsChecked = true;
            }
            if (d.RequiresPairing)
            {
                AddLog(L.UsePairingHint);
                _txtPairing.Focus();
            }
        }

        // ==================== Known devices (device memory) ====================

        private void LoadKnownDevices()
        {
            _knownDevices.Clear();
            string raw = Config.Get("KnownDevices", "");
            if (string.IsNullOrEmpty(raw)) return;
            string[] parts = raw.Split(';');
            for (int i = 0; i < parts.Length; i++)
            {
                string[] fields = parts[i].Split('|');
                int port, flags;
                if (fields.Length == 4 && int.TryParse(fields[2], out port) && int.TryParse(fields[3], out flags))
                {
                    _knownDevices.Add(new DeviceInfo
                    {
                        Name = fields[0],
                        Ip = fields[1],
                        Port = port,
                        SupportsTcp = (flags & 1) != 0,
                        SupportsUdt = (flags & 2) != 0
                    });
                }
            }
        }

        private void SaveKnownDevices()
        {
            var sb = new StringBuilder();
            for (int i = 0; i < _knownDevices.Count; i++)
            {
                if (i > 0) sb.Append(';');
                var d = _knownDevices[i];
                sb.Append(d.Name.Replace('|', '_').Replace(';', '_'));
                sb.Append('|');
                sb.Append(d.Ip);
                sb.Append('|');
                sb.Append(d.Port.ToString());
                sb.Append('|');
                sb.Append((d.SupportsTcp ? 1 : 0) | (d.SupportsUdt ? 2 : 0));
            }
            Config.Set("KnownDevices", sb.ToString());
        }

        /// <summary>Remembers a successfully connected device so it can be re-selected without a rescan.</summary>
        private void RememberDevice(string ip, int port, bool isUdp)
        {
            for (int i = 0; i < _knownDevices.Count; i++)
            {
                var d = _knownDevices[i];
                if (d.Ip == ip && d.Port == port)
                {
                    if (isUdp) d.SupportsUdt = true;
                    else d.SupportsTcp = true;
                    if (d.Name == "?" || string.IsNullOrEmpty(d.Name)) d.Name = ip;
                    _knownDevices[i] = d;
                    SaveKnownDevices();
                    return;
                }
            }
            _knownDevices.Add(new DeviceInfo
            {
                Name = ip,
                Ip = ip,
                Port = port,
                SupportsTcp = !isUdp,
                SupportsUdt = isUdp
            });
            if (_knownDevices.Count > 10) _knownDevices.RemoveAt(0);
            SaveKnownDevices();
        }

        private void OnFileReceived(string path, long size)
        {
            if (!string.IsNullOrEmpty(path))
            {
                _recentFiles.Add(path + "|" + size.ToString());
                if (_recentFiles.Count > 100)
                    _recentFiles.RemoveAt(0);
            }
            Notify(L.NotifyReceiveDone, Path.GetFileName(path));
        }

        /// <summary>Applies the optional date-archive layout to the save directory.</summary>
        private static string GetArchiveDir(string saveDir)
        {
            if (!Config.GetBool("AutoArchive", false)) return saveDir;
            return Path.Combine(saveDir, DateTime.Now.ToString("yyyy-MM-dd"));
        }

        // ==================== Text messages (0x06) ====================

        /// <summary>Shows a received text message: log, balloon tip, and a non-modal viewer.</summary>
        private void OnTextReceived(string text)
        {
            string preview = ServerWire.Preview(text);
            AddLog(L.S_TextReceived(preview));
            Notify(L.NotifyTextTitle, preview);
            if (_textRecvDialog == null || !_textRecvDialog.IsLoaded)
                _textRecvDialog = new TextReceivedDialog(text);
            else
                _textRecvDialog.AppendMessage(text);
            _textRecvDialog.Owner = this;
            _textRecvDialog.Show();
            _textRecvDialog.Activate();
        }

        private async void BtnSendText_Click(object sender, RoutedEventArgs e)
        {
            string text;
            var dlg = new TextSendDialog { Owner = this };
            if (dlg.ShowDialog() != true) return;
            text = dlg.MessageText;
            if (string.IsNullOrEmpty(text)) return;

            string ip = _txtServerIp.Text.Trim();
            int port;
            if (ip.Length == 0 || !int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535
                || !ClientIpIsValid())
            {
                MessageBox.Show(this, ip.Length == 0 || ClientIpIsValid() ? L.InvalidPort : L.FieldIpInvalid,
                    L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            bool isUdt = _rbClientUdt.IsChecked == true;
            _btnSendText.IsEnabled = false;
            try
            {
                bool ok = await SendTextMessageAsync(text, ip, port, isUdt);
                if (ok)
                {
                    AddLog(L.SendTextDone);
                    RememberDevice(ip, port, isUdt);
                }
            }
            finally
            {
                _btnSendText.IsEnabled = true;
            }
        }

        /// <summary>One-shot text transfer; returns false when the send failed
        /// (the error is already logged via the client's OnLog/OnError events).</summary>
        private async Task<bool> SendTextMessageAsync(string text, string ip, int port, bool isUdt)
        {
            var done = new TaskCompletionSource<bool>();
            Exception error = null;
            string pairing = _txtPairing.Text.Trim();

            if (isUdt)
            {
                var client = new TransferUdtClient(ip, port, "", 0, 4194304, 0);
                client.PairingCode = pairing;
                client.OnLog += msg => RunOnUi(() => AddLog(msg));
                client.OnError += msg => { error = new Exception(msg); };
                client.OnStopped += () => done.TrySetResult(error == null);
                try { await client.SendTextAsync(text).ConfigureAwait(true); }
                catch { /* failures propagate like TCP after OnError fired; OnStopped settles the task */ }
            }
            else
            {
                var client = new TransferClient(ip, port, "", 0, 4194304, 0);
                client.PairingCode = pairing;
                client.OnLog += msg => RunOnUi(() => AddLog(msg));
                client.OnError += msg => { error = new Exception(msg); };
                client.OnStopped += () => done.TrySetResult(error == null);
                try { await client.SendTextAsync(text).ConfigureAwait(true); }
                catch { /* RunTransfer rethrows after raising OnError; OnStopped settles the task */ }
            }
            return await done.Task.ConfigureAwait(true);
        }

        // ==================== Server start / stop ====================

        private void BtnStartServer_Click(object sender, RoutedEventArgs e)
        {
            int port;
            if (!int.TryParse(_txtPortS.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, L.InvalidPort, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string saveDir = _txtSaveDir.Text.Trim();
            if (!Directory.Exists(saveDir))
            {
                MessageBox.Show(this, L.DirNotExist, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // The combo stores display strings ("0.0.0.0 (所有接口)") — strip to the bare
            // address: TCP's TryParse would silently fall back to Any, but UDT's
            // IPAddress.Parse throws on the decoration
            string bindAddr = (_cmbBind.SelectedItem as string ?? "0.0.0.0").Split(' ')[0].Trim();
            if (string.IsNullOrWhiteSpace(bindAddr))
                bindAddr = "0.0.0.0";

            if (_chkServerTcp.IsChecked != true && _chkServerUdt.IsChecked != true)
            {
                MessageBox.Show(this, L.NoProtocolSelected, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Port availability pre-check: offer the next free port when busy
            bool needTcp = _chkServerTcp.IsChecked == true, needUdp = _chkServerUdt.IsChecked == true;
            if (!Utils.IsPortFree(port, needTcp, needUdp))
            {
                int alt = Utils.FindFreePortFrom(port + 1, needTcp, needUdp);
                if (alt == 0)
                {
                    MessageBox.Show(this, L.PortBusyNoAlt(port), L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (MessageBox.Show(this, L.PortBusyOffer(port, alt), L.DlgError,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                {
                    port = alt;
                    _txtPortS.Text = alt.ToString();
                }
                else
                {
                    return;
                }
            }

            DisableServerInputs();
            _serverCount = 0;

            // A fresh pairing code per server session keeps old codes from lingering
            string pairingCode = null;
            if (_chkPairing.IsChecked == true)
            {
                pairingCode = WireAuth.GeneratePairingCode();
                _lblPairingCode.Text = pairingCode;
            }

            if (_chkServerTcp.IsChecked == true)
            {
                bool tcpStarted = false;
                var tcpServer = new TransferServer(bindAddr, port, GetArchiveDir(saveDir));
                tcpServer.PairingCode = pairingCode;
                ApplyServerOptions(tcpServer);
                tcpServer.OnLog += msg => RunOnUi(() => AddLog(msg));
                tcpServer.OnError += msg => RunOnUi(() => _lblStatusS.Text = L.ErrorPrefix + msg);
                tcpServer.OnFileReceived += (path, size) => RunOnUi(() => OnFileReceived(path, size));
                tcpServer.OnTextReceived += t => RunOnUi(() => OnTextReceived(t));
                tcpServer.OnClientProgress += (ep, p) => RunOnUi(() =>
                {
                    var card = GetOrCreateTcpCard(ep);
                    UpdateCardProgress(card, p);
                });
                tcpServer.OnClientTransferComplete += ep => RunOnUi(() =>
                {
                    Border card;
                    if (_tcpCards.TryGetValue(ep, out card)) { UpdateCardComplete(card); _tcpCards.Remove(ep); }
                });
                tcpServer.OnTransferComplete += () => RunOnUi(() =>
                {
                    foreach (var c in _tcpCards.Values) UpdateCardComplete(c);
                    _tcpCards.Clear();
                    _lblStatusS.Text = L.Listening;
                });
                tcpServer.OnStarted += () => RunOnUi(() =>
                {
                    tcpStarted = true;
                    _serverCount++;
                    OnServerStarted();
                });
                tcpServer.OnStopped += () => RunOnUi(() =>
                {
                    if (!tcpStarted) return; // start failed, ignore
                    foreach (var c in _tcpCards.Values) UpdateCardComplete(c);
                    _tcpCards.Clear();
                    _server = null;
                    OnServerStopped();
                });
                _server = tcpServer;
                tcpServer.Start();
            }

            if (_chkServerUdt.IsChecked == true)
            {
                bool udtStarted = false;
                var udtServer = new TransferUdtServer(bindAddr, port, GetArchiveDir(saveDir));
                udtServer.PairingCode = pairingCode;
                ApplyServerOptionsUdt(udtServer);
                udtServer.OnLog += msg => RunOnUi(() => AddLog(msg));
                udtServer.OnError += msg => RunOnUi(() => _lblStatusS.Text = L.ErrorPrefix + msg);
                udtServer.OnFileReceived += (path, size) => RunOnUi(() => OnFileReceived(path, size));
                udtServer.OnTextReceived += t => RunOnUi(() => OnTextReceived(t));
                udtServer.OnClientProgress += (ep, p) => RunOnUi(() =>
                {
                    var card = GetOrCreateUdtCard(ep);
                    UpdateCardProgress(card, p);
                });
                udtServer.OnClientTransferComplete += ep => RunOnUi(() =>
                {
                    Border card;
                    if (_udtCards.TryGetValue(ep, out card)) { UpdateCardComplete(card); _udtCards.Remove(ep); }
                });
                udtServer.OnTransferComplete += () => RunOnUi(() =>
                {
                    foreach (var c in _udtCards.Values) UpdateCardComplete(c);
                    _udtCards.Clear();
                    _lblStatusS.Text = L.Listening;
                });
                udtServer.OnStarted += () => RunOnUi(() =>
                {
                    udtStarted = true;
                    _serverCount++;
                    OnServerStarted();
                });
                udtServer.OnStopped += () => RunOnUi(() =>
                {
                    if (!udtStarted) return;
                    foreach (var c in _udtCards.Values) UpdateCardComplete(c);
                    _udtCards.Clear();
                    _serverUdt = null;
                    OnServerStopped();
                });
                _serverUdt = udtServer;
                udtServer.Start();
            }

            if (_serverCount == 0)
            {
                // Both protocols failed to start
                EnableServerInputs();
                MessageBox.Show(this, L.ServerStartFailed, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                // Advertise the server over UDP so LAN clients can discover it
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                if (_discoveryServer == null) _discoveryServer = new DiscoveryServer(dPort);
                _discoveryServer.Start(Environment.MachineName, port,
                    _chkServerTcp.IsChecked == true, _chkServerUdt.IsChecked == true, pairingCode != null);
            }
        }

        private void DisableServerInputs()
        {
            _chkServerTcp.IsEnabled = false;
            _chkServerUdt.IsEnabled = false;
            _chkPairing.IsEnabled = false;
            _cmbLang.IsEnabled = false;
            _cmbBind.IsEnabled = false;
            _txtPortS.IsEnabled = false;
            _txtSaveDir.IsEnabled = false;
            _btnBrowseDir.IsEnabled = false;
        }

        private void EnableServerInputs()
        {
            _btnStartServer.IsEnabled = true;
            _btnStopServer.IsEnabled = false;
            _chkServerTcp.IsEnabled = true;
            _chkServerUdt.IsEnabled = true;
            _chkPairing.IsEnabled = true;
            _cmbLang.IsEnabled = true;
            _cmbBind.IsEnabled = true;
            _txtPortS.IsEnabled = true;
            _txtSaveDir.IsEnabled = true;
            _btnBrowseDir.IsEnabled = true;
        }

        private void OnServerStarted()
        {
            _btnStartServer.IsEnabled = false;
            _btnStopServer.IsEnabled = true;
            _lblStatusS.Text = L.Listening;

            // One-time firewall guidance — local probes cannot detect external blocks,
            // so hand the user the diagnosis and the exact allow command up front
            if (!Config.GetBool("FirewallHintShown", false))
            {
                Config.SetBool("FirewallHintShown", true);
                Config.Save();
                string cmd = "netsh advfirewall firewall add rule name=\"TrFileTransfer\" dir=in action=allow program=\"" +
                    App.ExePath + "\" enable=yes";
                var dlg = new TextReceivedDialog(L.FwHintText(cmd), L.FwHintTitle) { Owner = this };
                dlg.Show();
            }
        }

        private void OnServerStopped()
        {
            if (_serverCount > 0) _serverCount--;
            if (_serverCount > 0) return; // still have other servers running
            EnableServerInputs();
            _lblStatusS.Text = L.ServerStopped;
        }

        // ==================== Receive policy (options dialog, IP filter, confirm) ====================

        /// <summary>Reads the receive options from Config into a TCP server. Directory
        /// layout options (per-device, dedup) are snapshot here, so they apply from the
        /// next server start; the IP filter and confirmation read Config live per event.</summary>
        private void ApplyServerOptions(TransferServer server)
        {
            server.OnSessionStats += stats => RecordReceived(stats);
            server.PerDeviceFolder = Config.GetBool("PerDeviceFolder", false);
            server.SkipDuplicateFiles = Config.Get("DuplicateFiles", "rename") == "skip";
            server.ResolveDeviceName = ResolveDeviceNameForIp;
            server.IpAllowed = IpFilterPass;
            server.ConfirmRequest = AskReceiveConfirmation;
            server.ReceiveSpeedLimit = (long)Config.GetInt("RecvSpeedLimit", 0) * 1024;
        }

        private void ApplyServerOptionsUdt(TransferUdtServer server)
        {
            server.OnSessionStats += stats => RecordReceived(stats);
            server.PerDeviceFolder = Config.GetBool("PerDeviceFolder", false);
            server.SkipDuplicateFiles = Config.Get("DuplicateFiles", "rename") == "skip";
            server.ResolveDeviceName = ResolveDeviceNameForIp;
            server.IpAllowed = IpFilterPass;
            server.ConfirmRequest = AskReceiveConfirmation;
            server.ReceiveSpeedLimit = (long)Config.GetInt("RecvSpeedLimit", 0) * 1024;
        }

        /// <summary>IP filter decision — evaluated per connection from Config.</summary>
        private bool IpFilterPass(string ip)
        {
            string mode = Config.Get("IpFilterMode", "off");
            if (mode == "off" || string.IsNullOrEmpty(ip)) return true;
            bool listed = IpFilter.Matches(Config.Get("IpFilterList", ""), ip);
            return mode == "allow" ? listed : !listed;
        }

        /// <summary>Friendly per-device folder name: the known device's name when we
        /// have it (sanitized by the wire layer), otherwise the raw IP.</summary>
        private string ResolveDeviceNameForIp(string ip)
        {
            for (int i = 0; i < _knownDevices.Count; i++)
            {
                if (_knownDevices[i].Ip == ip && !string.IsNullOrEmpty(_knownDevices[i].Name)
                    && _knownDevices[i].Name != "?")
                    return _knownDevices[i].Name;
            }
            return ip;
        }

        /// <summary>Receive confirmation gate (runs on a server thread). Off / known-device
        /// exemption short-circuit to true; otherwise a prompt shows for 30 s — no answer
        /// counts as a refusal so the sender is never left hanging.</summary>
        private Task<bool> AskReceiveConfirmation(string ip, string name, long size, int files, bool isFolder)
        {
            string mode = Config.Get("ConfirmReceive", "off");
            if (mode == "off") return Task.FromResult(true);
            if (mode == "unknown" && ip.Length > 0)
            {
                for (int i = 0; i < _knownDevices.Count; i++)
                {
                    if (_knownDevices[i].Ip == ip) return Task.FromResult(true);
                }
            }

            DateTime granted;
            lock (_confirmedReceives)
            {
                if (_confirmedReceives.TryGetValue(ip, out granted)
                    && DateTime.Now - granted < TimeSpan.FromMinutes(10))
                {
                    return Task.FromResult(true);
                }
            }

            var tcs = new TaskCompletionSource<bool>();
            RunOnUi(delegate
            {
                try
                {
                    var dlg = new ConfirmReceiveDialog(ip, name, size, files, isFolder) { Owner = this };
                    dlg.Closed += (s, e) => tcs.TrySetResult(dlg.Accepted);
                    dlg.Show();
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            });
            // Auto-deny timeout — runs regardless of dialog outcome
            Task.Delay(TimeSpan.FromSeconds(30)).ContinueWith(t => tcs.TrySetResult(false));

            return tcs.Task.ContinueWith(t =>
            {
                if (t.Result)
                {
                    lock (_confirmedReceives) { _confirmedReceives[ip] = DateTime.Now; }
                }
                return t.Result;
            });
        }

        private void RecordReceived(WireSessionStats stats)
        {
            _stats.Append(new StatsEntry
            {
                When = DateTime.Now,
                Direction = 'R',
                Peer = stats.Peer,
                Bytes = stats.Bytes,
                Files = stats.Files,
                Seconds = stats.Watch.Elapsed.TotalSeconds,
                Detail = stats.Detail,
                Path = stats.Path
            });
        }

        private void RecordSent(string ip, long bytes, int files, double seconds, string detail, string path)
        {
            if (bytes <= 0) return;
            _stats.Append(new StatsEntry
            {
                When = DateTime.Now,
                Direction = 'S',
                Peer = ip,
                Bytes = bytes,
                Files = files,
                Seconds = seconds,
                Detail = detail,
                Path = path
            });
        }

        // ==================== Receive options / stats buttons ====================

        private void BtnRecvOptions_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new ReceiveOptionsDialog { Owner = this };
            dlg.ShowDialog();
        }

        private void BtnStats_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new StatsDialog(_stats) { Owner = this };
            dlg.ShowDialog();
        }

        private void BtnHistory_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new HistoryDialog(_stats) { Owner = this };
            dlg.ShowDialog();
        }

        private void BtnDevices_Click(object sender, RoutedEventArgs e)
        {
            // The dialog edits _knownDevices in place; persist whatever remains
            var dlg = new DevicesDialog(_knownDevices, UseDiscoveredDevice) { Owner = this };
            dlg.ShowDialog();
            SaveKnownDevices();
        }

        private void BtnStopServer_Click(object sender, RoutedEventArgs e)
        {
            _serverCount = 0; // reset before stopping so OnStopped handlers see zero
            if (_discoveryServer != null) { _discoveryServer.Stop(); _discoveryServer = null; }
            if (_server != null) { _server.Stop(); _server = null; }
            if (_serverUdt != null) { _serverUdt.Stop(); _serverUdt = null; }
        }

        // ==================== Client send ====================

        private async void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            string path = _txtFile.Text.Trim();
            bool isFolder = _chkFolder.IsChecked == true;
            bool isMonitor = _chkMonitor.IsChecked == true;

            // Validate IP and port (shared)
            int port;
            if (!int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, L.InvalidPort, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            string ip = _txtServerIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show(this, L.EnterServerIP, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!ClientIpIsValid())
            {
                MessageBox.Show(this, L.FieldIpInvalid, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Monitor mode branch
            if (isMonitor)
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(this, L.MonitorDirNotExist, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                StartMonitoring(path, ip, port);
                return;
            }

            // Normal send branch — delegate to the shared transfer runner
            int concurrency = _numConcurrency.Value;
            bool isTcp = _rbClientTcp.IsChecked == true;
            int srcPort = _numSrcPort.Value;

            // Resume is only valid for single-connection sends, and only when the
            // selected file/folder still matches the one recorded in the resume state.
            Guid? resumeSession = null;
            if (concurrency == 1 && _pendingResumeSession.HasValue)
            {
                if (isFolder)
                {
                    var fs = FolderResumeState.Load(_pendingResumeSession.Value);
                    if (fs != null && string.Equals(fs.FolderPath, path, StringComparison.OrdinalIgnoreCase))
                        resumeSession = _pendingResumeSession;
                    else
                        _pendingResumeSession = null;
                }
                else
                {
                    var st = ResumeState.Load(_pendingResumeSession.Value);
                    if (st != null && string.Equals(st.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        resumeSession = _pendingResumeSession;
                    else
                        _pendingResumeSession = null;
                }
            }

            await StartTransfer(path, isFolder, ip, port, isTcp, srcPort, concurrency,
                _chkVerifyHash.IsChecked == true, _numSpeed.Value * 1024, resumeSession);
        }

        /// <summary>
        /// Runs a single send (single or concurrent, TCP or UDT). Disables client inputs
        /// while running and restores them afterwards. Returns false on failure — the
        /// error is already surfaced via OnError event handlers. Single-connection sends
        /// arm the pause button: pausing cancels the client and keeps a parameter snapshot
        /// (plus the 0x03/0x04 session id) so the resume click continues the checkpoint.
        /// </summary>
        private async Task<bool> StartTransfer(string path, bool isFolder, string ip, int port,
            bool isTcp, int srcPort, int concurrency, bool verifyHash, int speedLimit, Guid? resumeSession)
        {
            try
            {
                if (isFolder)
                {
                    if (!Directory.Exists(path))
                    {
                        MessageBox.Show(this, L.DirNotExist, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                        return false;
                    }
                }
                else if (!File.Exists(path))
                {
                    MessageBox.Show(this, L.FileNotFound, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                    return false;
                }

                // A fresh send invalidates any leftover pause snapshot
                _pauseState = null;
                _paused = false;
                _pauseRequested = false;
                _btnPause.IsEnabled = false;
                _btnPause.Content = L.PauseBtn;

                DisableClientInputs();

                bool syncMode = isFolder && _chkSync.IsChecked == true;
                Guid pauseSession = resumeSession.HasValue ? resumeSession.Value
                    : (syncMode ? FolderResumeState.DeriveSyncSession(path, ip, port, !isTcp)
                                : Guid.NewGuid());
                bool pausable = concurrency == 1; // chunked sends have no session to resume
                if (pausable)
                {
                    _pauseState = new PauseState
                    {
                        Path = path,
                        IsFolder = isFolder,
                        Ip = ip,
                        Port = port,
                        IsTcp = isTcp,
                        SrcPort = srcPort,
                        Concurrency = concurrency,
                        VerifyHash = verifyHash,
                        SpeedLimit = speedLimit,
                        Sync = syncMode,
                        Session = pauseSession
                    };
                    _btnPause.IsEnabled = true;
                }

                var sendWatch = System.Diagnostics.Stopwatch.StartNew();

                if (!isFolder && concurrency > 1)
                {
                    // Multi-concurrent transfer
                    var concurrent = new ConcurrentTransfer(ip, port, path, concurrency, isTcp, srcPort, speedLimit);
                    concurrent.PairingCode = _txtPairing.Text.Trim();
                    WireConcurrentEvents(concurrent);
                    _concurrent = concurrent; // so BtnCancel_Click can stop it
                    try { await concurrent.SendAsync(); }
                    finally { _concurrent = null; }
                }
                else if (isTcp)
                {
                    _client = ClientFactory.CreateTcp(ip, port, path, srcPort, speedLimit, _txtPairing.Text.Trim());
                    WireClientEvents(_client);
                    if (isFolder && resumeSession.HasValue)
                    {
                        await _client.SendFolderResumableAsync(resumeSession.Value);
                        _pendingResumeSession = null;
                    }
                    else if (isFolder && syncMode)
                    {
                        // Sync mode: stable session per (folder, target) — the server-side
                        // 0x04 scan skips unchanged files, so only differences travel
                        AddLog(L.C_SyncStart(path));
                        await _client.SendFolderResumableAsync(pauseSession, keepState: true);
                    }
                    else if (isFolder)
                    {
                        await SendPlainWithRetryAsync(true, path, pauseSession,
                            () => _client.SendFolderAsync(path),
                            s => _client.SendFolderResumableAsync(s));
                    }
                    else if (resumeSession.HasValue)
                    {
                        await _client.SendResumableAsync(resumeSession.Value, verifyHash);
                        _pendingResumeSession = null;
                    }
                    else
                    {
                        await SendPlainWithRetryAsync(false, path, pauseSession,
                            () => _client.SendAsync(),
                            s => _client.SendResumableAsync(s, verifyHash));
                    }
                }
                else
                {
                    _clientUdt = ClientFactory.CreateUdt(ip, port, path, srcPort, speedLimit, _txtPairing.Text.Trim());
                    WireUdtClientEvents(_clientUdt);
                    if (isFolder && resumeSession.HasValue)
                    {
                        await _clientUdt.SendFolderResumableAsync(resumeSession.Value);
                        _pendingResumeSession = null;
                    }
                    else if (isFolder && syncMode)
                    {
                        AddLog(L.C_SyncStart(path));
                        await _clientUdt.SendFolderResumableAsync(pauseSession, keepState: true);
                    }
                    else if (isFolder)
                    {
                        await SendPlainWithRetryAsync(true, path, pauseSession,
                            () => _clientUdt.SendFolderAsync(path),
                            s => _clientUdt.SendFolderResumableAsync(s));
                    }
                    else if (resumeSession.HasValue)
                    {
                        await _clientUdt.SendResumableAsync(resumeSession.Value, verifyHash);
                        _pendingResumeSession = null;
                    }
                    else
                    {
                        await SendPlainWithRetryAsync(false, path, pauseSession,
                            () => _clientUdt.SendAsync(),
                            s => _clientUdt.SendResumableAsync(s, verifyHash));
                    }
                }

                // Cancellation does not throw — the clients swallow the OperationCanceled —
                // so a user pause surfaces as a normal return flagged by WasCancelled
                if (_pauseRequested && WasActiveClientCancelled())
                {
                    FinalizePause();
                    return false;
                }
                RecordSent(ip, MeasurePathBytes(path, isFolder), isFolder ? 0 : 1,
                    sendWatch.Elapsed.TotalSeconds, PathDisplayName(path), path);
                RememberDevice(ip, port, !isTcp);
                return true;
            }
            catch (Exception ex)
            {
                if (_pauseRequested)
                {
                    // The cancel surfaced as a connection error instead of a clean stop
                    FinalizePause();
                    return false;
                }
                AddLog(L.ErrorPrefix + ex.Message);
                return false;
            }
            finally
            {
                try { RunOnUi(ResetClientUI); } catch { }
            }
        }

        private bool WasActiveClientCancelled()
        {
            return (_client != null && _client.WasCancelled)
                || (_clientUdt != null && _clientUdt.WasCancelled);
        }

        /// <summary>Locks in the paused state: keep the snapshot, arm the resume button.
        /// Runs on the UI thread (StartTransfer's continuations resume there).</summary>
        private void FinalizePause()
        {
            _pauseRequested = false;
            _paused = true;
            AddLog(L.C_Paused);
            _lblStatusC.Text = L.PausedStatus;
            _btnPause.IsEnabled = true;
            _btnPause.Content = L.ResumeText;
        }

        /// <summary>Drops the pause snapshot and resets the button (a different transfer
        /// mode is taking over; the interrupted session stays recoverable via the
        /// resume list, since the client/server states are already on disk).</summary>
        private void ClearPauseState()
        {
            _paused = false;
            _pauseState = null;
            _pauseRequested = false;
            _btnPause.IsEnabled = false;
            _btnPause.Content = L.PauseBtn;
        }

        /// <summary>Pause/Resume button. While sending: cancel the client and hold the
        /// snapshot. While paused: re-run the snapshot on its recorded session — the
        /// server-side checkpoint (and client resume state) continue from the break.</summary>
        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_paused && _pauseState != null)
            {
                var st = _pauseState;
                _pauseState = null;
                _paused = false;
                _btnPause.IsEnabled = false;
                _btnPause.Content = L.PauseBtn;
                _lblStatusC.Text = L.Ready;

                // Sync folders re-derive their stable session on the way through;
                // empty files cannot travel via 0x03, so resend them plainly
                Guid? session = st.Sync ? (Guid?)null : st.Session;
                if (!st.IsFolder && session.HasValue)
                {
                    try { if (new FileInfo(st.Path).Length == 0) session = null; }
                    catch { session = null; }
                }
                AddLog(L.C_TransferResumed);
                var _ = StartTransfer(st.Path, st.IsFolder, st.Ip, st.Port, st.IsTcp, st.SrcPort,
                    st.Concurrency, st.VerifyHash, st.SpeedLimit, session);
                return;
            }

            if (_pauseState == null || _pauseState.Concurrency > 1) return;
            if (_client == null && _clientUdt == null) return;

            _pauseRequested = true;
            _btnPause.IsEnabled = false;
            _lblStatusC.Text = L.PausingStatus;
            if (_client != null) _client.Cancel();
            if (_clientUdt != null) _clientUdt.Cancel();
        }

        private void WireClientEvents(TransferClient c)
        {
            var card = RunOnUiSync(() => CreateTransferCard(_progressPanelC));
            c.OnLog += msg => RunOnUi(() => AddLog(msg));
            c.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
            c.OnError += msg => RunOnUi(() =>
            {
                // A deliberate pause breaks the socket on purpose — not an error
                AddLog(_pauseRequested ? L.C_Paused : (L.ErrorPrefix + msg));
                ResetClientUI();
                UpdateCardComplete(card);
            });
            c.OnTransferComplete += () => RunOnUi(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            });
            c.OnStopped += () => RunOnUi(() => UpdateCardComplete(card));
        }

        /// <summary>
        /// Runs a plain send with optional transparent resume retries. With auto-resume
        /// on, single files travel via the 0x03 protocol from the first attempt (cheap,
        /// and a broken attempt just resumes); folders stay on plain 0x01 until the first
        /// failure (0x04 needs the full manifest hash pass), then resume in place via a
        /// stable session id. The session id comes from the caller so a user pause can
        /// re-run the same session later from the pause snapshot.
        /// </summary>
        private async Task SendPlainWithRetryAsync(bool isFolder, string path, Guid session,
            Func<Task> plainSend, Func<Guid, Task> resumeSend)
        {
            if (_chkAutoRetry.IsChecked != true)
            {
                await plainSend();
                return;
            }

            bool canResume;
            if (isFolder)
            {
                canResume = true;
            }
            else
            {
                try { canResume = new FileInfo(path).Length > 0; } // 0-byte files cannot use 0x03
                catch { canResume = false; }
            }
            if (!canResume)
            {
                await plainSend();
                return;
            }

            const int MaxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    if (attempt == 1 && isFolder)
                        await plainSend();
                    else
                        await resumeSend(session);
                    return;
                }
                catch (Exception)
                {
                    if (_pauseRequested) throw; // deliberate pause — never auto-retry
                    if (attempt >= MaxAttempts) throw;
                    AddLog(L.C_AutoRetry(attempt, MaxAttempts - 1));
                    DisableClientInputs(); // the client's error path re-enabled the panel
                    await Task.Delay(1200 * attempt);
                }
            }
        }

        /// <summary>Total bytes of the item just sent (metadata walk only).</summary>
        private static long MeasurePathBytes(string path, bool isFolder)
        {
            if (!isFolder)
            {
                try { return new FileInfo(path).Length; }
                catch { return 0; }
            }
            try
            {
                long total = 0;
                foreach (var f in Directory.GetFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { total += new FileInfo(f).Length; } catch { }
                }
                return total;
            }
            catch { return 0; }
        }

        /// <summary>File name, or the folder's own name for folder sends (stats detail).</summary>
        private static string PathDisplayName(string path)
        {
            try { return Path.GetFileName(path.TrimEnd('\\', '/')); }
            catch { return path; }
        }

        private void WireConcurrentEvents(ConcurrentTransfer c)
        {
            var card = RunOnUiSync(() => CreateTransferCard(_progressPanelC));
            c.OnLog += msg => RunOnUi(() => AddLog(msg));
            c.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
            c.OnError += msg => RunOnUi(() =>
            {
                AddLog(L.ErrorPrefix + msg);
                ResetClientUI();
                UpdateCardComplete(card);
            });
            c.OnTransferComplete += () => RunOnUi(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            });
        }

        private void WireUdtClientEvents(TransferUdtClient c)
        {
            var card = RunOnUiSync(() => CreateTransferCard(_progressPanelC));
            c.OnLog += msg => RunOnUi(() => AddLog(msg));
            c.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
            c.OnError += msg => RunOnUi(() =>
            {
                AddLog(_pauseRequested ? L.C_Paused : (L.ErrorPrefix + msg));
                ResetClientUI();
                UpdateCardComplete(card);
            });
            c.OnTransferComplete += () => RunOnUi(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            });
            c.OnStopped += () => RunOnUi(() => UpdateCardComplete(card));
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            if (_monitorCts != null)
            {
                StopMonitoring();
                return;
            }
            if (_fanOutRunning)
            {
                CancelFanOut();
                return;
            }
            if (_concurrent != null)
                _concurrent.Cancel();
            if (_client != null)
                _client.Cancel();
            if (_clientUdt != null)
                _clientUdt.Cancel();
            _btnCancel.IsEnabled = false;
            _lblStatusC.Text = L.Cancelling;
        }

        private void DisableClientInputs()
        {
            _btnSend.IsEnabled = false;
            _btnCancel.IsEnabled = true;
            _rbClientTcp.IsEnabled = false;
            _rbClientUdt.IsEnabled = false;
            _cmbLang.IsEnabled = false;
            _txtServerIp.IsEnabled = false;
            _txtPortC.IsEnabled = false;
            _txtFile.IsEnabled = false;
            _btnBrowseFile.IsEnabled = false;
            _chkFolder.IsEnabled = false;
            _chkMonitor.IsEnabled = false;
            _numConcurrency.IsEnabled = false;
            _numSrcPort.IsEnabled = false;
            _btnResumeList.IsEnabled = false;
            _chkVerifyHash.IsEnabled = false;
            _numSpeed.IsEnabled = false;
            _btnQueue.IsEnabled = false;
            _btnScan.IsEnabled = false;
            _btnFanOut.IsEnabled = false;
            _btnSendText.IsEnabled = false;
            _txtPairing.IsEnabled = false;
        }

        private void ResetClientUI()
        {
            // While paused the resume button stays armed and the status keeps saying so
            _lblStatusC.Text = _paused ? L.PausedStatus : L.Ready;
            _btnSend.IsEnabled = true;
            _btnCancel.IsEnabled = false;
            if (!_paused)
            {
                _btnPause.IsEnabled = false;
                _btnPause.Content = L.PauseBtn;
            }
            if (!_btnStopServer.IsEnabled)
            {
                _cmbLang.IsEnabled = true;
            }
            _rbClientTcp.IsEnabled = true;
            _rbClientUdt.IsEnabled = true;
            _txtServerIp.IsEnabled = true;
            _txtPortC.IsEnabled = true;
            _txtFile.IsEnabled = true;
            _btnBrowseFile.IsEnabled = true;
            _chkFolder.IsEnabled = true;
            _chkMonitor.IsEnabled = true;
            _numConcurrency.IsEnabled = true;
            _numSrcPort.IsEnabled = true;
            _btnResumeList.IsEnabled = true;
            _chkVerifyHash.IsEnabled = true;
            _numSpeed.IsEnabled = true;
            _btnQueue.IsEnabled = true;
            _btnScan.IsEnabled = true;
            _btnFanOut.IsEnabled = true;
            _btnSendText.IsEnabled = true;
            _txtPairing.IsEnabled = true;
        }

        // ==================== Fan-out: one file/folder to many devices in parallel ====================

        private void BtnFanOut_Click(object sender, RoutedEventArgs e)
        {
            if (_chkMonitor.IsChecked == true || _monitorCts != null) return;
            bool isFolder = _chkFolder.IsChecked == true;
            string path = _txtFile.Text.Trim();
            if (isFolder ? !Directory.Exists(path) : !File.Exists(path))
            {
                MessageBox.Show(this, isFolder ? L.DirNotExist : L.FileNotFound, L.DlgError,
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new FanOutDialog(_knownDevices) { Owner = this };
            if (dlg.ShowDialog() != true) return;
            if (dlg.SelectedDevices.Count == 0) return;
            StartFanOut(dlg.SelectedDevices, path, isFolder);
        }

        /// <summary>Sends the same item to every selected device concurrently, one
        /// progress card per target. Honors the panel's TCP/UDT selection, speed limit
        /// and pairing code; the source-port setting is ignored (parallel clients
        /// cannot share one local port).</summary>
        private void StartFanOut(List<DeviceInfo> targets, string path, bool isFolder)
        {
            bool isTcp = _rbClientTcp.IsChecked == true;
            int speedLimit = _numSpeed.Value * 1024;
            string pairing = _txtPairing.Text.Trim();
            if (_numSrcPort.Value != 0)
                AddLog(L.FanOutIgnoreSrcPort);

            ClearPauseState(); // fan-out replaces any paused send
            DisableClientInputs();
            _btnPause.IsEnabled = false; // fan-out has no single session to resume
            _btnCancel.IsEnabled = true;
            _lblStatusC.Text = L.FanOutStart(targets.Count);

            lock (_fanOutClients) _fanOutClients.Clear();
            _fanOutRunning = true;
            int total = targets.Count;
            int finished = 0;
            int okCount = 0;

            foreach (var t in targets)
            {
                var card = RunOnUiSync(() => CreateTransferCard(_progressPanelC));

                if (isTcp)
                {
                    var client = ClientFactory.CreateTcp(t.Ip, t.Port, path, 0, speedLimit, pairing);
                    client.OnLog += msg => RunOnUi(() => AddLog(msg));
                    client.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
                    client.OnError += msg => RunOnUi(() =>
                    {
                        AddLog(L.ErrorPrefix + msg);
                        UpdateCardComplete(card);
                    });
                    client.OnTransferComplete += () => RunOnUi(() =>
                    {
                        UpdateCardComplete(card);
                        Interlocked.Increment(ref okCount);
                        RecordSent(t.Ip, MeasurePathBytes(path, isFolder), isFolder ? 0 : 1, 0,
                            PathDisplayName(path), path);
                        RememberDevice(t.Ip, t.Port, false);
                    });
                    client.OnStopped += () =>
                    {
                        RunOnUi(() => UpdateCardComplete(card));
                        if (Interlocked.Increment(ref finished) == total)
                            RunOnUi(() => FinalizeFanOut(okCount, total));
                    };
                    lock (_fanOutClients) _fanOutClients.Add(client);
                    var _ = isFolder ? client.SendFolderAsync(path) : client.SendAsync();
                }
                else
                {
                    var client = ClientFactory.CreateUdt(t.Ip, t.Port, path, 0, speedLimit, pairing);
                    client.OnLog += msg => RunOnUi(() => AddLog(msg));
                    client.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
                    client.OnError += msg => RunOnUi(() =>
                    {
                        AddLog(L.ErrorPrefix + msg);
                        UpdateCardComplete(card);
                    });
                    client.OnTransferComplete += () => RunOnUi(() =>
                    {
                        UpdateCardComplete(card);
                        Interlocked.Increment(ref okCount);
                        RecordSent(t.Ip, MeasurePathBytes(path, isFolder), isFolder ? 0 : 1, 0,
                            PathDisplayName(path), path);
                        RememberDevice(t.Ip, t.Port, true);
                    });
                    client.OnStopped += () =>
                    {
                        RunOnUi(() => UpdateCardComplete(card));
                        if (Interlocked.Increment(ref finished) == total)
                            RunOnUi(() => FinalizeFanOut(okCount, total));
                    };
                    lock (_fanOutClients) _fanOutClients.Add(client);
                    var _ = isFolder ? client.SendFolderAsync(path) : client.SendAsync();
                }
            }
        }

        private void FinalizeFanOut(int okCount, int total)
        {
            _fanOutRunning = false;
            lock (_fanOutClients) _fanOutClients.Clear();
            AddLog(L.FanOutDone(okCount, total));
            if (!_trayExit) Notify(L.NotifySendDone, L.FanOutDone(okCount, total));
            try { ResetClientUI(); } catch { }
        }

        private void CancelFanOut()
        {
            lock (_fanOutClients)
            {
                foreach (var c in _fanOutClients)
                {
                    var tcp = c as TransferClient;
                    if (tcp != null) tcp.Cancel();
                    var udt = c as TransferUdtClient;
                    if (udt != null) udt.Cancel();
                }
            }
            _lblStatusC.Text = L.Cancelling;
        }

        // ==================== Progress cards ====================

        private static string FormatEta(TransferProgress p)
        {
            if (p.SpeedBytesPerSecond <= 0 || p.TotalBytes <= 0) return "--:--";
            var remaining = TimeSpan.FromSeconds((p.TotalBytes - p.BytesTransferred) / p.SpeedBytesPerSecond);
            if (remaining.Ticks <= 0) return "0:00";
            // TimeSpan's "mm" is the minute COMPONENT (0-59) — spell out hours
            // explicitly or a 2h05m ETA renders as "05:00"
            if (remaining.TotalHours >= 1) return string.Format("{0:h\\:mm\\:ss}", remaining);
            return string.Format("{0:mm\\:ss}", remaining);
        }

        private class ProgressCardInfo
        {
            public ProgressBar Bar;
            public TextBlock Label;
            public DateTime LastUpdate;
        }

        private Border GetOrCreateTcpCard(IPEndPoint ep)
        {
            Border card;
            if (!_tcpCards.TryGetValue(ep, out card))
            {
                card = CreateTransferCard(_progressPanelS);
                _tcpCards[ep] = card;
            }
            return card;
        }

        private Border GetOrCreateUdtCard(IPEndPoint ep)
        {
            Border card;
            if (!_udtCards.TryGetValue(ep, out card))
            {
                card = CreateTransferCard(_progressPanelS);
                _udtCards[ep] = card;
            }
            return card;
        }

        private Border CreateTransferCard(StackPanel parent)
        {
            // Two rows: progress bar on top, status text below — stacking them in
            // one cell made the text overlap the bar
            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var bar = new ProgressBar
            {
                Style = (Style)FindResource("BarModern"),
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                Margin = new Thickness(0, 0, 0, 5)
            };
            Grid.SetRow(bar, 0);
            grid.Children.Add(bar);
            var lbl = new TextBlock
            {
                FontSize = 11,
                Foreground = (Brush)FindResource("Brush.TextSecondary"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Text = ""
            };
            Grid.SetRow(lbl, 1);
            grid.Children.Add(lbl);
            var border = new Border
            {
                Background = (Brush)FindResource("Brush.ItemCard"),
                BorderBrush = (Brush)FindResource("Brush.ItemCardBorder"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(9, 7, 9, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = grid
            };
            border.Tag = new ProgressCardInfo { Bar = bar, Label = lbl };
            parent.Children.Add(border);
            // Fade the card in instead of popping it into existence
            border.Opacity = 0;
            border.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            return border;
        }

        private void UpdateCardProgress(Border card, TransferProgress p)
        {
            var info = card.Tag as ProgressCardInfo;
            if (info == null) return;
            int pct = p.TotalBytes > 0 ? (int)(p.BytesTransferred * 100 / p.TotalBytes) : 0;
            // Throttle repaints (complete look only every 100ms)
            if ((DateTime.Now - info.LastUpdate).TotalMilliseconds < 100 && pct < 100) return;
            info.LastUpdate = DateTime.Now;
            if (p.TotalBytes > 0)
            {
                double target = Math.Max(0, Math.Min(100, pct));
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(150));
                info.Bar.BeginAnimation(ProgressBar.ValueProperty, anim);
            }
            string speed = Utils.FormatSize((long)p.SpeedBytesPerSecond) + "/s";
            info.Label.Text = string.Format("{0} | {1} | {2}% | {3}/{4} | {5} {6}",
                p.FileName, speed, pct,
                Utils.FormatSize(p.BytesTransferred), Utils.FormatSize(p.TotalBytes),
                L.EtaShort, FormatEta(p));
        }

        private void UpdateCardComplete(Border card)
        {
            if (card.Tag == null) return; // already completed
            card.Tag = null;
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                var panel = card.Parent as Panel;
                if (panel != null) panel.Children.Remove(card);
            };
            timer.Start();
        }

        // ==================== Log ====================

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.ExportLogTitle,
                Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*",
                DefaultExt = "log",
                FileName = "TrFileTransfer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log"
            };
            if (dlg.ShowDialog(this) == true)
            {
                try
                {
                    var lines = new string[_lstLog.Items.Count];
                    for (int i = 0; i < _lstLog.Items.Count; i++)
                        lines[i] = _lstLog.Items[i].ToString();
                    File.WriteAllLines(dlg.FileName, lines, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, L.ExportLogFailed + ex.Message, L.DlgError,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void AddLog(string msg)
        {
            _lstLog.Items.Add(msg);
            _lstLog.ScrollIntoView(_lstLog.Items[_lstLog.Items.Count - 1]);
            while (_lstLog.Items.Count > 500)
                _lstLog.Items.RemoveAt(0);
            AppendLogFile(msg);
        }

        private static DateTime _lastLogSweep = DateTime.MinValue;

        /// <summary>Appends a log line to the daily log file under %AppData%\TrFileTransfer\logs.</summary>
        private static void AppendLogFile(string msg)
        {
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "TrFileTransfer", "logs");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
                File.AppendAllText(path, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine);
                if (DateTime.Now.Date != _lastLogSweep)
                {
                    _lastLogSweep = DateTime.Now.Date;
                    SweepOldLogs(dir);
                }
            }
            catch { }
        }

        /// <summary>Deletes daily log files older than 30 days (runs at most once per day).</summary>
        private static void SweepOldLogs(string dir)
        {
            try
            {
                DateTime cutoff = DateTime.Now.Date.AddDays(-30);
                string[] files = Directory.GetFiles(dir, "*.txt");
                for (int i = 0; i < files.Length; i++)
                {
                    try
                    {
                        if (File.GetLastWriteTime(files[i]) < cutoff)
                            File.Delete(files[i]);
                    }
                    catch { }
                }
            }
            catch { }
        }

        // ==================== Monitor mode ====================

        private void StartMonitoring(string folderPath, string ip, int port)
        {
            ClearPauseState(); // monitor mode replaces any paused send
            _monitorCts = new CancellationTokenSource();
            _monitorSrcPort = _numSrcPort.Value;
            _monitorSpeedBytesPerSec = _numSpeed.Value * 1024;
            _monitorIsTcp = _rbClientTcp.IsChecked == true;
            _monitorPairing = _txtPairing.Text.Trim();
            lock (_monitorLock) { _monitorQueue.Clear(); } // stale entries from a previous session

            DisableClientInputs();
            _lblStatusC.Text = L.MonitorWaiting;
            AddLog(L.MonitorStarted(folderPath));

            string sentDir = Path.Combine(folderPath, "已发送文件");
            Directory.CreateDirectory(sentDir);

            Task.Run(() => MonitorLoop(folderPath, ip, port, sentDir, _monitorCts.Token));
        }

        private void StopMonitoring()
        {
            // Clear the CTS: several UI guards test `_monitorCts != null` to mean
            // "monitoring is active", so leaving it set here would permanently disable
            // the queue/scan/fan-out buttons for the rest of the process lifetime.
            var cts = _monitorCts;
            _monitorCts = null;
            if (cts != null)
            {
                try { cts.Cancel(); } catch { }
                try { cts.Dispose(); } catch { }
            }
            lock (_monitorLock) { _monitorQueue.Clear(); }
            _btnCancel.IsEnabled = false;
            _lblStatusC.Text = L.MonitorStopped;
            AddLog(L.MonitorLogStopped);
            ResetClientUI();
        }

        private async Task MonitorLoop(string folderPath, string ip, int port, string sentDir, CancellationToken ct)
        {
            using (var watcher = new FileSystemWatcher(folderPath))
            {
                watcher.NotifyFilter = NotifyFilters.FileName;
                watcher.Created += (s, e) =>
                {
                    lock (_monitorLock) { _monitorQueue.Add(e.FullPath); }
                };
                // Events ON before the scan: a file created in between is caught by
                // the watcher instead of falling through the scan-then-enable gap
                watcher.EnableRaisingEvents = true;

                foreach (var file in Directory.GetFiles(folderPath))
                {
                    lock (_monitorLock)
                    {
                        if (!_monitorQueue.Contains(file)) _monitorQueue.Add(file);
                    }
                }

                while (!ct.IsCancellationRequested)
                {
                    string filePath = null;
                    lock (_monitorLock)
                    {
                        if (_monitorQueue.Count > 0)
                        {
                            filePath = _monitorQueue[0];
                            _monitorQueue.RemoveAt(0);
                        }
                    }

                    if (filePath != null)
                    {
                        await ProcessMonitoredFile(filePath, ip, port, sentDir, ct);
                    }
                    else
                    {
                        await Task.Delay(500, ct);
                    }
                }
            }
        }

        private async Task ProcessMonitoredFile(string filePath, string ip, int port, string sentDir, CancellationToken ct)
        {
            string fileName = Path.GetFileName(filePath);

            if (!await WaitForFileReady(filePath, ct))
            {
                RunOnUi(() => AddLog(L.MonitorFileNotReady(fileName)));
                // Requeue only what still exists — a vanished file (deleted, or a
                // duplicate of one already moved to the sent folder) would otherwise
                // bounce through the queue forever in a tight loop
                if (File.Exists(filePath))
                    lock (_monitorLock) { _monitorQueue.Add(filePath); }
                return;
            }

            bool success = false;
            try
            {
                var tcs = new TaskCompletionSource<bool>();

                var card = RunOnUiSync(() => CreateTransferCard(_progressPanelC));
                if (_monitorIsTcp)
                {
                    var client = ClientFactory.CreateTcp(ip, port, filePath, _monitorSrcPort, _monitorSpeedBytesPerSec, _monitorPairing);
                    client.OnLog += msg => RunOnUi(() => AddLog(msg));
                    client.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
                    client.OnError += msg => RunOnUi(() => AddLog(L.MonitorFileSendFailed(fileName, msg)));
                    client.OnTransferComplete += () => { tcs.TrySetResult(true); RunOnUi(() => UpdateCardComplete(card)); };
                    client.OnStopped += () => { tcs.TrySetResult(false); RunOnUi(() => UpdateCardComplete(card)); };
                    await client.SendAsync();
                }
                else
                {
                    var clientUdt = ClientFactory.CreateUdt(ip, port, filePath, _monitorSrcPort, _monitorSpeedBytesPerSec, _monitorPairing);
                    clientUdt.OnLog += msg => RunOnUi(() => AddLog(msg));
                    clientUdt.OnProgress += p => RunOnUi(() => UpdateCardProgress(card, p));
                    clientUdt.OnError += msg => RunOnUi(() => AddLog(L.MonitorFileSendFailed(fileName, msg)));
                    clientUdt.OnTransferComplete += () => { tcs.TrySetResult(true); RunOnUi(() => UpdateCardComplete(card)); };
                    clientUdt.OnStopped += () => { tcs.TrySetResult(false); RunOnUi(() => UpdateCardComplete(card)); };
                    await clientUdt.SendAsync();
                }

                success = await tcs.Task;
            }
            catch (Exception ex)
            {
                RunOnUi(() => AddLog(L.MonitorFileSendFailed(fileName, ex.Message)));
            }

            if (success)
            {
                long bytes = 0;
                try { bytes = new FileInfo(filePath).Length; } catch { }
                RecordSent(ip, bytes, 1, 0, Path.GetFileName(filePath), filePath);
                string destPath = Utils.GetUniqueSavePath(sentDir, fileName);
                try { File.Move(filePath, destPath); } catch { }
                RunOnUi(() =>
                {
                    AddLog(L.MonitorFileSent(fileName));
                    _lblStatusC.Text = L.MonitorWaiting;
                    RememberDevice(ip, port, !_monitorIsTcp);
                });
            }
        }

        private async Task<bool> WaitForFileReady(string filePath, CancellationToken ct)
        {
            int stableCount = 0;
            long lastSize = -1;
            int totalWaited = 0;
            const int PollIntervalMs = 500;
            const int StableThreshold = 2;
            const int RequeueAfterMs = 120000;
            const int LogIntervalMs = 30000;
            int lastLogAt = 0;
            var fileInfo = new FileInfo(filePath);

            while (!ct.IsCancellationRequested)
            {
                long currentSize;
                try
                {
                    fileInfo.Refresh();
                    if (!fileInfo.Exists)
                        return false;
                    currentSize = fileInfo.Length;
                }
                catch
                {
                    return false;
                }

                if (currentSize == lastSize)
                {
                    stableCount++;
                    if (stableCount >= StableThreshold)
                        return true;
                }
                else
                {
                    stableCount = 0;
                    lastSize = currentSize;
                }

                if (totalWaited >= RequeueAfterMs)
                    return false;

                if (totalWaited - lastLogAt >= LogIntervalMs)
                {
                    lastLogAt = totalWaited;
                    string fileName = Path.GetFileName(filePath);
                    RunOnUi(() => AddLog(L.MonitorFileWaiting(fileName, totalWaited / 1000)));
                }

                await Task.Delay(PollIntervalMs, ct);
                totalWaited += PollIntervalMs;
            }

            return false;
        }

        // ==================== File browse ====================

        private void BtnBrowseFile_Click(object sender, RoutedEventArgs e)
        {
            if (_chkFolder.IsChecked == true || _chkMonitor.IsChecked == true)
            {
                using (var dlg = new WinForms.FolderBrowserDialog())
                {
                    dlg.Description = _chkMonitor.IsChecked == true ? L.MonitorLabel : L.BrowseFolderDesc;
                    if (dlg.ShowDialog() == WinForms.DialogResult.OK)
                        _txtFile.Text = dlg.SelectedPath;
                }
            }
            else
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = L.BrowseFileTitle
                };
                if (dlg.ShowDialog(this) == true)
                    _txtFile.Text = dlg.FileName;
            }
        }

        // ==================== Window close ====================

        /// <summary>Stops any active transfer before closing the window.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // Closing the window hides to tray; use the tray menu's Exit to quit
            if (!_trayExit)
            {
                e.Cancel = true;
                SaveConfig();
                Hide();
                Notify(L.AppTitle, L.TrayMinimized);
                return;
            }
            SaveConfig();
            if (_monitorCts != null)
            {
                try { _monitorCts.Cancel(); } catch { }
            }
            if (_server != null)
                _server.Stop();
            if (_serverUdt != null)
                _serverUdt.Stop();
            if (_client != null)
                _client.Cancel();
            if (_clientUdt != null)
                _clientUdt.Cancel();
            // Remove the tray icon before teardown, otherwise it lingers until hover
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            if (_httpShare != null) { try { _httpShare.Stop(); } catch { } _httpShare = null; }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _windowClosed = true;
            base.OnClosed(e);
        }
    }
}
