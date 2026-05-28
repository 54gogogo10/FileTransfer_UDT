using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows.Forms;

namespace TrFileTransfer
{
    /// <summary>Main application window — mode/protocol selector, server/client panels, progress, and log.</summary>
    public class MainForm : Form
    {
        // Language
        private ComboBox _cmbLang;

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

        private void InitializeComponent()
        {
            Text = L.AppTitle;
            ClientSize = new Size(620, 785);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Segoe UI", 9f);

            // Language selector
            _cmbLang = new ComboBox
            {
                Location = new Point(480, 16),
                Width = 110,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            _cmbLang.Items.Add("English");
            _cmbLang.Items.Add("中文");
            _cmbLang.SelectedIndex = 0;
            _cmbLang.SelectedIndexChanged += CmbLang_SelectedIndexChanged;

            // Server protocol checkboxes and client protocol radio buttons are placed inside their panels below

            // Server panel
            _gbServer = new GroupBox { Location = new Point(12, 60), Size = new Size(580, 155) };
            _lblBind = new Label { Location = new Point(15, 28), Width = 70, TextAlign = ContentAlignment.MiddleRight };
            _cmbBind = new ComboBox { Location = new Point(90, 24), Width = 125, DropDownStyle = ComboBoxStyle.DropDownList };
            _lblPortS = new Label { Location = new Point(220, 28), Width = 50, TextAlign = ContentAlignment.MiddleRight };
            _txtPortS = new TextBox { Text = "8080", Location = new Point(275, 25), Width = 55 };
            _chkServerTcp = new CheckBox { Text = "TCP", Location = new Point(340, 23), Width = 50, Checked = true };
            _chkServerUdt = new CheckBox { Text = "UDT", Location = new Point(392, 23), Width = 55 };
            _lblSaveDir = new Label { Location = new Point(15, 63), Width = 60, TextAlign = ContentAlignment.MiddleRight };
            _txtSaveDir = new TextBox { Text = Environment.GetFolderPath(Environment.SpecialFolder.Desktop), Location = new Point(80, 60), Width = 390 };
            _btnBrowseDir = new Button { Location = new Point(475, 59), Width = 85 };
            _btnBrowseDir.Click += BtnBrowseDir_Click;
            _btnStartServer = new Button { Location = new Point(55, 95), Width = 110, Height = 30 };
            _btnStartServer.Click += BtnStartServer_Click;
            _btnStopServer = new Button { Location = new Point(175, 95), Width = 110, Height = 30, Enabled = false };
            _btnStopServer.Click += BtnStopServer_Click;
            _gbServer.Controls.Add(_lblBind);
            _gbServer.Controls.Add(_cmbBind);
            _gbServer.Controls.Add(_lblPortS);
            _gbServer.Controls.Add(_txtPortS);
            _gbServer.Controls.Add(_chkServerTcp);
            _gbServer.Controls.Add(_chkServerUdt);
            _gbServer.Controls.Add(_lblSaveDir);
            _gbServer.Controls.Add(_txtSaveDir);
            _gbServer.Controls.Add(_btnBrowseDir);
            _gbServer.Controls.Add(_btnStartServer);
            _gbServer.Controls.Add(_btnStopServer);

            // Client panel
            _gbClient = new GroupBox { Location = new Point(12, 223), Size = new Size(580, 150) };
            _lblServerIp = new Label { Location = new Point(15, 28), Width = 90, TextAlign = ContentAlignment.MiddleRight };
            _txtServerIp = new TextBox { Text = "127.0.0.1", Location = new Point(110, 25), Width = 95 };
            _lblPortC = new Label { Location = new Point(212, 28), Width = 50, TextAlign = ContentAlignment.MiddleRight };
            _txtPortC = new TextBox { Text = "8080", Location = new Point(267, 25), Width = 55 };
            _rbClientTcp = new RadioButton { Text = "TCP", Location = new Point(335, 23), Width = 50, Checked = true };
            _rbClientUdt = new RadioButton { Text = "UDT", Location = new Point(387, 23), Width = 55 };
            _btnSend = new Button { Location = new Point(455, 20), Width = 110, Height = 30 };
            _btnSend.Click += BtnSend_Click;
            _lblFile = new Label { Location = new Point(15, 63), Width = 48, TextAlign = ContentAlignment.MiddleRight };
            _txtFile = new TextBox { Location = new Point(68, 60), Width = 290 };
            _btnBrowseFile = new Button { Location = new Point(365, 59), Width = 80 };
            _btnBrowseFile.Click += BtnBrowseFile_Click;
            _chkFolder = new CheckBox { Location = new Point(180, 93), Width = 100, TextAlign = ContentAlignment.MiddleLeft };
            _chkFolder.CheckedChanged += ChkFolder_CheckedChanged;
            _chkMonitor = new CheckBox { Location = new Point(70, 95), Width = 100, TextAlign = ContentAlignment.MiddleLeft };
            _chkMonitor.CheckedChanged += ChkMonitor_CheckedChanged;
            _lblConcurrency = new Label { Location = new Point(245, 93), Width = 95, TextAlign = ContentAlignment.MiddleRight };
            _numConcurrency = new NumericUpDown
            {
                Location = new Point(345, 93), Width = 50,
                Minimum = 1, Maximum = 16, Value = 4
            };
            _numConcurrency.ValueChanged += NumConcurrency_ValueChanged;
            _btnCancel = new Button { Location = new Point(455, 56), Width = 110, Height = 30, Enabled = false };
            _btnCancel.Click += BtnCancel_Click;
            _gbClient.Controls.Add(_lblServerIp);
            _gbClient.Controls.Add(_txtServerIp);
            _gbClient.Controls.Add(_lblPortC);
            _gbClient.Controls.Add(_txtPortC);
            _gbClient.Controls.Add(_rbClientTcp);
            _gbClient.Controls.Add(_rbClientUdt);
            _gbClient.Controls.Add(_lblFile);
            _gbClient.Controls.Add(_txtFile);
            _gbClient.Controls.Add(_btnBrowseFile);
            _gbClient.Controls.Add(_chkFolder);
            _gbClient.Controls.Add(_chkMonitor);
            _gbClient.Controls.Add(_lblConcurrency);
            _gbClient.Controls.Add(_numConcurrency);
            _gbClient.Controls.Add(_btnSend);
            _gbClient.Controls.Add(_btnCancel);

            // Server progress panel
            _gbProgressS = new GroupBox { Location = new Point(12, 381), Size = new Size(286, 205) };
            _progressPanelS = new FlowLayoutPanel
            {
                Location = new Point(5, 14), Width = 274, Height = 170,
                AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            _lblStatusS = new Label { Location = new Point(5, 186), Width = 274, Text = "" };
            _gbProgressS.Controls.Add(_progressPanelS);
            _gbProgressS.Controls.Add(_lblStatusS);

            // Client progress panel
            _gbProgressC = new GroupBox { Location = new Point(306, 381), Size = new Size(286, 205) };
            _progressPanelC = new FlowLayoutPanel
            {
                Location = new Point(5, 14), Width = 274, Height = 170,
                AutoScroll = true, FlowDirection = FlowDirection.TopDown,
                WrapContents = false
            };
            _lblStatusC = new Label { Location = new Point(5, 186), Width = 274, Text = L.Ready };
            _gbProgressC.Controls.Add(_progressPanelC);
            _gbProgressC.Controls.Add(_lblStatusC);

            // Log panel
            _gbLog = new GroupBox { Location = new Point(12, 596), Size = new Size(580, 170) };
            _lstLog = new ListBox { Location = new Point(10, 20), Width = 555, Height = 122, IntegralHeight = false, Font = new Font("Consolas", 8.5f) };
            _btnExportLog = new Button { Text = "...", Location = new Point(480, 143), Width = 85, Height = 22 };
            _btnExportLog.Click += BtnExportLog_Click;
            _gbLog.Controls.Add(_lstLog);
            _gbLog.Controls.Add(_btnExportLog);

            Controls.Add(_cmbLang);
            Controls.Add(_gbServer);
            Controls.Add(_gbClient);
            Controls.Add(_gbProgressS);
            Controls.Add(_gbProgressC);
            Controls.Add(_gbLog);
        }

        private void CmbLang_SelectedIndexChanged(object sender, EventArgs e)
        {
            L.IsChinese = _cmbLang.SelectedIndex == 1;
            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            Text = L.AppTitle;

            _gbServer.Text = L.ServerSettings;
            _lblBind.Text = L.BindAddress;
            _lblPortS.Text = L.Port;
            _lblSaveDir.Text = L.SaveTo;
            _btnBrowseDir.Text = L.Browse;
            _btnStartServer.Text = L.StartServer;
            _btnStopServer.Text = L.StopServer;

            _gbClient.Text = L.ClientSettings;
            _lblServerIp.Text = L.ServerIP;
            _lblPortC.Text = L.Port;
            _lblFile.Text = _chkMonitor.Checked ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnBrowseFile.Text = L.Browse;
            _btnSend.Text = _chkMonitor.Checked ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _btnCancel.Text = L.CancelBtn;
            _chkFolder.Text = L.FolderMode;
            _chkMonitor.Text = L.MonitorMode;
            _lblConcurrency.Text = L.ConcurrencyLabel;

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
            _numConcurrency.Value = Math.Max(1, Math.Min(64, Config.GetInt("Concurrency", 4)));
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
            if (_numConcurrency.Value > 64) _numConcurrency.Value = 64;
        }

        private void ChkMonitor_CheckedChanged(object sender, EventArgs e)
        {
            bool isMonitor = _chkMonitor.Checked;
            _lblFile.Text = isMonitor ? L.MonitorLabel : (_chkFolder.Checked ? L.FolderLabel : L.FileLabel);
            _btnSend.Text = isMonitor ? L.StartMonitor : (_chkFolder.Checked ? L.SendFolder : L.SendFile);
            _chkFolder.Enabled = !isMonitor;
            _txtFile.Text = "";
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
                var tcpServer = new TransferServer(bindAddr, port, saveDir);
                tcpServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                tcpServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
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
                var udtServer = new TransferUdtServer(bindAddr, port, saveDir);
                udtServer.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                udtServer.OnError += msg => this.Invoke((Action)(() => _lblStatusS.Text = L.ErrorPrefix + msg));
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

            // Normal send branch
            if (isFolder)
            {
                if (!Directory.Exists(path))
                {
                    MessageBox.Show(L.DirNotExist, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }
            else
            {
                if (!File.Exists(path))
                {
                    MessageBox.Show(L.FileNotFound, L.DlgError, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
            }

            DisableClientInputs();

            int concurrency = (int)_numConcurrency.Value;
            bool isTcp = _rbClientTcp.Checked;

            if (!isFolder && concurrency > 1)
            {
                // Multi-concurrent transfer
                var concurrent = new ConcurrentTransfer(ip, port, path, concurrency, isTcp);
                WireConcurrentEvents(concurrent);
                if (isFolder)
                    await concurrent.SendFolderAsync();
                else
                    await concurrent.SendAsync();
            }
            else if (isTcp)
            {
                _client = new TransferClient(ip, port, path);
                WireClientEvents(_client);
                if (isFolder)
                    await _client.SendFolderAsync(path);
                else
                    await _client.SendAsync();
            }
            else
            {
                _clientUdt = new TransferUdtClient(ip, port, path);
                WireUdtClientEvents(_clientUdt);
                if (isFolder)
                    await _clientUdt.SendFolderAsync(path);
                else
                    await _clientUdt.SendAsync();
            }

            try { ResetClientUI(); } catch { }
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
            var panel = new Panel { Width = parent.Width - 6, Height = 28, Margin = new Padding(0, 1, 0, 0) };
            var bar = new ProgressBar
            {
                Location = new Point(2, 1), Width = panel.Width - 6, Height = 14,
                Style = ProgressBarStyle.Continuous, Minimum = 0, Maximum = 100
            };
            var lbl = new Label
            {
                Location = new Point(4, 16), Width = panel.Width - 8, Height = 12,
                Text = "", AutoSize = false, TextAlign = ContentAlignment.MiddleLeft,
                Font = new Font("Segoe UI", 7f)
            };
            panel.Controls.Add(bar);
            panel.Controls.Add(lbl);
            panel.Tag = new ProgressCardInfo { Bar = bar, Label = lbl };
            parent.Controls.Add(panel);
            return panel;
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
            if (p.TotalBytes > 0)
                info.Bar.Value = Math.Max(0, Math.Min(100,
                    (int)(p.BytesTransferred * 100 / p.TotalBytes)));
            int pct = p.TotalBytes > 0 ? (int)(p.BytesTransferred * 100 / p.TotalBytes) : 0;
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
        }

        // ---- Monitor mode ----

        private void StartMonitoring(string folderPath, string ip, int port)
        {
            _monitorCts = new System.Threading.CancellationTokenSource();

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
                    var client = new TransferClient(ip, port, filePath);
                    client.OnLog += msg => this.Invoke((Action)(() => AddLog(msg)));
                    client.OnProgress += p => this.Invoke((Action)(() => UpdateCardProgress(card, p)));
                    client.OnError += msg => this.Invoke((Action)(() => AddLog(L.MonitorFileSendFailed(fileName, msg))));
                    client.OnTransferComplete += () => { tcs.TrySetResult(true); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    client.OnStopped += () => { tcs.TrySetResult(false); this.Invoke((Action)(() => UpdateCardComplete(card))); };
                    await client.SendAsync();
                }
                else
                {
                    var clientUdt = new TransferUdtClient(ip, port, filePath);
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
            base.OnFormClosing(e);
        }
    }
}
