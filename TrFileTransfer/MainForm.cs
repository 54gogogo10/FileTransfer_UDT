using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace TrFileTransfer
{
    /// <summary>Main application window — mode/protocol selector, server/client panels, progress, and log.</summary>
    public class MainForm : Form
    {
        // Language
        private ComboBox _cmbLang;
        private Label _lblHeader;
        private Button _btnCheckUpdate;

        // Progress split (server | client)
        private SplitContainer _splitProgress;

        // Protocol — server checkboxes, client radio buttons
        private CheckBox _chkServerTcp;
        private CheckBox _chkServerUdt;
        private RadioButton _rbClientTcp;
        private RadioButton _rbClientUdt;

        // Server controls
        private GroupBox _gbServer;
        private Label _lblBind;
        private ComboBox _cmbBind;
        private Label _lblPortS;
        private TextBox _txtPortS;
        private Label _lblSaveDir;
        private TextBox _txtSaveDir;
        private Button _btnBrowseDir;
        private Button _btnStartServer;
        private Button _btnStopServer;
        private CheckBox _chkPairing;
        private Label _lblPairingCode;

        // Client controls
        private GroupBox _gbClient;
        private Label _lblServerIp;
        private TextBox _txtServerIp;
        private Label _lblPortC;
        private TextBox _txtPortC;
        private Label _lblFile;
        private TextBox _txtFile;
        private Button _btnBrowseFile;
        private Button _btnSend;
        private Button _btnCancel;
        private CheckBox _chkFolder;
        private CheckBox _chkSync;
        private CheckBox _chkMonitor;
        private NumericUpDown _numConcurrency;
        private Label _lblConcurrency;
        private Label _lblSrcPort;
        private NumericUpDown _numSrcPort;
        private Button _btnResumeList;
        private CheckBox _chkVerifyHash;
        private Label _lblSpeed;
        private NumericUpDown _numSpeed;
        private Button _btnQueue;
        private Button _btnScan;
        private Button _btnFanOut;
        private Button _btnSendText;
        private Label _lblPairingC;
        private TextBox _txtPairing;
        private TextReceivedDialog _textRecvDialog;
        private int _monitorSpeedBytesPerSec;
        private DiscoveryServer _discoveryServer;
        private NotifyIcon _notifyIcon;
        private Button _btnOpenDir;
        private Button _btnRecent;
        private Button _btnHttpShare;
        private Label _lblHttpPort;
        private NumericUpDown _numHttpPort;
        private HttpShareServer _httpShare;
        private ContextMenuStrip _trayMenu;
        private bool _trayExit;
        private readonly System.Collections.Generic.List<string> _recentFiles
            = new System.Collections.Generic.List<string>();
        private readonly System.Collections.Generic.List<DeviceInfo> _knownDevices
            = new System.Collections.Generic.List<DeviceInfo>();
        private Guid? _pendingResumeSession;

        // Progress
        private GroupBox _gbProgressS;
        private FlowLayoutPanel _progressPanelS;
        private Label _lblStatusS;
        private GroupBox _gbProgressC;
        private FlowLayoutPanel _progressPanelC;
        private Label _lblStatusC;

        // Log
        private GroupBox _gbLog;
        private ListBox _lstLog;
        private Button _btnExportLog;

        // State
        private TransferServer _server;
        private TransferClient _client;
        private TransferUdtServer _serverUdt;
        private TransferUdtClient _clientUdt;
        private int _serverCount;
        private Dictionary<IPEndPoint, Panel> _tcpCards = new Dictionary<IPEndPoint, Panel>();
        private Dictionary<IPEndPoint, Panel> _udtCards = new Dictionary<IPEndPoint, Panel>();

        // Monitor mode
        private System.Threading.CancellationTokenSource _monitorCts;
        private int _monitorSrcPort;
        private System.Collections.Generic.List<string> _monitorQueue = new System.Collections.Generic.List<string>();
        private readonly object _monitorLock = new object();

        // Fan-out (one file/folder -> many devices in parallel)
        private readonly System.Collections.Generic.List<object> _fanOutClients
            = new System.Collections.Generic.List<object>();
        private volatile bool _fanOutRunning;

        /// <summary>Assembly version for display (major.minor.build).</summary>
        private static string AppVersion
        {
            get
            {
                Version v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                return v.Major + "." + v.Minor + "." + v.Build;
            }
        }

        /// <summary>Initializes the form, populates NIC list, and applies default language.</summary>
        public MainForm()
        {
            // Load config first — InitializeComponent already reads settings that must
            // survive restarts (VerifyHash, SpeedLimit, KnownDevices)
            Config.Load();
            Updater.DeleteStaleBackup(Application.ExecutablePath);
            InitializeComponent();
            PopulateBindAddresses();
            ApplyLanguage();
            ApplyConfig();
            AddLog(L.StartedVersion(AppVersion));
            ScheduleStartupUpdateCheck();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Splitter ratio once the real width is known (50/50)
            try { _splitProgress.SplitterDistance = _splitProgress.Width / 2; } catch { }
        }

        private void InitializeComponent()
        {
            Text = L.AppTitle;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = true;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            DoubleBuffered = true;
            AllowDrop = true;
            DragEnter += MainForm_DragEnter;
            DragDrop += MainForm_DragDrop;

            // Fit the default window to the screen (small laptops get a smaller but usable window)
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            ClientSize = new Size(Math.Min(720, wa.Width - 24), Math.Min(860, wa.Height - 24));
            MinimumSize = new Size(Math.Min(660, ClientSize.Width), Math.Min(720, ClientSize.Height));

            // Root layout: header / server / client are content-sized rows;
            // progress and log share the remaining space equally and grow on resize
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(12, 8, 12, 10) };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 132));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            // Header row: app title on the left, language selector and update button on the right
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 108));
            _lblHeader = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51)
            };
            _cmbLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 6, 0) };
            _cmbLang.Items.Add("English");
            _cmbLang.Items.Add("中文");
            _cmbLang.SelectedIndex = 0;
            _cmbLang.SelectedIndexChanged += CmbLang_SelectedIndexChanged;
            _btnCheckUpdate = new Button { Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 0) };
            UiStyle.Secondary(_btnCheckUpdate);
            _btnCheckUpdate.Click += BtnCheckUpdate_Click;
            header.Controls.Add(_lblHeader, 0, 0);
            header.Controls.Add(_cmbLang, 1, 0);
            header.Controls.Add(_btnCheckUpdate, 2, 0);
            root.Controls.Add(header, 0, 0);

            // ---- Server panel ----
            _gbServer = new GroupBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var tlpS = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 2, 6, 4) };
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 86));
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            tlpS.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            tlpS.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlpS.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            tlpS.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));

            _lblBind = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _cmbBind = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _lblPortS = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _txtPortS = new TextBox { Text = "8080", Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _chkServerTcp = new CheckBox { Text = "TCP", AutoSize = true, Checked = true, Margin = new Padding(6, 8, 3, 3) };
            _chkServerUdt = new CheckBox { Text = "UDT", AutoSize = true, Margin = new Padding(6, 8, 3, 3) };
            _lblSaveDir = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _txtSaveDir = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _btnBrowseDir = new Button { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
            UiStyle.Secondary(_btnBrowseDir);
            _btnBrowseDir.Click += BtnBrowseDir_Click;
            _btnStartServer = new Button { Width = 82, Height = 28, Margin = new Padding(0, 3, 6, 3) };
            UiStyle.Primary(_btnStartServer);
            _btnStartServer.Click += BtnStartServer_Click;
            _btnStopServer = new Button { Width = 82, Height = 28, Margin = new Padding(0, 3, 6, 3), Enabled = false };
            UiStyle.Secondary(_btnStopServer);
            _btnStopServer.Click += BtnStopServer_Click;
            _btnOpenDir = new Button { Width = 62, Height = 28, Margin = new Padding(0, 3, 6, 3) };
            UiStyle.Secondary(_btnOpenDir);
            _btnOpenDir.Click += BtnOpenDir_Click;
            _btnRecent = new Button { Width = 62, Height = 28, Margin = new Padding(0, 3, 6, 3) };
            UiStyle.Secondary(_btnRecent);
            _btnRecent.Click += BtnRecent_Click;
            _lblHttpPort = new Label { AutoSize = true, Margin = new Padding(10, 9, 3, 0), ForeColor = Color.FromArgb(68, 68, 68) };
            _numHttpPort = new NumericUpDown
            {
                Width = 56, Minimum = 1, Maximum = 65535,
                Value = Math.Max(1, Math.Min(65535, Config.GetInt("HttpSharePort", HttpShareServer.DefaultPort))),
                Margin = new Padding(0, 5, 6, 3)
            };
            _numHttpPort.ValueChanged += (s2, e2) => Config.SetInt("HttpSharePort", (int)_numHttpPort.Value);
            _btnHttpShare = new Button { Width = 62, Height = 28, Margin = new Padding(0, 3, 6, 3) };
            UiStyle.Secondary(_btnHttpShare);
            _btnHttpShare.Click += BtnHttpShare_Click;
            _chkPairing = new CheckBox { AutoSize = true, Margin = new Padding(16, 8, 3, 3) };
            _chkPairing.CheckedChanged += ChkPairing_CheckedChanged;
            _lblPairingCode = new Label
            {
                AutoSize = true,
                Margin = new Padding(6, 10, 3, 3),
                Font = new Font("Consolas", 10f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 120, 215),
                Visible = false
            };
            var serverButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            serverButtons.Controls.Add(_btnStartServer);
            serverButtons.Controls.Add(_btnStopServer);
            serverButtons.Controls.Add(_btnOpenDir);
            serverButtons.Controls.Add(_btnRecent);
            serverButtons.Controls.Add(_lblHttpPort);
            serverButtons.Controls.Add(_numHttpPort);
            serverButtons.Controls.Add(_btnHttpShare);
            serverButtons.Controls.Add(_chkPairing);
            serverButtons.Controls.Add(_lblPairingCode);
            tlpS.Controls.Add(_lblBind, 0, 0);
            tlpS.Controls.Add(_cmbBind, 1, 0);
            tlpS.Controls.Add(_lblPortS, 2, 0);
            tlpS.Controls.Add(_txtPortS, 3, 0);
            tlpS.Controls.Add(_chkServerTcp, 4, 0);
            tlpS.Controls.Add(_chkServerUdt, 5, 0);
            tlpS.Controls.Add(_lblSaveDir, 0, 1);
            tlpS.Controls.Add(_txtSaveDir, 1, 1);
            tlpS.SetColumnSpan(_txtSaveDir, 4);
            tlpS.Controls.Add(_btnBrowseDir, 5, 1);
            tlpS.Controls.Add(serverButtons, 1, 2);
            tlpS.SetColumnSpan(serverButtons, 5);
            _gbServer.Controls.Add(tlpS);
            root.Controls.Add(_gbServer, 0, 1);

            // ---- Client panel ----
            _gbClient = new GroupBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var tlpC = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 2, 6, 4) };
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            tlpC.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 104));
            for (int i = 0; i < 5; i++)
                tlpC.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));

            _lblServerIp = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _txtServerIp = new TextBox { Text = "127.0.0.1", Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _lblPortC = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _txtPortC = new TextBox { Text = "8080", Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _rbClientTcp = new RadioButton { Text = "TCP", AutoSize = true, Checked = true, Margin = new Padding(6, 8, 3, 3) };
            _rbClientUdt = new RadioButton { Text = "UDT", AutoSize = true, Margin = new Padding(6, 8, 3, 3) };
            _btnSend = new Button { Dock = DockStyle.Fill, Margin = new Padding(4, 2, 2, 2) };
            UiStyle.Primary(_btnSend);
            _btnSend.Click += BtnSend_Click;
            _btnCancel = new Button { Dock = DockStyle.Fill, Margin = new Padding(4, 2, 2, 2), Enabled = false };
            UiStyle.Secondary(_btnCancel);
            _btnCancel.Click += BtnCancel_Click;
            _lblFile = new Label { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Color.FromArgb(68, 68, 68), Margin = new Padding(0, 0, 6, 0) };
            _txtFile = new TextBox { Dock = DockStyle.Fill, Margin = new Padding(3, 6, 3, 6) };
            _btnBrowseFile = new Button { Dock = DockStyle.Fill, Margin = new Padding(3, 5, 3, 5) };
            UiStyle.Secondary(_btnBrowseFile);
            _btnBrowseFile.Click += BtnBrowseFile_Click;

            _chkMonitor = new CheckBox { AutoSize = true, Margin = new Padding(2, 8, 16, 3) };
            _chkMonitor.CheckedChanged += ChkMonitor_CheckedChanged;
            _chkFolder = new CheckBox { AutoSize = true, Margin = new Padding(2, 8, 16, 3) };
            _chkFolder.CheckedChanged += ChkFolder_CheckedChanged;
            _chkSync = new CheckBox { AutoSize = true, Margin = new Padding(2, 8, 16, 3) };
            _chkVerifyHash = new CheckBox { AutoSize = true, Margin = new Padding(2, 8, 16, 3) };
            _chkVerifyHash.Checked = Config.GetBool("VerifyHash", false);
            _chkVerifyHash.CheckedChanged += (s2, e2) => Config.SetBool("VerifyHash", _chkVerifyHash.Checked);
            var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            optionsRow.Controls.Add(_chkMonitor);
            optionsRow.Controls.Add(_chkFolder);
            optionsRow.Controls.Add(_chkSync);
            optionsRow.Controls.Add(_chkVerifyHash);

            _lblSrcPort = new Label { AutoSize = true, Margin = new Padding(2, 10, 4, 3), ForeColor = Color.FromArgb(68, 68, 68) };
            _numSrcPort = new NumericUpDown { Width = 58, Minimum = 0, Maximum = 65535, Value = 0, Margin = new Padding(0, 6, 18, 3) };
            _lblConcurrency = new Label { AutoSize = true, Margin = new Padding(2, 10, 4, 3), ForeColor = Color.FromArgb(68, 68, 68) };
            _numConcurrency = new NumericUpDown { Width = 50, Minimum = 1, Maximum = 8, Value = 4, Margin = new Padding(0, 6, 18, 3) };
            _numConcurrency.ValueChanged += NumConcurrency_ValueChanged;
            _lblSpeed = new Label { AutoSize = true, Margin = new Padding(2, 10, 4, 3), ForeColor = Color.FromArgb(68, 68, 68) };
            _numSpeed = new NumericUpDown
            {
                Width = 72, Minimum = 0, Maximum = 1048576,
                Value = Config.GetInt("SpeedLimit", 0), Margin = new Padding(0, 6, 0, 3)
            };
            _numSpeed.ValueChanged += (s2, e2) => Config.SetInt("SpeedLimit", (int)_numSpeed.Value);
            var numericRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            numericRow.Controls.Add(_lblSrcPort);
            numericRow.Controls.Add(_numSrcPort);
            numericRow.Controls.Add(_lblConcurrency);
            numericRow.Controls.Add(_numConcurrency);
            numericRow.Controls.Add(_lblSpeed);
            numericRow.Controls.Add(_numSpeed);

            _btnScan = new Button { Width = 72, Height = 26, Margin = new Padding(2, 3, 8, 3) };
            UiStyle.Secondary(_btnScan);
            _btnScan.Click += BtnScan_Click;
            _btnFanOut = new Button { Width = 72, Height = 26, Margin = new Padding(2, 3, 8, 3) };
            UiStyle.Secondary(_btnFanOut);
            _btnFanOut.Click += BtnFanOut_Click;
            _btnQueue = new Button { Width = 96, Height = 26, Margin = new Padding(2, 3, 8, 3) };
            UiStyle.Secondary(_btnQueue);
            _btnQueue.Click += BtnQueue_Click;
            _btnResumeList = new Button { Width = 72, Height = 26, Margin = new Padding(2, 3, 0, 3) };
            UiStyle.Secondary(_btnResumeList);
            _btnResumeList.Click += BtnResumeList_Click;
            _btnSendText = new Button { Width = 72, Height = 26, Margin = new Padding(14, 3, 8, 3) };
            UiStyle.Secondary(_btnSendText);
            _btnSendText.Click += BtnSendText_Click;
            _lblPairingC = new Label { AutoSize = true, Margin = new Padding(10, 10, 4, 3), ForeColor = Color.FromArgb(68, 68, 68) };
            _txtPairing = new TextBox { Width = 58, MaxLength = 12, Margin = new Padding(0, 6, 0, 3) };
            var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            actionRow.Controls.Add(_btnScan);
            actionRow.Controls.Add(_btnFanOut);
            actionRow.Controls.Add(_btnQueue);
            actionRow.Controls.Add(_btnResumeList);
            actionRow.Controls.Add(_btnSendText);
            actionRow.Controls.Add(_lblPairingC);
            actionRow.Controls.Add(_txtPairing);

            // Tray icon for completion notifications (Win7-compatible balloon tips)
            _notifyIcon = new NotifyIcon
            {
                Icon = System.Drawing.SystemIcons.Application,
                Visible = true,
                Text = L.AppTitle
            };
            _notifyIcon.DoubleClick += (s2, e2) =>
            {
                this.WindowState = FormWindowState.Normal;
                this.Show();
                this.Activate();
            };
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add(L.TrayShow, null, (s2, e2) =>
            {
                this.Show();
                this.WindowState = FormWindowState.Normal;
                this.Activate();
            });
            var autoStartItem = new ToolStripMenuItem(L.TrayAutoStart)
            {
                CheckOnClick = true,
                Checked = AutoStart.IsEnabled()
            };
            autoStartItem.Click += (s2, e2) =>
            {
                AutoStart.Set(autoStartItem.Checked, Application.ExecutablePath);
                AddLog(autoStartItem.Checked ? L.AutoStartOn : L.AutoStartOff);
            };
            _trayMenu.Items.Add(autoStartItem);
            _trayMenu.Items.Add(L.UpdBtn, null, (s2, e2) => ShowUpdateDialog());
            _trayMenu.Items.Add(L.About, null, (s2, e2) =>
            {
                MessageBox.Show(this, L.AboutText(AppVersion), L.About,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            _trayMenu.Items.Add(L.TrayExit, null, (s2, e2) =>
            {
                _trayExit = true;
                this.Close();
            });
            _notifyIcon.ContextMenuStrip = _trayMenu;
            this.Resize += MainForm_Resize;
            LoadKnownDevices();
            tlpC.Controls.Add(_lblServerIp, 0, 0);
            tlpC.Controls.Add(_txtServerIp, 1, 0);
            tlpC.Controls.Add(_lblPortC, 2, 0);
            tlpC.Controls.Add(_txtPortC, 3, 0);
            tlpC.Controls.Add(_rbClientTcp, 4, 0);
            tlpC.Controls.Add(_rbClientUdt, 5, 0);
            tlpC.Controls.Add(_btnSend, 6, 0);
            tlpC.Controls.Add(_lblFile, 0, 1);
            tlpC.Controls.Add(_txtFile, 1, 1);
            tlpC.SetColumnSpan(_txtFile, 4);
            tlpC.Controls.Add(_btnBrowseFile, 5, 1);
            tlpC.Controls.Add(_btnCancel, 6, 1);
            tlpC.Controls.Add(optionsRow, 0, 2);
            tlpC.SetColumnSpan(optionsRow, 7);
            tlpC.Controls.Add(numericRow, 0, 3);
            tlpC.SetColumnSpan(numericRow, 7);
            tlpC.Controls.Add(actionRow, 0, 4);
            tlpC.SetColumnSpan(actionRow, 7);
            _gbClient.Controls.Add(tlpC);
            root.Controls.Add(_gbClient, 0, 2);

            // ---- Progress: server | client side by side in a proportional splitter ----
            _splitProgress = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical, SplitterWidth = 6, BackColor = Color.White };
            _splitProgress.Panel1.BackColor = Color.White;
            _splitProgress.Panel2.BackColor = Color.White;

            _gbProgressS = new GroupBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var tlpPs = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 2, 6, 4) };
            tlpPs.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlpPs.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            _progressPanelS = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(243, 244, 246),
                Margin = new Padding(0)
            };
            _progressPanelS.Resize += (s, e) => SyncCardWidths(_progressPanelS);
            _lblStatusS = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(106, 106, 106), Font = new Font("Segoe UI", 8.25f), Text = "" };
            tlpPs.Controls.Add(_progressPanelS, 0, 0);
            tlpPs.Controls.Add(_lblStatusS, 0, 1);
            _gbProgressS.Controls.Add(tlpPs);
            _splitProgress.Panel1.Controls.Add(_gbProgressS);

            _gbProgressC = new GroupBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var tlpPc = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 2, 6, 4) };
            tlpPc.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlpPc.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            _progressPanelC = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                BackColor = Color.FromArgb(243, 244, 246),
                Margin = new Padding(0)
            };
            _progressPanelC.Resize += (s, e) => SyncCardWidths(_progressPanelC);
            _lblStatusC = new Label { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(106, 106, 106), Font = new Font("Segoe UI", 8.25f), Text = L.Ready };
            tlpPc.Controls.Add(_progressPanelC, 0, 0);
            tlpPc.Controls.Add(_lblStatusC, 0, 1);
            _gbProgressC.Controls.Add(tlpPc);
            _splitProgress.Panel2.Controls.Add(_gbProgressC);
            root.Controls.Add(_splitProgress, 0, 3);

            // ---- Log panel: dark console-style list ----
            _gbLog = new GroupBox { Dock = DockStyle.Fill, BackColor = Color.White };
            var tlpL = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(6, 2, 6, 4) };
            tlpL.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlpL.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            _lstLog = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 9f),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.FromArgb(212, 212, 212),
                Margin = new Padding(0)
            };
            var logButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, BackColor = Color.White, Margin = new Padding(0), Padding = new Padding(0, 2, 2, 0) };
            _btnExportLog = new Button { Width = 96, Height = 24 };
            UiStyle.Secondary(_btnExportLog);
            _btnExportLog.Click += BtnExportLog_Click;
            logButtons.Controls.Add(_btnExportLog);
            tlpL.Controls.Add(_lstLog, 0, 0);
            tlpL.Controls.Add(logButtons, 0, 1);
            _gbLog.Controls.Add(tlpL);
            root.Controls.Add(_gbLog, 0, 4);

            Controls.Add(root);
        }

        private void CmbLang_SelectedIndexChanged(object sender, EventArgs e)
        {
            L.IsChinese = _cmbLang.SelectedIndex == 1;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = L.AppTitle;
            _lblHeader.Text = L.AppTitle;

            _gbServer.Text = L.ServerSettings;
            _lblBind.Text = L.BindAddress;
            _lblPortS.Text = L.Port;
            _lblSaveDir.Text = L.SaveTo;
            _btnBrowseDir.Text = L.Browse;
            _btnStartServer.Text = L.StartServer;
            _btnStopServer.Text = L.StopServer;
            _btnOpenDir.Text = L.OpenSaveDir;
            _btnRecent.Text = L.RecentFiles;

            _gbClient.Text = L.ClientSettings;
            _lblServerIp.Text = L.ServerIP;
            _lblPortC.Text = L.Port;
            _lblFile.Text = _chkMonitor.Checked ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnBrowseFile.Text = L.Browse;
            _btnSend.Text = _chkMonitor.Checked ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _btnCancel.Text = L.CancelBtn;
            _chkFolder.Text = L.FolderMode;
            _chkSync.Text = L.SyncModeLabel;
            _btnResumeList.Text = L.ResumeBtn;
            _chkVerifyHash.Text = L.VerifyHashLabel;
            _lblSpeed.Text = L.SpeedLimitLabel;
            _btnQueue.Text = L.QueueBtn;
            _btnScan.Text = L.ScanBtn;
            _chkMonitor.Text = L.MonitorMode;
            _lblConcurrency.Text = L.ConcurrencyLabel;
            _lblSrcPort.Text = L.SrcPortLabel;

            _gbProgressS.Text = L.ServerProgress;
            _gbProgressC.Text = L.ClientProgress;

            _gbLog.Text = L.LogGroup;
            _btnExportLog.Text = L.ExportLog;
            _btnCheckUpdate.Text = L.UpdBtn;
            _chkPairing.Text = L.PairingLabel;
            _btnSendText.Text = L.SendTextBtn;
            _btnFanOut.Text = L.FanOutBtn;
            _lblPairingC.Text = L.PairingClientLabel;
            _lblHttpPort.Text = L.HttpPortLabel;
            if (_httpShare == null || !_httpShare.IsRunning)
                _btnHttpShare.Text = L.HttpShareBtn;
            else
                _btnHttpShare.Text = L.HttpShareStop;

            PopulateBindAddresses();
        }

        private void ApplyConfig()
        {
            // Language
            _cmbLang.SelectedIndex = Config.Get("Language", "English") == "中文" ? 1 : 0;

            // Protocol
            _chkServerTcp.Checked = Config.GetBool("ServerTCP", true);
            _chkServerUdt.Checked = Config.GetBool("ServerUDT", false);
            string clientProto = Config.Get("ClientProtocol", "TCP");
            _rbClientTcp.Checked = clientProto != "UDT";
            _rbClientUdt.Checked = clientProto == "UDT";

            // Server
            _txtPortS.Text = Config.Get("ServerPort", "8080");
            _txtSaveDir.Text = Config.Get("SaveDir", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
            string bind = Config.Get("ServerBind", "");
            if (!string.IsNullOrWhiteSpace(bind))
            {
                for (int i = 0; i < _cmbBind.Items.Count; i++)
                {
                    if (_cmbBind.Items[i].ToString() == bind) { _cmbBind.SelectedIndex = i; break; }
                }
            }

            // Client
            _txtServerIp.Text = Config.Get("ClientIP", "127.0.0.1");
            _txtPortC.Text = Config.Get("ClientPort", "8080");
            _txtFile.Text = Config.Get("LastPath", "");
            _chkFolder.Checked = Config.GetBool("FolderMode", false);
            _chkSync.Checked = Config.GetBool("SyncMode", false);
            _chkSync.Enabled = _chkFolder.Checked && !Config.GetBool("MonitorMode", false);
            _chkMonitor.Checked = Config.GetBool("MonitorMode", false);
            _numConcurrency.Value = Math.Max(1, Math.Min(8, Config.GetInt("Concurrency", 4)));
            _numSrcPort.Value = Math.Max(0, Math.Min(65535, Config.GetInt("SrcPort", 0)));
            _txtPairing.Text = Config.Get("PairingCode", "");
        }

        private void SaveConfig()
        {
            Config.Set("Language", _cmbLang.SelectedIndex == 1 ? "中文" : "English");
            Config.SetBool("ServerTCP", _chkServerTcp.Checked);
            Config.SetBool("ServerUDT", _chkServerUdt.Checked);
            Config.Set("ClientProtocol", _rbClientUdt.Checked ? "UDT" : "TCP");
            Config.Set("ServerPort", _txtPortS.Text.Trim());
            Config.Set("SaveDir", _txtSaveDir.Text.Trim());
            Config.Set("ServerBind", _cmbBind.SelectedItem != null ? _cmbBind.SelectedItem.ToString() : "");
            Config.Set("ClientIP", _txtServerIp.Text.Trim());
            Config.Set("ClientPort", _txtPortC.Text.Trim());
            Config.Set("LastPath", _txtFile.Text.Trim());
            Config.SetBool("FolderMode", _chkFolder.Checked);
            Config.SetBool("SyncMode", _chkSync.Checked);
            Config.SetBool("MonitorMode", _chkMonitor.Checked);
            Config.SetBool("VerifyHash", _chkVerifyHash.Checked);
            Config.SetInt("Concurrency", (int)_numConcurrency.Value);
            Config.SetInt("SrcPort", (int)_numSrcPort.Value);
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

        // ---- Auto update ----

        /// <summary>Default update source: this project's GitHub Releases. The updater
        /// detects api.github.com URLs and reads version/download URL from the release,
        /// SHA256 from the .sha256 sidecar asset.</summary>
        internal const string DefaultUpdateUrl =
            "https://api.github.com/repos/54gogogo10/FileTransfer_UDT/releases/latest";

        private void BtnCheckUpdate_Click(object sender, EventArgs e)
        {
            ShowUpdateDialog();
        }

        private void ShowUpdateDialog()
        {
            using (var dlg = new UpdateDialog())
                dlg.ShowDialog(this);
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
                        try
                        {
                            BeginInvoke((MethodInvoker)delegate
                            {
                                if (IsDisposed) return;
                                AddLog(L.UpdAvailable(AppVersion, m.Version.ToString()));
                                ShowUpdateDialog();
                            });
                        }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    }
                }
                catch { }
            });
        }

        /// <summary>Called by UpdateDialog after a verified download: swap in the new exe
        /// and restart. Invoked on the UI thread.</summary>
        internal void ApplyUpdateAndRestart(string stagedPath)
        {
            try
            {
                Updater.Apply(stagedPath, Application.ExecutablePath);
            }
            catch (Exception ex)
            {
                AddLog(L.UpdApplyFailed(ex.Message));
                MessageBox.Show(this, L.UpdApplyFailed(ex.Message), L.UpdTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            AddLog(L.UpdRestarting);
            SaveConfig();
            _trayExit = true;
            try { System.Diagnostics.Process.Start(Application.ExecutablePath); }
            catch { /* new exe is in place; user can start it manually */ }
            Close();
        }

        private void BtnBrowseDir_Click(object sender, EventArgs e)
        {
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = L.BrowseDirDesc;
                if (dlg.ShowDialog() == DialogResult.OK)
                    _txtSaveDir.Text = dlg.SelectedPath;
            }
        }

        private void ChkFolder_CheckedChanged(object sender, EventArgs e)
        {
            if (_chkMonitor.Checked) return; // Monitor mode overrides folder mode
            bool isFolder = _chkFolder.Checked;
            _lblFile.Text = isFolder ? L.FolderLabel : L.FileLabel;
            _btnSend.Text = isFolder ? L.SendFolder : L.SendFile;
            _txtFile.Text = "";
            // Sync builds on folder mode (0x04 diff transfer)
            _chkSync.Enabled = isFolder;
            if (!isFolder) _chkSync.Checked = false;
        }

        private void NumConcurrency_ValueChanged(object sender, EventArgs e)
        {
            if (_numConcurrency.Value < 1) _numConcurrency.Value = 1;
            if (_numConcurrency.Value > 16) _numConcurrency.Value = 16;
        }

        private void ChkMonitor_CheckedChanged(object sender, EventArgs e)
        {
            bool isMonitor = _chkMonitor.Checked;
            _lblFile.Text = isMonitor ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnSend.Text = isMonitor ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _chkFolder.Enabled = !isMonitor;
            _chkSync.Enabled = !isMonitor && _chkFolder.Checked;
            if (isMonitor) _chkSync.Checked = false;
            _txtFile.Text = "";
        }

        private void ChkPairing_CheckedChanged(object sender, EventArgs e)
        {
            // Show a fresh code immediately; Start regenerates one per server session
            if (_chkPairing.Checked)
                _lblPairingCode.Text = WireAuth.GeneratePairingCode();
            _lblPairingCode.Visible = _chkPairing.Checked;
        }

        // ---- HTTP share (browser download) ----

        private void BtnHttpShare_Click(object sender, EventArgs e)
        {
            if (_httpShare != null && _httpShare.IsRunning)
            {
                _httpShare.Stop();
                _httpShare = null;
                AddLog(L.HttpShareOff);
                _btnHttpShare.Text = L.HttpShareBtn;
                return;
            }

            string dir = _txtSaveDir.Text.Trim();
            if (!Directory.Exists(dir))
            {
                MessageBox.Show(L.HttpShareDirMissing, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            int port = (int)_numHttpPort.Value;
            Config.SetInt("HttpSharePort", port);
            var share = new HttpShareServer();
            share.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            try
            {
                // Pairing enabled -> reuse the pairing code as the share access code
                string token = _chkPairing.Checked ? _lblPairingCode.Text : null;
                share.Start(dir, port, token);
                _httpShare = share;
                _btnHttpShare.Text = L.HttpShareStop;
                AddLog(L.HttpShareOn(share.LanUrl));
            }
            catch (Exception ex)
            {
                AddLog(L.HttpShareStartFailed(ex.Message));
                MessageBox.Show(L.HttpShareStartFailed(ex.Message), L.DlgError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void MainForm_DragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
                e.Effect = DragDropEffects.Copy;
            else
                e.Effect = DragDropEffects.None;
        }

        private void MainForm_DragDrop(object sender, DragEventArgs e)
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            if (files.Length == 1)
            {
                string path = files[0];
                if (Directory.Exists(path))
                {
                    _chkFolder.Checked = true;
                    _txtFile.Text = path;
                }
                else if (File.Exists(path))
                {
                    _chkFolder.Checked = false;
                    _txtFile.Text = path;
                }
                else
                {
                    AddLog(L.DragDropInvalid(path));
                }
                return;
            }

            // Multiple items: enqueue every valid file/folder and open the send queue
            var tasks = new System.Collections.Generic.List<QueuedTask>();
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
            using (var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask, tasks, CaptureQueuedTaskFor))
                dlg.ShowDialog(this);
        }

        private void BtnResumeList_Click(object sender, EventArgs e)
        {
            var states = ResumeState.ListAll();
            var validStates = new System.Collections.Generic.List<ResumeState>();
            if (states != null)
            {
                foreach (var s in states) { if (s != null) validStates.Add(s); }
            }
            var folderStates = FolderResumeState.ListAll();
            if (validStates.Count == 0 && folderStates.Count == 0)
            {
                MessageBox.Show(L.ResumeListEmpty, L.ResumeListTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ResumeDialog(validStates.ToArray(), folderStates.ToArray()))
            {
                var result = dlg.ShowDialog();
                if (result == DialogResult.OK && dlg.SelectedFolderState != null)
                {
                    var f = dlg.SelectedFolderState;
                    _txtServerIp.Text = f.ServerIp;
                    _txtPortC.Text = f.Port.ToString();
                    // Checked first — the CheckedChanged handler clears the path box
                    _chkFolder.Checked = true;
                    _txtFile.Text = f.FolderPath;
                    _numConcurrency.Value = 1;
                    _pendingResumeSession = f.SessionId;
                    AddLog(L.ResumeQueued);
                    if (f.IsUdt)
                    {
                        _rbClientTcp.Checked = false;
                        _rbClientUdt.Checked = true;
                    }
                    else
                    {
                        _rbClientTcp.Checked = true;
                        _rbClientUdt.Checked = false;
                    }
                }
                else if (result == DialogResult.OK && dlg.SelectedState != null)
                {
                    var s = dlg.SelectedState;
                    _txtServerIp.Text = s.ServerIp;
                    _txtPortC.Text = s.Port.ToString();
                    // Checked first — the CheckedChanged handler clears the path box
                    _chkFolder.Checked = false;
                    _txtFile.Text = s.FilePath;
                    _numConcurrency.Value = 1;
                    _pendingResumeSession = s.SessionId;
                    AddLog(L.ResumeQueued);
                    if (s.IsUdt)
                    {
                        _rbClientTcp.Checked = false;
                        _rbClientUdt.Checked = true;
                    }
                    else
                    {
                        _rbClientTcp.Checked = true;
                        _rbClientUdt.Checked = false;
                    }
                }
            }
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

        private void BtnQueue_Click(object sender, EventArgs e)
        {
            if (_chkMonitor.Checked || _monitorCts != null) return;
            using (var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask, null, CaptureQueuedTaskFor))
                dlg.ShowDialog(this);
        }

        private QueuedTask CaptureQueuedTask()
        {
            string path = _txtFile.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) return null;
            return CaptureQueuedTaskFor(path, _chkFolder.Checked);
        }

        private QueuedTask CaptureQueuedTaskFor(string path, bool isFolder)
        {
            int port;
            if (!int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
                return null;
            if (string.IsNullOrWhiteSpace(_txtServerIp.Text.Trim()))
                return null;
            return new QueuedTask
            {
                FilePath = path,
                IsFolder = isFolder,
                ServerIp = _txtServerIp.Text.Trim(),
                Port = port,
                IsUdp = _rbClientUdt.Checked,
                SrcPort = (int)_numSrcPort.Value,
                Concurrency = (int)_numConcurrency.Value,
                VerifyHash = _chkVerifyHash.Checked,
                SpeedLimit = (int)_numSpeed.Value * 1024
            };
        }

        private async Task<bool> ExecuteQueuedTask(QueuedTask t)
        {
            return await StartTransfer(t.FilePath, t.IsFolder, t.ServerIp, t.Port, !t.IsUdp,
                t.SrcPort, t.Concurrency, t.VerifyHash, t.SpeedLimit, null);
        }

        private void BtnScan_Click(object sender, EventArgs e)
        {
            if (_chkMonitor.Checked || _monitorCts != null) return;
            using (var dlg = new DiscoveryDialog(UseDiscoveredDevice, _knownDevices))
                dlg.ShowDialog(this);
        }

        private void UseDiscoveredDevice(DeviceInfo d)
        {
            _txtServerIp.Text = d.Ip;
            _txtPortC.Text = d.Port.ToString();
            if (d.SupportsTcp && !d.SupportsUdt)
            {
                _rbClientTcp.Checked = true;
                _rbClientUdt.Checked = false;
            }
            else if (d.SupportsUdt && !d.SupportsTcp)
            {
                _rbClientTcp.Checked = false;
                _rbClientUdt.Checked = true;
            }
            if (d.RequiresPairing)
            {
                AddLog(L.UsePairingHint);
                _txtPairing.Focus();
            }
        }

        // ---- Fan-out: one file/folder to many devices in parallel ----

        private void BtnFanOut_Click(object sender, EventArgs e)
        {
            if (_chkMonitor.Checked || _monitorCts != null) return;
            bool isFolder = _chkFolder.Checked;
            string path = _txtFile.Text.Trim();
            if (isFolder ? !Directory.Exists(path) : !File.Exists(path))
            {
                MessageBox.Show(isFolder ? L.DirNotExist : L.FileNotFound, L.DlgError,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using (var dlg = new FanOutDialog(_knownDevices))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.SelectedDevices.Count == 0) return;
                StartFanOut(dlg.SelectedDevices, path, isFolder);
            }
        }

        /// <summary>Sends the same item to every selected device concurrently, one
        /// progress card per target. Honors the panel's TCP/UDT selection, speed limit
        /// and pairing code; the source-port setting is ignored (parallel clients
        /// cannot share one local port).</summary>
        private void StartFanOut(System.Collections.Generic.List<DeviceInfo> targets, string path, bool isFolder)
        {
            bool isTcp = _rbClientTcp.Checked;
            int speedLimit = (int)_numSpeed.Value * 1024;
            string pairing = _txtPairing.Text.Trim();
            if ((int)_numSrcPort.Value != 0)
                AddLog(L.FanOutIgnoreSrcPort);

            DisableClientInputs();
            _btnCancel.Enabled = true;
            _lblStatusC.Text = L.FanOutStart(targets.Count);

            lock (_fanOutClients) _fanOutClients.Clear();
            _fanOutRunning = true;
            int total = targets.Count;
            int finished = 0;
            int okCount = 0;

            foreach (var t in targets)
            {
                var card = CreateTransferCard(_progressPanelC);
                Action<Action> onUi = a => { try { this.Invoke(a); } catch (ObjectDisposedException) { } catch (InvalidOperationException) { } };

                if (isTcp)
                {
                    var client = ClientFactory.CreateTcp(t.Ip, t.Port, path, 0, speedLimit, pairing);
                    client.OnLog += msg => onUi(() => AddLog(msg));
                    client.OnProgress += p => onUi(() => UpdateCardProgress(card, p));
                    client.OnError += msg => onUi(() =>
                    {
                        AddLog(L.ErrorPrefix + msg);
                        UpdateCardComplete(card);
                    });
                    client.OnTransferComplete += () => onUi(() =>
                    {
                        UpdateCardComplete(card);
                        System.Threading.Interlocked.Increment(ref okCount);
                        RememberDevice(t.Ip, t.Port, false);
                    });
                    client.OnStopped += () =>
                    {
                        UpdateCardComplete(card);
                        if (System.Threading.Interlocked.Increment(ref finished) == total)
                            onUi(() => FinalizeFanOut(okCount, total));
                    };
                    lock (_fanOutClients) _fanOutClients.Add(client);
                    var _ = isFolder ? client.SendFolderAsync(path) : client.SendAsync();
                }
                else
                {
                    var client = ClientFactory.CreateUdt(t.Ip, t.Port, path, 0, speedLimit, pairing);
                    client.OnLog += msg => onUi(() => AddLog(msg));
                    client.OnProgress += p => onUi(() => UpdateCardProgress(card, p));
                    client.OnError += msg => onUi(() =>
                    {
                        AddLog(L.ErrorPrefix + msg);
                        UpdateCardComplete(card);
                    });
                    client.OnTransferComplete += () => onUi(() =>
                    {
                        UpdateCardComplete(card);
                        System.Threading.Interlocked.Increment(ref okCount);
                        RememberDevice(t.Ip, t.Port, true);
                    });
                    client.OnStopped += () =>
                    {
                        UpdateCardComplete(card);
                        if (System.Threading.Interlocked.Increment(ref finished) == total)
                            onUi(() => FinalizeFanOut(okCount, total));
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

        private void BtnBrowseFile_Click(object sender, EventArgs e)
        {
            if (_chkFolder.Checked || _chkMonitor.Checked)
            {
                using (var dlg = new FolderBrowserDialog())
                {
                    dlg.Description = _chkMonitor.Checked ? L.MonitorLabel : L.BrowseFolderDesc;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        _txtFile.Text = dlg.SelectedPath;
                }
            }
            else
            {
                using (var dlg = new OpenFileDialog())
                {
                    dlg.Title = L.BrowseFileTitle;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        _txtFile.Text = dlg.FileName;
                }
            }
        }

        private void BtnOpenDir_Click(object sender, EventArgs e)
        {
            string dir = _txtSaveDir.Text.Trim();
            if (Directory.Exists(dir))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", dir); } catch { }
            }
            else
            {
                MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void BtnRecent_Click(object sender, EventArgs e)
        {
            using (var dlg = new RecentFilesDialog(_recentFiles.ToArray()))
                dlg.ShowDialog(this);
        }

        /// <summary>Records a received file and shows a tray notification.</summary>
        private void MainForm_Resize(object sender, EventArgs e)
        {
            // Minimize-to-tray: hide instead of occupying the taskbar
            if (this.WindowState == FormWindowState.Minimized && !_trayExit)
                this.Hide();
        }

        // ---- Known devices (device memory) ----

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
            var sb = new System.Text.StringBuilder();
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

        // ---- Text messages (0x06) ----

        /// <summary>Shows a received text message: log, balloon tip, and a non-modal viewer.</summary>
        private void OnTextReceived(string text)
        {
            string preview = ServerWire.Preview(text);
            AddLog(L.S_TextReceived(preview));
            Notify(L.NotifyTextTitle, preview);
            if (_textRecvDialog == null || _textRecvDialog.IsDisposed)
                _textRecvDialog = new TextReceivedDialog(text);
            else
                _textRecvDialog.AppendMessage(text);
            _textRecvDialog.Show(this);
            _textRecvDialog.Activate();
        }

        private async void BtnSendText_Click(object sender, EventArgs e)
        {
            string text;
            using (var dlg = new TextSendDialog())
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                text = dlg.MessageText;
            }
            if (string.IsNullOrEmpty(text)) return;

            string ip = _txtServerIp.Text.Trim();
            int port;
            if (ip.Length == 0 || !int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(this, L.InvalidPort, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            bool isUdt = _rbClientUdt.Checked;
            _btnSendText.Enabled = false;
            try
            {
                bool ok = await SendTextMessageAsync(text, ip, port, isUdt).ConfigureAwait(true);
                if (ok)
                {
                    AddLog(L.SendTextDone);
                    RememberDevice(ip, port, isUdt);
                }
            }
            finally
            {
                _btnSendText.Enabled = true;
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
                client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                client.OnError += msg => { error = new Exception(msg); };
                client.OnStopped += () => done.TrySetResult(error == null);
                await client.SendTextAsync(text).ConfigureAwait(true);
            }
            else
            {
                var client = new TransferClient(ip, port, "", 0, 4194304, 0);
                client.PairingCode = pairing;
                client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                client.OnError += msg => { error = new Exception(msg); };
                client.OnStopped += () => done.TrySetResult(error == null);
                try { await client.SendTextAsync(text).ConfigureAwait(true); }
                catch { /* RunTransfer rethrows after raising OnError; OnStopped settles the task */ }
            }
            return await done.Task.ConfigureAwait(true);
        }

        private void BtnStartServer_Click(object sender, EventArgs e)
        {
            int port;
            if (!int.TryParse(_txtPortS.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(L.InvalidPort, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string saveDir = _txtSaveDir.Text.Trim();
            if (!Directory.Exists(saveDir))
            {
                MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            string bindAddr = _cmbBind.SelectedItem != null ? _cmbBind.SelectedItem.ToString() : "0.0.0.0";
            if (string.IsNullOrWhiteSpace(bindAddr))
                bindAddr = "0.0.0.0";

            if (!_chkServerTcp.Checked && !_chkServerUdt.Checked)
            {
                MessageBox.Show(L.NoProtocolSelected, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Port availability pre-check: offer the next free port when busy
            bool needTcp = _chkServerTcp.Checked, needUdp = _chkServerUdt.Checked;
            if (!Utils.IsPortFree(port, needTcp, needUdp))
            {
                int alt = Utils.FindFreePortFrom(port + 1, needTcp, needUdp);
                if (alt == 0)
                {
                    MessageBox.Show(L.PortBusyNoAlt(port), L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                if (MessageBox.Show(this, L.PortBusyOffer(port, alt), L.DlgError,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
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
            if (_chkPairing.Checked)
            {
                pairingCode = WireAuth.GeneratePairingCode();
                _lblPairingCode.Text = pairingCode;
            }

            if (_chkServerTcp.Checked)
            {
                bool tcpStarted = false;
                var tcpServer = new TransferServer(bindAddr, port, GetArchiveDir(saveDir));
                tcpServer.PairingCode = pairingCode;
                tcpServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                tcpServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
                tcpServer.OnFileReceived += (path, size) => this.Invoke((Action)(() => OnFileReceived(path, size)));
                tcpServer.OnTextReceived += t => this.Invoke((Action)(() => OnTextReceived(t)));
                tcpServer.OnClientConnected += ep => this.Invoke((Action)(() => { }));
                tcpServer.OnClientProgress += (ep, p) => this.Invoke((Action)(() =>
                {
                    var card = GetOrCreateTcpCard(ep);
                    UpdateCardProgress(card, p);
                }));
                tcpServer.OnClientTransferComplete += ep => this.Invoke((Action)(() =>
                {
                    Panel card;
                    if (_tcpCards.TryGetValue(ep, out card)) { UpdateCardComplete(card); _tcpCards.Remove(ep); }
                }));
                tcpServer.OnTransferComplete += () => this.Invoke((Action)(() =>
                {
                    foreach (var c in _tcpCards.Values) UpdateCardComplete(c);
                    _tcpCards.Clear();
                    _lblStatusS.Text = L.Listening;
                }));
                tcpServer.OnStarted += () => this.Invoke((Action)(() =>
                {
                    tcpStarted = true;
                    _serverCount++;
                    OnServerStarted();
                }));
                tcpServer.OnStopped += () => this.Invoke((Action)(() =>
                {
                    if (!tcpStarted) return; // start failed, ignore
                    foreach (var c in _tcpCards.Values) UpdateCardComplete(c);
                    _tcpCards.Clear();
                    _server = null;
                    OnServerStopped();
                }));
                _server = tcpServer;
                tcpServer.Start();
            }

            if (_chkServerUdt.Checked)
            {
                bool udtStarted = false;
                var udtServer = new TransferUdtServer(bindAddr, port, GetArchiveDir(saveDir));
                udtServer.PairingCode = pairingCode;
                udtServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                udtServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
                udtServer.OnFileReceived += (path, size) => this.Invoke((Action)(() => OnFileReceived(path, size)));
                udtServer.OnTextReceived += t => this.Invoke((Action)(() => OnTextReceived(t)));
                udtServer.OnClientConnected += ep => this.Invoke((Action)(() => { }));
                udtServer.OnClientProgress += (ep, p) => this.Invoke((Action)(() =>
                {
                    var card = GetOrCreateUdtCard(ep);
                    UpdateCardProgress(card, p);
                }));
                udtServer.OnClientTransferComplete += ep => this.Invoke((Action)(() =>
                {
                    Panel card;
                    if (_udtCards.TryGetValue(ep, out card)) { UpdateCardComplete(card); _udtCards.Remove(ep); }
                }));
                udtServer.OnTransferComplete += () => this.Invoke((Action)(() =>
                {
                    foreach (var c in _udtCards.Values) UpdateCardComplete(c);
                    _udtCards.Clear();
                    _lblStatusS.Text = L.Listening;
                }));
                udtServer.OnStarted += () => this.Invoke((Action)(() =>
                {
                    udtStarted = true;
                    _serverCount++;
                    OnServerStarted();
                }));
                udtServer.OnStopped += () => this.Invoke((Action)(() =>
                {
                    if (!udtStarted) return;
                    foreach (var c in _udtCards.Values) UpdateCardComplete(c);
                    _udtCards.Clear();
                    _serverUdt = null;
                    OnServerStopped();
                }));
                _serverUdt = udtServer;
                udtServer.Start();
            }

            if (_serverCount == 0)
            {
                // Both protocols failed to start
                EnableServerInputs();
                MessageBox.Show(L.ServerStartFailed, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            else
            {
                // Advertise the server over UDP so LAN clients can discover it
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                if (_discoveryServer == null) _discoveryServer = new DiscoveryServer(dPort);
                _discoveryServer.Start(Environment.MachineName, port,
                    _chkServerTcp.Checked, _chkServerUdt.Checked, pairingCode != null);
            }
        }

        private void DisableServerInputs()
        {
            _chkServerTcp.Enabled = false;
            _chkServerUdt.Enabled = false;
            _chkPairing.Enabled = false;
            _cmbLang.Enabled = false;
            _cmbBind.Enabled = false;
            _txtPortS.Enabled = false;
            _txtSaveDir.Enabled = false;
            _btnBrowseDir.Enabled = false;
        }

        private void EnableServerInputs()
        {
            _btnStartServer.Enabled = true;
            _btnStopServer.Enabled = false;
            _chkServerTcp.Enabled = true;
            _chkServerUdt.Enabled = true;
            _chkPairing.Enabled = true;
            _cmbLang.Enabled = true;
            _cmbBind.Enabled = true;
            _txtPortS.Enabled = true;
            _txtSaveDir.Enabled = true;
            _btnBrowseDir.Enabled = true;
        }

        private void DisableClientInputs()
        {
            _btnSend.Enabled = false;
            _btnCancel.Enabled = true;
            _rbClientTcp.Enabled = false;
            _rbClientUdt.Enabled = false;
            _cmbLang.Enabled = false;
            _txtServerIp.Enabled = false;
            _txtPortC.Enabled = false;
            _txtFile.Enabled = false;
            _btnBrowseFile.Enabled = false;
            _chkFolder.Enabled = false;
            _chkMonitor.Enabled = false;
            _numConcurrency.Enabled = false;
            _numSrcPort.Enabled = false;
            _btnResumeList.Enabled = false;
            _chkVerifyHash.Enabled = false;
            _numSpeed.Enabled = false;
            _btnQueue.Enabled = false;
            _btnScan.Enabled = false;
            _btnFanOut.Enabled = false;
            _btnSendText.Enabled = false;
            _txtPairing.Enabled = false;
        }

        private void OnServerStarted()
        {
            _btnStartServer.Enabled = false;
            _btnStopServer.Enabled = true;
            _lblStatusS.Text = L.Listening;

            // One-time firewall guidance — local probes cannot detect external blocks,
            // so hand the user the diagnosis and the exact allow command up front
            if (!Config.GetBool("FirewallHintShown", false))
            {
                Config.SetBool("FirewallHintShown", true);
                Config.Save();
                string cmd = "netsh advfirewall firewall add rule name=\"TrFileTransfer\" dir=in action=allow program=\"" +
                    Application.ExecutablePath + "\" enable=yes";
                var dlg = new TextReceivedDialog(L.FwHintText(cmd), L.FwHintTitle);
                dlg.Show(this);
            }
        }

        private void OnServerStopped()
        {
            if (_serverCount > 0) _serverCount--;
            if (_serverCount > 0) return; // still have other servers running
            EnableServerInputs();
            _lblStatusS.Text = L.ServerStopped;
        }

        private void BtnStopServer_Click(object sender, EventArgs e)
        {
            _serverCount = 0; // reset before stopping so OnStopped handlers see zero
            if (_discoveryServer != null) { _discoveryServer.Stop(); _discoveryServer = null; }
            if (_server != null) { _server.Stop(); _server = null; }
            if (_serverUdt != null) { _serverUdt.Stop(); _serverUdt = null; }
        }

        private async void BtnSend_Click(object sender, EventArgs e)
        {
            string path = _txtFile.Text.Trim();
            bool isFolder = _chkFolder.Checked;
            bool isMonitor = _chkMonitor.Checked;

            // Validate IP and port (shared)
            int port;
            if (!int.TryParse(_txtPortC.Text.Trim(), out port) || port < 1 || port > 65535)
            {
                MessageBox.Show(L.InvalidPort, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            string ip = _txtServerIp.Text.Trim();
            if (string.IsNullOrWhiteSpace(ip))
            {
                MessageBox.Show(L.EnterServerIP, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Monitor mode branch
            if (isMonitor)
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(L.MonitorDirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                StartMonitoring(path, ip, port);
                return;
            }

            // Normal send branch — delegate to the shared transfer runner
            int concurrency = (int)_numConcurrency.Value;
            bool isTcp = _rbClientTcp.Checked;
            int srcPort = (int)_numSrcPort.Value;

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
                _chkVerifyHash.Checked, (int)_numSpeed.Value * 1024, resumeSession);
        }

        /// <summary>
        /// Runs a single send (single or concurrent, TCP or UDT). Disables client inputs
        /// while running and restores them afterwards. Returns false on failure — the
        /// error is already surfaced via OnError event handlers.
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
                        MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return false;
                    }
                }
                else if (!File.Exists(path))
                {
                    MessageBox.Show(L.FileNotFound, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }

                DisableClientInputs();

                if (!isFolder && concurrency > 1)
                {
                    // Multi-concurrent transfer
                    var concurrent = new ConcurrentTransfer(ip, port, path, concurrency, isTcp, srcPort, speedLimit);
                    concurrent.PairingCode = _txtPairing.Text.Trim();
                    WireConcurrentEvents(concurrent);
                    if (isFolder)
                        await concurrent.SendFolderAsync();
                    else
                        await concurrent.SendAsync();
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
                    else if (isFolder && _chkSync.Checked)
                    {
                        // Sync mode: stable session per (folder, target) — the server-side
                        // 0x04 scan skips unchanged files, so only differences travel
                        AddLog(L.C_SyncStart(path));
                        Guid syncSession = FolderResumeState.DeriveSyncSession(path, ip, port, false);
                        await _client.SendFolderResumableAsync(syncSession, keepState: true);
                    }
                    else if (isFolder)
                        await _client.SendFolderAsync(path);
                    else if (resumeSession.HasValue)
                    {
                        await _client.SendResumableAsync(resumeSession.Value, verifyHash);
                        _pendingResumeSession = null;
                    }
                    else
                        await _client.SendAsync();
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
                    else if (isFolder && _chkSync.Checked)
                    {
                        AddLog(L.C_SyncStart(path));
                        Guid syncSession = FolderResumeState.DeriveSyncSession(path, ip, port, true);
                        await _clientUdt.SendFolderResumableAsync(syncSession, keepState: true);
                    }
                    else if (isFolder)
                        await _clientUdt.SendFolderAsync(path);
                    else if (resumeSession.HasValue)
                    {
                        await _clientUdt.SendResumableAsync(resumeSession.Value, verifyHash);
                        _pendingResumeSession = null;
                    }
                    else
                        await _clientUdt.SendAsync();
                }
                RememberDevice(ip, port, !isTcp);
                return true;
            }
            catch (Exception ex)
            {
                AddLog(L.ErrorPrefix + ex.Message);
                return false;
            }
            finally
            {
                try { ResetClientUI(); } catch { }
            }
        }

        private void WireClientEvents(TransferClient c)
        {
            var card = CreateTransferCard(_progressPanelC);
            c.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            c.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
            c.OnError += msg => this.Invoke((Action)(() =>
            {
                AddLog(L.ErrorPrefix + msg);
                ResetClientUI();
                UpdateCardComplete(card);
            }));
            c.OnTransferComplete += () => this.Invoke((Action)(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            }));
            c.OnStopped += () => this.Invoke((Action)(() => UpdateCardComplete(card)));
        }

        private void WireConcurrentEvents(ConcurrentTransfer c)
        {
            var card = CreateTransferCard(_progressPanelC);
            c.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            c.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
            c.OnError += msg => this.Invoke((Action)(() =>
            {
                AddLog(L.ErrorPrefix + msg);
                ResetClientUI();
                UpdateCardComplete(card);
            }));
            c.OnTransferComplete += () => this.Invoke((Action)(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            }));
        }

        private void WireUdtClientEvents(TransferUdtClient c)
        {
            var card = CreateTransferCard(_progressPanelC);
            c.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
            c.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
            c.OnError += msg => this.Invoke((Action)(() =>
            {
                AddLog(L.ErrorPrefix + msg);
                ResetClientUI();
                UpdateCardComplete(card);
            }));
            c.OnTransferComplete += () => this.Invoke((Action)(() =>
            {
                ResetClientUI();
                UpdateCardComplete(card);
                Notify(L.NotifySendDone, L.TransferComplete);
            }));
            c.OnStopped += () => this.Invoke((Action)(() => UpdateCardComplete(card)));
        }

        private void BtnCancel_Click(object sender, EventArgs e)
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
            if (_client != null)
                _client.Cancel();
            if (_clientUdt != null)
                _clientUdt.Cancel();
            _btnCancel.Enabled = false;
            _lblStatusC.Text = L.Cancelling;
        }

        private void ResetClientUI()
        {
            _lblStatusC.Text = L.Ready;
            _btnSend.Enabled = true;
            _btnCancel.Enabled = false;
            if (!_btnStopServer.Enabled)
            {
                _cmbLang.Enabled = true;
            }
            _rbClientTcp.Enabled = true;
            _rbClientUdt.Enabled = true;
            _txtServerIp.Enabled = true;
            _txtPortC.Enabled = true;
            _txtFile.Enabled = true;
            _btnBrowseFile.Enabled = true;
            _chkFolder.Enabled = true;
            _chkMonitor.Enabled = true;
            _numConcurrency.Enabled = true;
            _numSrcPort.Enabled = true;
            _btnResumeList.Enabled = true;
            _chkVerifyHash.Enabled = true;
            _numSpeed.Enabled = true;
            _btnQueue.Enabled = true;
            _btnScan.Enabled = true;
            _btnFanOut.Enabled = true;
            _btnSendText.Enabled = true;
            _txtPairing.Enabled = true;
        }

        private static string FormatEta(TransferProgress p)
        {
            if (p.SpeedBytesPerSecond <= 0) return "--:--";
            var remaining = TimeSpan.FromSeconds((p.TotalBytes - p.BytesTransferred) / p.SpeedBytesPerSecond);
            return string.Format("{0:mm\\:ss}", remaining);
        }

        private class ProgressCardInfo
        {
            public ProgressBar Bar;
            public Label Label;
        }

        private Panel GetOrCreateTcpCard(IPEndPoint ep)
        {
            Panel card;
            if (!_tcpCards.TryGetValue(ep, out card))
            {
                card = CreateTransferCard(_progressPanelS);
                _tcpCards[ep] = card;
            }
            return card;
        }

        /// <summary>Width each transfer card should have inside its host panel
        /// (accounts for the vertical scrollbar once it appears).</summary>
        private static int CardWidth(FlowLayoutPanel parent)
        {
            int w = parent.ClientSize.Width - (parent.VerticalScroll.Visible
                ? SystemInformation.VerticalScrollBarWidth + 8
                : 8);
            return Math.Max(40, w);
        }

        private Panel CreateTransferCard(FlowLayoutPanel parent)
        {
            // Size the card and its children to the host panel up front; the bar must
            // never be sized against the panel while the card itself is still at its
            // default width — that made every bar overflow its card.
            int cardWidth = CardWidth(parent);
            var panel = new Panel { Width = cardWidth, Height = 44, Margin = new Padding(2), BackColor = Color.White };
            var bar = new ProgressBar
            {
                Location = new Point(6, 5),
                Width = cardWidth - 12,
                Height = 16,
                Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100
            };
            var lbl = new Label
            {
                Location = new Point(6, 25),
                Width = cardWidth - 12,
                Height = 15,
                Text = "", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(85, 85, 85),
                Font = new Font("Segoe UI", 8f)
            };
            panel.Controls.Add(bar);
            panel.Controls.Add(lbl);
            panel.Tag = new ProgressCardInfo { Bar = bar, Label = lbl };
            parent.Controls.Add(panel);
            // Adding the card may have introduced the scrollbar (shrinking the client
            // area) — re-sync every card so all widths stay uniform
            SyncCardWidths(parent);
            return panel;
        }

        /// <summary>Keeps every transfer card, and the bar/label inside it, as wide as
        /// its host panel (minus scrollbar). Child widths are set explicitly — anchoring
        /// cannot recover from children that start wider than their card.</summary>
        private static void SyncCardWidths(FlowLayoutPanel parent)
        {
            int w = CardWidth(parent);
            for (int i = 0; i < parent.Controls.Count; i++)
            {
                var card = parent.Controls[i];
                card.Width = w;
                foreach (Control c in card.Controls)
                    c.Width = w - 12;
            }
        }

        private Panel GetOrCreateUdtCard(IPEndPoint ep)
        {
            Panel card;
            if (!_udtCards.TryGetValue(ep, out card))
            {
                card = CreateTransferCard(_progressPanelS);
                _udtCards[ep] = card;
            }
            return card;
        }

        private void UpdateCardProgress(Panel card, TransferProgress p)
        {
            var info = card.Tag as ProgressCardInfo;
            if (info == null) return;
            int pct = p.TotalBytes > 0 ? (int)(p.BytesTransferred * 100 / p.TotalBytes) : 0;
            if (p.TotalBytes > 0)
                info.Bar.Value = Math.Max(0, Math.Min(100, pct));
            string speed = Utils.FormatSize((long)p.SpeedBytesPerSecond) + "/s";
            info.Label.Text = string.Format("{0} | {1} | {2}% | {3}/{4} | {5} {6}",
                p.FileName, speed, pct,
                Utils.FormatSize(p.BytesTransferred), Utils.FormatSize(p.TotalBytes),
                L.EtaShort, FormatEta(p));
        }

        private void UpdateCardComplete(Panel card)
        {
            if (card.Tag == null) return; // already completed
            card.Tag = null;
            var timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) =>
            {
                timer.Stop(); timer.Dispose();
                if (!card.IsDisposed && card.Parent != null) { card.Parent.Controls.Remove(card); card.Dispose(); }
            };
            timer.Start();
        }

        private void BtnExportLog_Click(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title = L.ExportLogTitle;
                dlg.Filter = "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*";
                dlg.DefaultExt = "log";
                dlg.FileName = "TrFileTransfer_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".log";
                if (dlg.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        var lines = new string[_lstLog.Items.Count];
                        for (int i = 0; i < _lstLog.Items.Count; i++)
                            lines[i] = _lstLog.Items[i].ToString();
                        System.IO.File.WriteAllLines(dlg.FileName, lines, System.Text.Encoding.UTF8);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(L.ExportLogFailed + ex.Message, L.DlgError,
                            MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void AddLog(string msg)
        {
            _lstLog.Items.Add(msg);
            _lstLog.TopIndex = _lstLog.Items.Count - 1;
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

        // ---- Monitor mode ----

        private void StartMonitoring(string folderPath, string ip, int port)
        {
            _monitorCts = new System.Threading.CancellationTokenSource();
            _monitorSrcPort = (int)_numSrcPort.Value;
            _monitorSpeedBytesPerSec = (int)_numSpeed.Value * 1024;

            DisableClientInputs();
            _lblStatusC.Text = L.MonitorWaiting;
            AddLog(L.MonitorStarted(folderPath));

            string sentDir = Path.Combine(folderPath, "已发送文件");
            Directory.CreateDirectory(sentDir);

            System.Threading.Tasks.Task.Run(() => MonitorLoop(folderPath, ip, port, sentDir, _monitorCts.Token));
        }

        private void StopMonitoring()
        {
            if (_monitorCts != null)
            {
                try { _monitorCts.Cancel(); } catch { }
            }
            _btnCancel.Enabled = false;
            _lblStatusC.Text = L.MonitorStopped;
            AddLog(L.MonitorLogStopped);
            ResetClientUI();
        }

        private async System.Threading.Tasks.Task MonitorLoop(string folderPath, string ip, int port, string sentDir, System.Threading.CancellationToken ct)
        {
            using (var watcher = new FileSystemWatcher(folderPath))
            {
                // Scan existing files first
                foreach (var file in Directory.GetFiles(folderPath))
                {
                    lock (_monitorLock) { _monitorQueue.Add(file); }
                }

                watcher.NotifyFilter = NotifyFilters.FileName;
                watcher.Created += (s, e) =>
                {
                    lock (_monitorLock) { _monitorQueue.Add(e.FullPath); }
                };
                watcher.EnableRaisingEvents = true;

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
                        await System.Threading.Tasks.Task.Delay(500, ct);
                    }
                }
            }
        }

        private async System.Threading.Tasks.Task ProcessMonitoredFile(string filePath, string ip, int port, string sentDir, System.Threading.CancellationToken ct)
        {
            string fileName = Path.GetFileName(filePath);

            if (!await WaitForFileReady(filePath, ct))
            {
                this.Invoke((Action)(() =>
                    AddLog(L.MonitorFileNotReady(fileName))));
                lock (_monitorLock) { _monitorQueue.Add(filePath); }
                return;
            }

            bool success = false;
            try
            {
                var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();

                var card = (Panel)this.Invoke((Func<Panel>)(() => CreateTransferCard(_progressPanelC)));
                if (_rbClientTcp.Checked)
                {
                    var client = ClientFactory.CreateTcp(ip, port, filePath, _monitorSrcPort, _monitorSpeedBytesPerSec, _txtPairing.Text.Trim());
                    client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    client.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
                    client.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    client.OnTransferComplete += () => { tcs.TrySetResult(true); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    client.OnStopped += () => { tcs.TrySetResult(false); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    await client.SendAsync();
                }
                else
                {
                    var clientUdt = ClientFactory.CreateUdt(ip, port, filePath, _monitorSrcPort, _monitorSpeedBytesPerSec, _txtPairing.Text.Trim());
                    clientUdt.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    clientUdt.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
                    clientUdt.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    clientUdt.OnTransferComplete += () => { tcs.TrySetResult(true); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    clientUdt.OnStopped += () => { tcs.TrySetResult(false); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    await clientUdt.SendAsync();
                }

                success = await tcs.Task;
            }
            catch (Exception ex)
            {
                this.Invoke((Action)(() =>
                    AddLog(L.MonitorFileSendFailed(fileName, ex.Message))));
            }

            if (success)
            {
                string destPath = Utils.GetUniqueSavePath(sentDir, fileName);
                try { File.Move(filePath, destPath); } catch { }
                this.Invoke((Action)(() =>
                {
                    AddLog(L.MonitorFileSent(fileName));
                    _lblStatusC.Text = L.MonitorWaiting;
                    RememberDevice(ip, port, !_rbClientTcp.Checked);
                }));
            }
        }

        private async System.Threading.Tasks.Task<bool> WaitForFileReady(string filePath, System.Threading.CancellationToken ct)
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
                    this.Invoke((Action)(() =>
                        AddLog(L.MonitorFileWaiting(fileName, totalWaited / 1000))));
                }

                await System.Threading.Tasks.Task.Delay(PollIntervalMs, ct);
                totalWaited += PollIntervalMs;
            }

            return false;
        }

        /// <summary>Stops any active transfer before closing the window.</summary>
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Closing the window hides to tray; use the tray menu's Exit to quit
            if (!_trayExit && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                SaveConfig();
                this.Hide();
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
            base.OnFormClosing(e);
        }
    }

    /// <summary>Shared flat-style helpers so buttons look consistent across all forms.</summary>
    internal static class UiStyle
    {
        /// <summary>Accent (primary action) button — Windows blue, white text.</summary>
        public static void Primary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.FromArgb(0, 120, 215);
            b.ForeColor = Color.White;
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 110, 190);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(0, 90, 158);
            b.Cursor = Cursors.Hand;
        }

        /// <summary>Neutral button — white with a light border, darkens on hover.</summary>
        public static void Secondary(Button b)
        {
            b.FlatStyle = FlatStyle.Flat;
            b.BackColor = Color.White;
            b.ForeColor = Color.FromArgb(31, 31, 31);
            b.FlatAppearance.BorderColor = Color.FromArgb(204, 204, 204);
            b.FlatAppearance.BorderSize = 1;
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(240, 240, 240);
            b.FlatAppearance.MouseDownBackColor = Color.FromArgb(225, 225, 225);
            b.Cursor = Cursors.Hand;
        }
    }

    public class ResumeDialog : Form
    {
        public ResumeState SelectedState;
        public FolderResumeState SelectedFolderState;
        private ListBox _list;
        private Button _btnContinue, _btnDelete, _btnClearAll, _btnClose;
        private ResumeState[] _states;
        private FolderResumeState[] _folderStates;

        public ResumeDialog(ResumeState[] states, FolderResumeState[] folderStates)
        {
            _states = states ?? new ResumeState[0];
            _folderStates = folderStates ?? new FolderResumeState[0];
            Text = L.ResumeListTitle;
            ClientSize = new Size(560, 340);
            MinimumSize = new Size(460, 280);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            FillList();
            tlp.Controls.Add(_list, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            _btnClose = new Button { Text = L.CancelBtn, Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            buttons.Controls.Add(_btnClose);

            _btnClearAll = new Button { Text = L.ResumeClearAll, Width = 100 };
            UiStyle.Secondary(_btnClearAll);
            _btnClearAll.Click += BtnClearAll_Click;
            buttons.Controls.Add(_btnClearAll);

            _btnDelete = new Button { Text = L.ResumeDelete, Width = 100 };
            UiStyle.Secondary(_btnDelete);
            _btnDelete.Click += BtnDelete_Click;
            buttons.Controls.Add(_btnDelete);

            _btnContinue = new Button { Text = L.ResumeBtn, Width = 100 };
            UiStyle.Primary(_btnContinue);
            _btnContinue.Click += BtnContinue_Click;
            buttons.Controls.Add(_btnContinue);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);
            AcceptButton = _btnContinue;
        }

        private void BtnContinue_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            if (idx < _states.Length)
            {
                SelectedState = _states[idx];
            }
            else
            {
                int folderIdx = idx - _states.Length;
                if (folderIdx < _folderStates.Length)
                    SelectedFolderState = _folderStates[folderIdx];
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            if (idx < _states.Length)
            {
                if (_states[idx] != null) ResumeState.Delete(_states[idx].SessionId);
                var newStates = new System.Collections.Generic.List<ResumeState>();
                for (int i = 0; i < _states.Length; i++)
                {
                    if (i != idx && _states[i] != null) newStates.Add(_states[i]);
                }
                _states = newStates.ToArray();
            }
            else
            {
                int folderIdx = idx - _states.Length;
                if (folderIdx < _folderStates.Length)
                {
                    if (_folderStates[folderIdx] != null)
                        FolderResumeState.Delete(_folderStates[folderIdx].SessionId);
                    var newFolders = new System.Collections.Generic.List<FolderResumeState>();
                    for (int i = 0; i < _folderStates.Length; i++)
                    {
                        if (i != folderIdx && _folderStates[i] != null) newFolders.Add(_folderStates[i]);
                    }
                    _folderStates = newFolders.ToArray();
                }
            }
            RefreshList();
        }

        private void BtnClearAll_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != null) ResumeState.Delete(_states[i].SessionId);
            }
            for (int i = 0; i < _folderStates.Length; i++)
            {
                if (_folderStates[i] != null) FolderResumeState.Delete(_folderStates[i].SessionId);
            }
            _states = new ResumeState[0];
            _folderStates = new FolderResumeState[0];
            RefreshList();
        }

        private void RefreshList()
        {
            int selected = _list.SelectedIndex;
            _list.Items.Clear();
            FillList();
            if (selected >= 0 && selected < _list.Items.Count)
                _list.SelectedIndex = selected;
        }

        private void FillList()
        {
            foreach (var s in _states)
                if (s != null) _list.Items.Add(FormatState(s));
            foreach (var f in _folderStates)
                if (f != null) _list.Items.Add(FormatFolderState(f));
        }

        private static string FormatState(ResumeState s)
        {
            string progress = s.TotalSize > 0
                ? string.Format("{0:F1}%", 100.0 * s.SentBytes / s.TotalSize)
                : "?";
            return string.Format("{0} -> {1}:{2} [{3}] {4}",
                s.FileName, s.ServerIp, s.Port, progress,
                s.Created.ToLocalTime().ToString("g"));
        }

        private static string FormatFolderState(FolderResumeState f)
        {
            string progress = f.TotalBytes > 0
                ? string.Format("{0:F1}%", 100.0 * f.SentBytes / f.TotalBytes)
                : "?";
            return string.Format("{0}{1} ({2}) -> {3}:{4} [{5}] {6}",
                L.FolderTag, f.FolderName, f.FileCount, f.ServerIp, f.Port, progress,
                f.Created.ToLocalTime().ToString("g"));
        }
    }

    /// <summary>One queued send task (captured from the client panel).</summary>
    public class QueuedTask
    {
        public string FilePath;
        public bool IsFolder;
        public string ServerIp;
        public int Port;
        public bool IsUdp;
        public int SrcPort;
        public int Concurrency;
        public bool VerifyHash;
        public int SpeedLimit;

        public string DisplayName
        {
            get
            {
                string name = IsFolder ? Path.GetFileName(FilePath.TrimEnd('\\', '/')) : Path.GetFileName(FilePath);
                if (string.IsNullOrEmpty(name)) name = FilePath;
                return name;
            }
        }
    }

    /// <summary>Send-queue dialog: batch tasks executed serially, with optional retries and per-item timing.</summary>
    public class QueueDialog : Form
    {
        private readonly Func<QueuedTask> _capture;
        private readonly Func<string, bool, QueuedTask> _captureFor;
        private readonly Func<QueuedTask, Task<bool>> _executor;
        private readonly System.Collections.Generic.List<QueuedTask> _tasks
            = new System.Collections.Generic.List<QueuedTask>();
        private ListBox _list;
        private Button _btnAdd, _btnDelete, _btnClear, _btnStart, _btnClose;
        private NumericUpDown _numRetries;
        private bool _running;

        public QueueDialog(Func<QueuedTask> capture, Func<QueuedTask, Task<bool>> executor,
            System.Collections.Generic.IEnumerable<QueuedTask> initial = null,
            Func<string, bool, QueuedTask> captureFor = null)
        {
            _capture = capture;
            _captureFor = captureFor;
            _executor = executor;
            if (initial != null)
            {
                foreach (var t in initial)
                    _tasks.Add(t);
            }
            Text = L.QueueTitle;
            ClientSize = new Size(600, 400);
            MinimumSize = new Size(500, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            // Drag files/folders onto the dialog (form or the list filling it) to enqueue
            AllowDrop = true;
            DragEnter += QueueDialog_DragEnter;
            DragDrop += QueueDialog_DragDrop;
            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            for (int i = 0; i < _tasks.Count; i++)
                _list.Items.Add(FormatTask(_tasks[i]));
            // The list fills the client area — it is the drop target users actually hit
            _list.AllowDrop = true;
            _list.DragEnter += QueueDialog_DragEnter;
            _list.DragDrop += QueueDialog_DragDrop;
            tlp.Controls.Add(_list, 0, 0);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Padding(0, 6, 0, 0), BackColor = Color.Transparent };
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            _btnAdd = new Button { Text = L.QueueAdd, Width = 100 };
            UiStyle.Secondary(_btnAdd);
            _btnAdd.Click += BtnAdd_Click;
            bottom.Controls.Add(_btnAdd, 0, 0);

            _btnDelete = new Button { Text = L.QueueDelete, Width = 100, Margin = new Padding(6, 3, 3, 3) };
            UiStyle.Secondary(_btnDelete);
            _btnDelete.Click += BtnDelete_Click;
            bottom.Controls.Add(_btnDelete, 1, 0);

            var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0) };
            _btnClose = new Button { Text = L.CancelBtn, Width = 90 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            right.Controls.Add(_btnClose);

            _btnStart = new Button { Text = L.QueueStart, Width = 100 };
            UiStyle.Primary(_btnStart);
            _btnStart.Click += BtnStart_Click;
            right.Controls.Add(_btnStart);

            _btnClear = new Button { Text = L.QueueClear, Width = 100 };
            UiStyle.Secondary(_btnClear);
            _btnClear.Click += BtnClear_Click;
            right.Controls.Add(_btnClear);

            // RightToLeft flow renders last-added leftmost: visual order = label, numeric, buttons
            _numRetries = new NumericUpDown
            {
                Minimum = 0, Maximum = 5,
                Value = Math.Max(0, Math.Min(5, Config.GetInt("QueueRetries", 1))),
                Width = 44, Margin = new Padding(0, 4, 3, 3)
            };
            right.Controls.Add(_numRetries);

            var lblRetry = new Label { Text = L.QueueRetriesLabel, AutoSize = true, Margin = new Padding(9, 9, 3, 0), ForeColor = Color.FromArgb(68, 68, 68) };
            right.Controls.Add(lblRetry);

            bottom.Controls.Add(right, 2, 0);
            tlp.Controls.Add(bottom, 0, 1);
            Controls.Add(tlp);
            AcceptButton = _btnStart;
        }

        private void BtnAdd_Click(object sender, EventArgs e)
        {
            if (_running) return;
            var task = _capture();
            if (task == null)
            {
                MessageBox.Show(L.QueueInvalidTask, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            _tasks.Add(task);
            _list.Items.Add(FormatTask(task));
        }

        private void QueueDialog_DragEnter(object sender, DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void QueueDialog_DragDrop(object sender, DragEventArgs e)
        {
            if (_running || _captureFor == null) return;
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            int added = 0, skipped = 0;
            foreach (string path in files)
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path))
                {
                    skipped++;
                    continue;
                }
                var task = _captureFor(path, isDir);
                if (task != null)
                {
                    _tasks.Add(task);
                    _list.Items.Add(FormatTask(task));
                    added++;
                }
                else
                {
                    skipped++;
                }
            }
            if (skipped > 0)
                MessageBox.Show(this, L.DragDropSkipped(skipped), L.QueueTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            if (_running) return;
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _tasks.Count)
            {
                _tasks.RemoveAt(idx);
                _list.Items.RemoveAt(idx);
            }
        }

        private void BtnClear_Click(object sender, EventArgs e)
        {
            if (_running) return;
            _tasks.Clear();
            _list.Items.Clear();
        }

        private async void BtnStart_Click(object sender, EventArgs e)
        {
            if (_running || _tasks.Count == 0) return;
            _running = true;
            _btnStart.Enabled = false;
            _btnAdd.Enabled = false;
            _btnDelete.Enabled = false;
            _btnClear.Enabled = false;
            _numRetries.Enabled = false;
            Config.SetInt("QueueRetries", (int)_numRetries.Value);
            int retries = (int)_numRetries.Value;
            try
            {
                for (int i = 0; i < _tasks.Count; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = false;
                    for (int attempt = 0; ; attempt++)
                    {
                        string prefix = attempt > 0
                            ? "\u25B6 " + FormatTask(_tasks[i]) + " (" + L.RetryWord + " " + attempt + "/" + retries + ")"
                            : "\u25B6 " + FormatTask(_tasks[i]); // ▶
                        _list.Items[i] = prefix;
                        ok = await _executor(_tasks[i]);
                        if (ok || attempt >= retries) break;
                        await Task.Delay(500);
                    }
                    sw.Stop();
                    _list.Items[i] = (ok ? "\u2713 " : "\u2717 ") + FormatTask(_tasks[i])
                        + "  [" + sw.Elapsed.TotalSeconds.ToString("F1") + "s]"; // ✓ / ✗
                }
            }
            finally
            {
                _running = false;
                _btnStart.Enabled = true;
                _btnAdd.Enabled = true;
                _btnDelete.Enabled = true;
                _btnClear.Enabled = true;
                _numRetries.Enabled = true;
            }
        }

        private static string FormatTask(QueuedTask t)
        {
            return string.Format("{0} -> {1}:{2} ({3})", t.DisplayName, t.ServerIp, t.Port, t.IsUdp ? "UDT" : "TCP");
        }
    }

    /// <summary>LAN device scan dialog: lists known + discovered servers, picks one to connect to.</summary>
    public class DiscoveryDialog : Form
    {
        private readonly Action<DeviceInfo> _useDevice;
        private readonly System.Collections.Generic.List<DeviceInfo> _known;
        private ListBox _list;
        private Button _btnRescan, _btnUse, _btnClose;
        private DeviceInfo[] _devices = new DeviceInfo[0];
        private readonly System.Collections.Generic.List<DeviceInfo> _items
            = new System.Collections.Generic.List<DeviceInfo>();

        public DiscoveryDialog(Action<DeviceInfo> useDevice,
            System.Collections.Generic.IEnumerable<DeviceInfo> knownDevices = null)
        {
            _useDevice = useDevice;
            _known = new System.Collections.Generic.List<DeviceInfo>();
            if (knownDevices != null)
            {
                foreach (var d in knownDevices) _known.Add(d);
            }
            Text = L.ScanTitle;
            ClientSize = new Size(520, 360);
            MinimumSize = new Size(440, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            tlp.Controls.Add(_list, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            _btnClose = new Button { Text = L.CancelBtn, Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            buttons.Controls.Add(_btnClose);

            _btnUse = new Button { Text = L.ScanUse, Width = 100 };
            UiStyle.Primary(_btnUse);
            _btnUse.Click += BtnUse_Click;
            buttons.Controls.Add(_btnUse);

            _btnRescan = new Button { Text = L.ScanRescan, Width = 100 };
            UiStyle.Secondary(_btnRescan);
            _btnRescan.Click += async (s2, e2) => await ScanAsync();
            buttons.Controls.Add(_btnRescan);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);

            Shown += async (s2, e2) => await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _btnRescan.Enabled = false;
            _list.Items.Clear();
            _items.Clear();
            _list.Items.Add(L.Scanning);
            try
            {
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                var devices = await DiscoveryClient.Scan(dPort, 2000);
                _devices = devices;
                _list.Items.Clear();
                _items.Clear();

                // Merge live results into the known list by Ip+Port so each device
                // appears exactly once; a matching known entry is shown with live data
                bool[] merged = new bool[devices.Length];
                if (_known.Count > 0)
                {
                    _list.Items.Add(L.ScanKnownTitle);
                    _items.Add(new DeviceInfo { Ip = null }); // section header placeholder
                    for (int i = 0; i < _known.Count; i++)
                    {
                        DeviceInfo d = _known[i];
                        bool online = false;
                        for (int j = 0; j < devices.Length; j++)
                        {
                            if (!merged[j] && devices[j].Ip == d.Ip && devices[j].Port == d.Port)
                            {
                                d = devices[j];
                                online = true;
                                merged[j] = true;
                                break;
                            }
                        }
                        _items.Add(d);
                        _list.Items.Add(FormatDevice(d, online));
                    }
                }

                // Then live scan results not already shown in the known section
                _list.Items.Add(L.ScanOnlineTitle);
                _items.Add(new DeviceInfo { Ip = null }); // section header placeholder
                if (devices.Length == 0)
                {
                    _list.Items.Add(L.ScanEmpty);
                    _items.Add(new DeviceInfo { Ip = null }); // empty hint placeholder
                }
                else
                {
                    for (int i = 0; i < devices.Length; i++)
                    {
                        if (merged[i]) continue;
                        _items.Add(devices[i]);
                        _list.Items.Add(FormatDevice(devices[i], true));
                    }
                }
            }
            catch (Exception ex)
            {
                _list.Items.Clear();
                _list.Items.Add(L.ErrorPrefix + ex.Message);
            }
            finally
            {
                _btnRescan.Enabled = true;
            }
        }

        internal static string FormatDevice(DeviceInfo d, bool online)
        {
            string prot = (d.SupportsTcp ? "TCP" : "") + (d.SupportsUdt ? (d.SupportsTcp ? "+UDT" : "UDT") : "");
            string tag = online ? "" : "  [" + L.ScanOffline + "]";
            string pairTag = d.RequiresPairing ? "  " + L.ScanNeedsPairing : "";
            return string.Format("{0}  {1}:{2}  ({3}){4}{5}", d.Name, d.Ip, d.Port, prot, tag, pairTag);
        }

        private void BtnUse_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _items.Count && _items[idx].Ip != null)
            {
                _useDevice(_items[idx]);
                Close();
            }
        }
    }

    /// <summary>Recent received files dialog: list, open location, clear.</summary>
    public class RecentFilesDialog : Form
    {
        private readonly string[] _entries;
        private ListBox _list;
        private Button _btnOpen, _btnClose;

        public RecentFilesDialog(string[] entries)
        {
            _entries = entries;
            Text = L.RecentFiles;
            ClientSize = new Size(600, 380);
            MinimumSize = new Size(500, 300);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
            if (entries == null || entries.Length == 0)
            {
                _list.Items.Add(L.RecentFilesEmpty);
            }
            else
            {
                for (int i = entries.Length - 1; i >= 0; i--)
                    _list.Items.Add(FormatEntry(entries[i]));
            }
            _list.DoubleClick += (s2, e2) => BtnOpen_Click(null, null);
            tlp.Controls.Add(_list, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            _btnClose = new Button { Text = L.CancelBtn, Width = 110 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            buttons.Controls.Add(_btnClose);

            _btnOpen = new Button { Text = L.RecentOpen, Width = 110 };
            UiStyle.Secondary(_btnOpen);
            _btnOpen.Click += BtnOpen_Click;
            buttons.Controls.Add(_btnOpen);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);
            AcceptButton = _btnOpen;
        }

        private static string FormatEntry(string entry)
        {
            int idx = entry.LastIndexOf('|');
            if (idx <= 0) return entry;
            string path = entry.Substring(0, idx);
            long size;
            long.TryParse(entry.Substring(idx + 1), out size);
            return Path.GetFileName(path) + "  (" + Utils.FormatSize(size) + ")" + "  — " + path;
        }

        private void BtnOpen_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            string line = _list.Items[idx].ToString();
            int pipe = line.LastIndexOf("  — ");
            if (pipe < 0) return;
            string path = line.Substring(pipe + 4);
            if (File.Exists(path))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
            }
        }
    }

    /// <summary>Auto-update dialog: manifest URL + auto-check setting, check-now, and
    /// verified download that hands off to MainForm.ApplyUpdateAndRestart.</summary>
    public class UpdateDialog : Form
    {
        private Label _lblCurrent;
        private TextBox _txtUrl;
        private CheckBox _chkAuto;
        private Button _btnCheck, _btnInstall, _btnSkip, _btnClose;
        private Label _lblStatus;
        private ProgressBar _progress;
        private Label _lblNotes;
        private UpdateManifest _manifest;

        public UpdateDialog()
        {
            Text = L.UpdTitle;
            ClientSize = new Size(520, 300);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 20));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _lblCurrent = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51)
            };
            tlp.Controls.Add(_lblCurrent, 0, 0);
            tlp.SetColumnSpan(_lblCurrent, 2);

            var lblUrl = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleRight,
                ForeColor = Color.FromArgb(68, 68, 68),
                Text = L.UpdUrlLabel
            };
            _txtUrl = new TextBox { Dock = DockStyle.Fill, Text = Config.Get("UpdateUrl", MainForm.DefaultUpdateUrl) };
            _txtUrl.TextChanged += (s, e) => Config.Set("UpdateUrl", _txtUrl.Text.Trim());
            tlp.Controls.Add(lblUrl, 0, 1);
            tlp.Controls.Add(_txtUrl, 1, 1);

            _chkAuto = new CheckBox
            {
                AutoSize = true,
                Checked = Config.GetBool("AutoUpdateCheck", true),
                Text = L.UpdAutoCheck
            };
            _chkAuto.CheckedChanged += (s, e) => Config.SetBool("AutoUpdateCheck", _chkAuto.Checked);
            tlp.Controls.Add(_chkAuto, 1, 2);
            tlp.SetColumnSpan(_chkAuto, 2);

            var actionRow = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                WrapContents = false,
                BackColor = Color.White,
                Margin = new Padding(0, 4, 0, 0)
            };
            _btnCheck = new Button { Width = 110, Height = 28 };
            UiStyle.Primary(_btnCheck);
            _btnCheck.Click += BtnCheck_Click;
            _btnInstall = new Button { Width = 130, Height = 28, Enabled = false, Margin = new Padding(8, 0, 0, 0) };
            UiStyle.Primary(_btnInstall);
            _btnInstall.Click += BtnInstall_Click;
            _btnSkip = new Button { Width = 120, Height = 28, Visible = false, Margin = new Padding(8, 0, 0, 0) };
            UiStyle.Secondary(_btnSkip);
            _btnSkip.Click += BtnSkip_Click;
            actionRow.Controls.Add(_btnCheck);
            actionRow.Controls.Add(_btnInstall);
            actionRow.Controls.Add(_btnSkip);
            tlp.Controls.Add(actionRow, 1, 3);
            tlp.SetColumnSpan(actionRow, 2);

            _lblStatus = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(106, 106, 106),
                Text = ""
            };
            tlp.Controls.Add(_lblStatus, 0, 4);
            tlp.SetColumnSpan(_lblStatus, 2);

            _progress = new ProgressBar { Dock = DockStyle.Fill, Height = 16, Visible = false };
            tlp.Controls.Add(_progress, 0, 5);
            tlp.SetColumnSpan(_progress, 2);

            _lblNotes = new Label
            {
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.TopLeft,
                ForeColor = Color.FromArgb(68, 68, 68),
                Text = "",
                Padding = new Padding(2, 4, 0, 0)
            };
            tlp.Controls.Add(_lblNotes, 0, 6);
            tlp.SetColumnSpan(_lblNotes, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                BackColor = Color.White,
                Margin = new Padding(0)
            };
            _btnClose = new Button { Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (s, e) => Close();
            buttons.Controls.Add(_btnClose);
            tlp.Controls.Add(buttons, 0, 7);
            tlp.SetColumnSpan(buttons, 2);

            Controls.Add(tlp);
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            _lblCurrent.Text = L.UpdCurrentVersion(Updater.CurrentVersion.ToString());
            _btnCheck.Text = L.UpdCheckNow;
            _btnInstall.Text = L.UpdDownloadBtn;
            _btnSkip.Text = L.UpdSkip;
            _btnClose.Text = L.CancelBtn;
        }

        private async void BtnCheck_Click(object sender, EventArgs e)
        {
            string url = _txtUrl.Text.Trim();
            if (url.Length == 0)
            {
                _lblStatus.Text = L.UpdNoUrl;
                return;
            }
            Config.Set("UpdateUrl", url);
            _btnCheck.Enabled = false;
            _btnInstall.Enabled = false;
            _lblStatus.Text = L.UpdChecking;
            _lblNotes.Text = "";
            try
            {
                UpdateManifest m = await Updater.CheckAnyAsync(url, 15000).ConfigureAwait(true);
                _manifest = m;
                if (m.IsNewerThan(Updater.CurrentVersion))
                {
                    _lblStatus.Text = L.UpdAvailable(Updater.CurrentVersion, m.Version);
                    _lblNotes.Text = string.IsNullOrEmpty(m.Notes) ? "" : L.UpdNotesLabel + "\n" + m.Notes;
                    _btnInstall.Enabled = true;
                    _btnSkip.Visible = true;
                }
                else
                {
                    _lblStatus.Text = L.UpdLatest(Updater.CurrentVersion);
                    _btnSkip.Visible = false;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = L.UpdCheckFailed(ex.Message);
            }
            _btnCheck.Enabled = true;
        }

        /// <summary>Remembers the offered version so the silent startup check stops
        /// nagging about it. A manual "Check Now" still shows it.</summary>
        private void BtnSkip_Click(object sender, EventArgs e)
        {
            if (_manifest == null) return;
            Config.Set("SkippedVersion", _manifest.Version.ToString());
            Config.Save();
            Close();
        }

        private async void BtnInstall_Click(object sender, EventArgs e)
        {
            if (_manifest == null) return;
            _btnCheck.Enabled = false;
            _btnInstall.Enabled = false;
            _progress.Visible = true;
            _progress.Value = 0;
            string staged = Path.Combine(Updater.StagingDir, "TrFileTransfer.update.exe");
            try
            {
                await Updater.DownloadAsync(_manifest, staged, (read, total) =>
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            if (total > 0)
                            {
                                _progress.Maximum = 100;
                                _progress.Value = (int)Math.Min(100, read * 100 / total);
                                _lblStatus.Text = L.UpdDownloading(_progress.Value);
                            }
                            else
                            {
                                _progress.Maximum = (int)Math.Max(1, read);
                                _progress.Value = (int)read;
                                _lblStatus.Text = L.UpdDownloading("-");
                            }
                        });
                    }
                    catch (ObjectDisposedException) { }
                    catch (InvalidOperationException) { }
                }, 30000).ConfigureAwait(true);

                _lblStatus.Text = L.UpdDownloadDone;
                if (MessageBox.Show(this, L.UpdRestartPrompt, L.UpdTitle,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    var main = Owner as MainForm;
                    Close();
                    if (main != null) main.ApplyUpdateAndRestart(staged);
                }
                else
                {
                    _btnCheck.Enabled = true;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = L.UpdDownloadFailed(ex.Message);
                _progress.Visible = false;
                _btnCheck.Enabled = true;
                _btnInstall.Enabled = true;
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            Config.Save();
            base.OnFormClosing(e);
        }
    }

    /// <summary>Modal input dialog for sending a text message (0x06) to the server.</summary>
    public class TextSendDialog : Form
    {
        private readonly TextBox _txt;

        public string MessageText { get { return _txt.Text; } }

        public TextSendDialog()
        {
            Text = L.SendTextTitle;
            ClientSize = new Size(460, 240);
            MinimumSize = new Size(380, 200);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _txt = new TextBox { Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both };
            tlp.Controls.Add(_txt, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnCancel = new Button { Text = L.CancelBtn, Width = 100 };
            UiStyle.Secondary(btnCancel);
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            buttons.Controls.Add(btnCancel);

            var btnSend = new Button { Text = L.SendTextSend, Width = 100 };
            UiStyle.Primary(btnSend);
            btnSend.Click += BtnSend_Click;
            buttons.Controls.Add(btnSend);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);
            AcceptButton = btnSend;
        }

        private void BtnSend_Click(object sender, EventArgs e)
        {
            if (_txt.Text.Length == 0)
            {
                MessageBox.Show(this, L.SendTextEmpty, L.SendTextTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    /// <summary>Non-modal viewer for received text messages; accumulates messages while open.</summary>
    public class TextReceivedDialog : Form
    {
        private readonly TextBox _txt;

        public TextReceivedDialog(string firstMessage)
            : this(firstMessage, L.TextReceivedTitle)
        {
        }

        public TextReceivedDialog(string firstMessage, string title)
        {
            Text = title;
            ClientSize = new Size(460, 240);
            MinimumSize = new Size(380, 200);
            StartPosition = FormStartPosition.CenterParent;
            Font = new Font("Segoe UI", 9f);
            BackColor = Color.White;
            FormClosing += (s, e) => { Hide(); if (e.CloseReason == CloseReason.UserClosing) e.Cancel = true; };

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _txt = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                BackColor = Color.White
            };
            _txt.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "]\r\n" + firstMessage;
            tlp.Controls.Add(_txt, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            var btnClose = new Button { Text = L.CancelBtn, Width = 100 };
            UiStyle.Secondary(btnClose);
            btnClose.Click += (s, e) => Close();
            buttons.Controls.Add(btnClose);

            var btnCopy = new Button { Text = L.CopyBtn, Width = 100 };
            UiStyle.Primary(btnCopy);
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_txt.Text);
                    MessageBox.Show(this, L.Copied, L.TextReceivedTitle,
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch { }
            };
            buttons.Controls.Add(btnCopy);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);
        }

        /// <summary>Appends another received message with a timestamp separator.</summary>
        public void AppendMessage(string text)
        {
            _txt.AppendText("\r\n\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "]\r\n" + text);
        }
    }

    /// <summary>Multi-select device picker for fan-out: saved devices + live scan,
    /// checkbox list. Devices with a null IP are section placeholders and cannot be
    /// selected (guarded in the collect step).</summary>
    public class FanOutDialog : Form
    {
        private CheckedListBox _list;
        private Button _btnRescan, _btnSend, _btnClose;
        private readonly System.Collections.Generic.List<DeviceInfo> _items
            = new System.Collections.Generic.List<DeviceInfo>();
        private readonly System.Collections.Generic.List<DeviceInfo> _known
            = new System.Collections.Generic.List<DeviceInfo>();

        public System.Collections.Generic.List<DeviceInfo> SelectedDevices
            = new System.Collections.Generic.List<DeviceInfo>();

        public FanOutDialog(System.Collections.Generic.IEnumerable<DeviceInfo> knownDevices)
        {
            if (knownDevices != null)
            {
                foreach (var d in knownDevices) _known.Add(d);
            }
            Text = L.FanOutTitle;
            ClientSize = new Size(540, 400);
            MinimumSize = new Size(460, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            var tlp = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10) };
            tlp.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            _list = new CheckedListBox { Dock = DockStyle.Fill, IntegralHeight = false, CheckOnClick = true };
            tlp.Controls.Add(_list, 0, 0);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0),
                Padding = new Padding(0, 6, 0, 0)
            };
            _btnClose = new Button { Text = L.CancelBtn, Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (s, e) => Close();
            buttons.Controls.Add(_btnClose);

            _btnSend = new Button { Text = L.FanOutSend, Width = 110 };
            UiStyle.Primary(_btnSend);
            _btnSend.Click += BtnSend_Click;
            buttons.Controls.Add(_btnSend);

            _btnRescan = new Button { Text = L.ScanRescan, Width = 100 };
            UiStyle.Secondary(_btnRescan);
            _btnRescan.Click += async (s, e) => await ScanAsync();
            buttons.Controls.Add(_btnRescan);

            tlp.Controls.Add(buttons, 0, 1);
            Controls.Add(tlp);

            Shown += async (s, e) => await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _btnRescan.Enabled = false;
            _list.Items.Clear();
            _items.Clear();
            _list.Items.Add(L.Scanning);
            try
            {
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                var devices = await DiscoveryClient.Scan(dPort, 2000);
                _list.Items.Clear();
                _items.Clear();

                // Known section first (live results merged in), then the rest online
                bool[] merged = new bool[devices.Length];
                if (_known.Count > 0)
                {
                    _items.Add(new DeviceInfo { Ip = null });
                    _list.Items.Add(L.ScanKnownTitle, false);
                    foreach (var d in _known)
                    {
                        DeviceInfo shown = d;
                        for (int j = 0; j < devices.Length; j++)
                        {
                            if (!merged[j] && devices[j].Ip == d.Ip && devices[j].Port == d.Port)
                            {
                                shown = devices[j];
                                merged[j] = true;
                                break;
                            }
                        }
                        _items.Add(shown);
                        _list.Items.Add(DiscoveryDialog.FormatDevice(shown, true), false);
                    }
                }

                _items.Add(new DeviceInfo { Ip = null });
                _list.Items.Add(L.ScanOnlineTitle, false);
                bool any = false;
                for (int i = 0; i < devices.Length; i++)
                {
                    if (merged[i]) continue;
                    _items.Add(devices[i]);
                    _list.Items.Add(DiscoveryDialog.FormatDevice(devices[i], true), false);
                    any = true;
                }
                if (!any && _known.Count == 0)
                {
                    _items.Add(new DeviceInfo { Ip = null });
                    _list.Items.Add(L.ScanEmpty, false);
                }
            }
            catch (Exception ex)
            {
                _list.Items.Clear();
                _items.Clear();
                _list.Items.Add(L.ErrorPrefix + ex.Message);
            }
            finally
            {
                _btnRescan.Enabled = true;
            }
        }

        private void BtnSend_Click(object sender, EventArgs e)
        {
            SelectedDevices.Clear();
            for (int i = 0; i < _items.Count && i < _list.Items.Count; i++)
            {
                if (_list.GetItemChecked(i) && _items[i].Ip != null)
                    SelectedDevices.Add(_items[i]);
            }
            if (SelectedDevices.Count == 0)
            {
                MessageBox.Show(this, L.FanOutNoSelection, L.FanOutTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
