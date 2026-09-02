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
        private int _monitorSpeedBytesPerSec;
        private DiscoveryServer _discoveryServer;
        private NotifyIcon _notifyIcon;
        private Button _btnOpenDir;
        private Button _btnRecent;
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

        /// <summary>Initializes the form, populates NIC list, and applies default language.</summary>
        public MainForm()
        {
            InitializeComponent();
            Config.Load();
            PopulateBindAddresses();
            ApplyLanguage();
            ApplyConfig();
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

            // Header row: app title on the left, language selector on the right
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.White, Margin = new Padding(0) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 116));
            _lblHeader = new Label
            {
                AutoSize = false,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(51, 51, 51)
            };
            _cmbLang = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, Margin = new Padding(3, 2, 0, 0) };
            _cmbLang.Items.Add("English");
            _cmbLang.Items.Add("中文");
            _cmbLang.SelectedIndex = 0;
            _cmbLang.SelectedIndexChanged += CmbLang_SelectedIndexChanged;
            header.Controls.Add(_lblHeader, 0, 0);
            header.Controls.Add(_cmbLang, 1, 0);
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
            _btnStartServer = new Button { Width = 108, Height = 28, Margin = new Padding(0, 3, 8, 3) };
            UiStyle.Primary(_btnStartServer);
            _btnStartServer.Click += BtnStartServer_Click;
            _btnStopServer = new Button { Width = 108, Height = 28, Margin = new Padding(0, 3, 8, 3), Enabled = false };
            UiStyle.Secondary(_btnStopServer);
            _btnStopServer.Click += BtnStopServer_Click;
            _btnOpenDir = new Button { Width = 96, Height = 28, Margin = new Padding(0, 3, 8, 3) };
            UiStyle.Secondary(_btnOpenDir);
            _btnOpenDir.Click += BtnOpenDir_Click;
            _btnRecent = new Button { Width = 96, Height = 28, Margin = new Padding(0, 3, 0, 3) };
            UiStyle.Secondary(_btnRecent);
            _btnRecent.Click += BtnRecent_Click;
            var serverButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            serverButtons.Controls.Add(_btnStartServer);
            serverButtons.Controls.Add(_btnStopServer);
            serverButtons.Controls.Add(_btnOpenDir);
            serverButtons.Controls.Add(_btnRecent);
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
            _chkVerifyHash = new CheckBox { AutoSize = true, Margin = new Padding(2, 8, 16, 3) };
            _chkVerifyHash.Checked = Config.GetBool("VerifyHash", false);
            var optionsRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            optionsRow.Controls.Add(_chkMonitor);
            optionsRow.Controls.Add(_chkFolder);
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
            _btnQueue = new Button { Width = 96, Height = 26, Margin = new Padding(2, 3, 8, 3) };
            UiStyle.Secondary(_btnQueue);
            _btnQueue.Click += BtnQueue_Click;
            _btnResumeList = new Button { Width = 72, Height = 26, Margin = new Padding(2, 3, 0, 3) };
            UiStyle.Secondary(_btnResumeList);
            _btnResumeList.Click += BtnResumeList_Click;
            var actionRow = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, BackColor = Color.White, Margin = new Padding(0) };
            actionRow.Controls.Add(_btnScan);
            actionRow.Controls.Add(_btnQueue);
            actionRow.Controls.Add(_btnResumeList);

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
            _chkMonitor.Checked = Config.GetBool("MonitorMode", false);
            _numConcurrency.Value = Math.Max(1, Math.Min(8, Config.GetInt("Concurrency", 4)));
            _numSrcPort.Value = Math.Max(0, Math.Min(65535, Config.GetInt("SrcPort", 0)));
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
            Config.SetBool("MonitorMode", _chkMonitor.Checked);
            Config.SetInt("Concurrency", (int)_numConcurrency.Value);
            Config.SetInt("SrcPort", (int)_numSrcPort.Value);
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
            _txtFile.Text = "";
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
            using (var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask, tasks))
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
            if (validStates.Count == 0)
            {
                MessageBox.Show(L.ResumeListEmpty, L.ResumeListTitle,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new ResumeDialog(validStates.ToArray()))
            {
                if (dlg.ShowDialog() == DialogResult.OK && dlg.SelectedState != null)
                {
                    var s = dlg.SelectedState;
                    _txtServerIp.Text = s.ServerIp;
                    _txtPortC.Text = s.Port.ToString();
                    _txtFile.Text = s.FilePath;
                    _chkFolder.Checked = false;
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
            using (var dlg = new QueueDialog(CaptureQueuedTask, ExecuteQueuedTask))
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

            DisableServerInputs();
            _serverCount = 0;

            if (_chkServerTcp.Checked)
            {
                bool tcpStarted = false;
                var tcpServer = new TransferServer(bindAddr, port, GetArchiveDir(saveDir));
                tcpServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                tcpServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
                tcpServer.OnFileReceived += (path, size) => this.Invoke((Action)(() => OnFileReceived(path, size)));
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
                udtServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                udtServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
                udtServer.OnFileReceived += (path, size) => this.Invoke((Action)(() => OnFileReceived(path, size)));
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
                    _chkServerTcp.Checked, _chkServerUdt.Checked);
            }
        }

        private void DisableServerInputs()
        {
            _chkServerTcp.Enabled = false;
            _chkServerUdt.Enabled = false;
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
        }

        private void OnServerStarted()
        {
            _btnStartServer.Enabled = false;
            _btnStopServer.Enabled = true;
            _lblStatusS.Text = L.Listening;
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

            // Resume is only valid for single-file, single-connection sends, and only
            // when the selected file still matches the one recorded in the resume state.
            Guid? resumeSession = null;
            if (!isFolder && concurrency == 1 && _pendingResumeSession.HasValue)
            {
                var st = ResumeState.Load(_pendingResumeSession.Value);
                if (st != null && string.Equals(st.FilePath, path, StringComparison.OrdinalIgnoreCase))
                    resumeSession = _pendingResumeSession;
                else
                    _pendingResumeSession = null;
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
                    WireConcurrentEvents(concurrent);
                    if (isFolder)
                        await concurrent.SendFolderAsync();
                    else
                        await concurrent.SendAsync();
                }
                else if (isTcp)
                {
                    _client = srcPort > 0
                        ? new TransferClient(ip, port, path, srcPort, 4194304, speedLimit)
                        : new TransferClient(ip, port, path, 0, 4194304, speedLimit);
                    WireClientEvents(_client);
                    if (isFolder)
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
                    _clientUdt = srcPort > 0
                        ? new TransferUdtClient(ip, port, path, srcPort, 4194304, speedLimit)
                        : new TransferUdtClient(ip, port, path, 0, 4194304, speedLimit);
                    WireUdtClientEvents(_clientUdt);
                    if (isFolder)
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

        private Panel CreateTransferCard(FlowLayoutPanel parent)
        {
            var panel = new Panel { Height = 44, Margin = new Padding(2), BackColor = Color.White };
            var bar = new ProgressBar
            {
                Location = new Point(6, 5),
                Width = Math.Max(40, parent.ClientSize.Width - 20),
                Height = 16,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100
            };
            var lbl = new Label
            {
                Location = new Point(6, 25),
                Width = Math.Max(40, parent.ClientSize.Width - 20),
                Height = 15,
                Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
                Text = "", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = Color.FromArgb(85, 85, 85),
                Font = new Font("Segoe UI", 8f)
            };
            panel.Controls.Add(bar);
            panel.Controls.Add(lbl);
            panel.Tag = new ProgressCardInfo { Bar = bar, Label = lbl };
            parent.Controls.Add(panel);
            return panel;
        }

        /// <summary>Keeps every transfer card as wide as its host panel (minus scrollbar).</summary>
        private static void SyncCardWidths(FlowLayoutPanel parent)
        {
            int w = parent.ClientSize.Width - (parent.VerticalScroll.Visible
                ? SystemInformation.VerticalScrollBarWidth + 8
                : 8);
            if (w < 40) w = 40;
            for (int i = 0; i < parent.Controls.Count; i++)
                parent.Controls[i].Width = w;
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
            info.Label.Text = string.Format("{0} | {1} | {2}% | {3}/{4}",
                p.FileName, speed, pct,
                Utils.FormatSize(p.BytesTransferred), Utils.FormatSize(p.TotalBytes));
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
                    var client = _monitorSrcPort > 0
                        ? new TransferClient(ip, port, filePath, _monitorSrcPort, 4194304, _monitorSpeedBytesPerSec)
                        : new TransferClient(ip, port, filePath, 0, 4194304, _monitorSpeedBytesPerSec);
                    client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    client.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
                    client.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    client.OnTransferComplete += () => { tcs.TrySetResult(true); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    client.OnStopped += () => { tcs.TrySetResult(false); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    await client.SendAsync();
                }
                else
                {
                    var clientUdt = _monitorSrcPort > 0
                        ? new TransferUdtClient(ip, port, filePath, _monitorSrcPort, 4194304, _monitorSpeedBytesPerSec)
                        : new TransferUdtClient(ip, port, filePath, 0, 4194304, _monitorSpeedBytesPerSec);
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
        private ListBox _list;
        private Button _btnContinue, _btnDelete, _btnClearAll, _btnClose;
        private ResumeState[] _states;

        public ResumeDialog(ResumeState[] states)
        {
            _states = states;
            Text = L.ResumeListTitle;
            Size = new Size(520, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            _list = new ListBox
            {
                Location = new Point(12, 12), Width = 480, Height = 200,
                IntegralHeight = false
            };
            for (int i = 0; i < states.Length; i++)
            {
                var s = states[i];
                if (s == null) continue;
                string progress = s.TotalSize > 0
                    ? string.Format("{0:F1}%", 100.0 * s.SentBytes / s.TotalSize)
                    : "?";
                _list.Items.Add(string.Format("{0} -> {1}:{2} [{3}] {4}",
                    s.FileName, s.ServerIp, s.Port, progress,
                    s.Created.ToLocalTime().ToString("g")));
            }
            Controls.Add(_list);

            _btnContinue = new Button { Text = L.ResumeBtn, Location = new Point(12, 220), Width = 100 };
            UiStyle.Primary(_btnContinue);
            _btnContinue.Click += BtnContinue_Click;
            Controls.Add(_btnContinue);

            _btnDelete = new Button { Text = L.ResumeDelete, Location = new Point(120, 220), Width = 100 };
            UiStyle.Secondary(_btnDelete);
            _btnDelete.Click += BtnDelete_Click;
            Controls.Add(_btnDelete);

            _btnClearAll = new Button { Text = L.ResumeClearAll, Location = new Point(228, 220), Width = 100 };
            UiStyle.Secondary(_btnClearAll);
            _btnClearAll.Click += BtnClearAll_Click;
            Controls.Add(_btnClearAll);

            _btnClose = new Button { Text = L.CancelBtn, Location = new Point(370, 220), Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            Controls.Add(_btnClose);
        }

        private void BtnContinue_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _states.Length)
            {
                SelectedState = _states[idx];
                DialogResult = DialogResult.OK;
                Close();
            }
        }

        private void BtnDelete_Click(object sender, EventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _states.Length && _states[idx] != null)
            {
                ResumeState.Delete(_states[idx].SessionId);
                _list.Items.RemoveAt(idx);
                // rebuild state array without the deleted item
                var newStates = new System.Collections.Generic.List<ResumeState>();
                for (int i = 0; i < _states.Length; i++)
                {
                    if (i != idx && _states[i] != null) newStates.Add(_states[i]);
                }
                _states = newStates.ToArray();
            }
        }

        private void BtnClearAll_Click(object sender, EventArgs e)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != null) ResumeState.Delete(_states[i].SessionId);
            }
            _list.Items.Clear();
            _states = new ResumeState[0];
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

    /// <summary>Send-queue dialog: batch tasks executed serially.</summary>
    public class QueueDialog : Form
    {
        private readonly Func<QueuedTask> _capture;
        private readonly Func<QueuedTask, Task<bool>> _executor;
        private readonly System.Collections.Generic.List<QueuedTask> _tasks
            = new System.Collections.Generic.List<QueuedTask>();
        private ListBox _list;
        private Button _btnAdd, _btnDelete, _btnClear, _btnStart, _btnClose;
        private bool _running;

        public QueueDialog(Func<QueuedTask> capture, Func<QueuedTask, Task<bool>> executor,
            System.Collections.Generic.IEnumerable<QueuedTask> initial = null)
        {
            _capture = capture;
            _executor = executor;
            if (initial != null)
            {
                foreach (var t in initial)
                    _tasks.Add(t);
            }
            Text = L.QueueTitle;
            Size = new Size(560, 360);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            _list = new ListBox
            {
                Location = new Point(12, 12), Width = 520, Height = 240,
                IntegralHeight = false
            };
            for (int i = 0; i < _tasks.Count; i++)
                _list.Items.Add(FormatTask(_tasks[i]));
            Controls.Add(_list);

            _btnAdd = new Button { Text = L.QueueAdd, Location = new Point(12, 262), Width = 100 };
            UiStyle.Secondary(_btnAdd);
            _btnAdd.Click += BtnAdd_Click;
            Controls.Add(_btnAdd);

            _btnDelete = new Button { Text = L.QueueDelete, Location = new Point(120, 262), Width = 100 };
            UiStyle.Secondary(_btnDelete);
            _btnDelete.Click += BtnDelete_Click;
            Controls.Add(_btnDelete);

            _btnClear = new Button { Text = L.QueueClear, Location = new Point(228, 262), Width = 100 };
            UiStyle.Secondary(_btnClear);
            _btnClear.Click += BtnClear_Click;
            Controls.Add(_btnClear);

            _btnStart = new Button { Text = L.QueueStart, Location = new Point(336, 262), Width = 100 };
            UiStyle.Primary(_btnStart);
            _btnStart.Click += BtnStart_Click;
            Controls.Add(_btnStart);

            _btnClose = new Button { Text = L.CancelBtn, Location = new Point(444, 262), Width = 90 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            Controls.Add(_btnClose);
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
            try
            {
                for (int i = 0; i < _tasks.Count; i++)
                {
                    _list.Items[i] = "\u25B6 " + FormatTask(_tasks[i]); // ▶
                    bool ok = await _executor(_tasks[i]);
                    _list.Items[i] = (ok ? "\u2713 " : "\u2717 ") + FormatTask(_tasks[i]); // ✓ / ✗
                }
            }
            finally
            {
                _running = false;
                _btnStart.Enabled = true;
                _btnAdd.Enabled = true;
                _btnDelete.Enabled = true;
                _btnClear.Enabled = true;
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
            Size = new Size(480, 320);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            _list = new ListBox
            {
                Location = new Point(12, 12), Width = 440, Height = 200,
                IntegralHeight = false
            };
            Controls.Add(_list);

            _btnRescan = new Button { Text = L.ScanRescan, Location = new Point(12, 222), Width = 100 };
            UiStyle.Secondary(_btnRescan);
            _btnRescan.Click += async (s2, e2) => await ScanAsync();
            Controls.Add(_btnRescan);

            _btnUse = new Button { Text = L.ScanUse, Location = new Point(120, 222), Width = 100 };
            UiStyle.Primary(_btnUse);
            _btnUse.Click += BtnUse_Click;
            Controls.Add(_btnUse);

            _btnClose = new Button { Text = L.CancelBtn, Location = new Point(228, 222), Width = 100 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            Controls.Add(_btnClose);

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

        private static string FormatDevice(DeviceInfo d, bool online)
        {
            string prot = (d.SupportsTcp ? "TCP" : "") + (d.SupportsUdt ? (d.SupportsTcp ? "+UDT" : "UDT") : "");
            string tag = online ? "" : "  [" + L.ScanOffline + "]";
            return string.Format("{0}  {1}:{2}  ({3}){4}", d.Name, d.Ip, d.Port, prot, tag);
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
            Size = new Size(560, 340);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            Font = new Font("Segoe UI", 9f);

            _list = new ListBox
            {
                Location = new Point(12, 12), Width = 520, Height = 230,
                IntegralHeight = false
            };
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
            Controls.Add(_list);

            _btnOpen = new Button { Text = L.RecentOpen, Location = new Point(12, 252), Width = 110 };
            UiStyle.Secondary(_btnOpen);
            _btnOpen.Click += BtnOpen_Click;
            Controls.Add(_btnOpen);

            _btnClose = new Button { Text = L.CancelBtn, Location = new Point(130, 252), Width = 110 };
            UiStyle.Secondary(_btnClose);
            _btnClose.Click += (__, ___) => Close();
            Controls.Add(_btnClose);
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
}
