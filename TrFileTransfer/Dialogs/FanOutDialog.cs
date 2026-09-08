using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Multi-select device picker for fan-out: saved devices + live scan +
    /// manually typed IP:port entries, checkbox list. WPF port of FanOutDialog —
    /// section headers are dim bold rows, devices are checkbox rows (checked state
    /// tracked per row, the DeviceInfo travels in the row's Tag).</summary>
    public class FanOutDialog : Window
    {
        private readonly List<DeviceInfo> _known = new List<DeviceInfo>();
        private readonly StackPanel _listPanel = new StackPanel();
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private TextBox _ipBox;
        private NumericBox _portBox;
        private Button _btnRescan;

        public List<DeviceInfo> SelectedDevices = new List<DeviceInfo>();

        public FanOutDialog(IEnumerable<DeviceInfo> knownDevices)
        {
            if (knownDevices != null)
            {
                foreach (var d in knownDevices) _known.Add(d);
            }
            DlgUi.Init(this, L.FanOutTitle, 540, 440, 460, 360);

            // Manual entry: type an IP:port that is not reachable by broadcast
            // (different subnet, firewall) but should still join the fan-out
            _ipBox = DlgUi.Input();
            _ipBox.Width = 150;
            _ipBox.PreviewTextInput += BlockIpChars;
            _ipBox.TextChanged += (s, e) => ValidateIpLive();
            var lblIp = DlgUi.Label(L.ServerIP);
            var lblPort = DlgUi.Label(L.Port);
            lblPort.Margin = new Thickness(10, 0, 6, 0);
            _portBox = new NumericBox { Min = 1, Max = 65535, Value = 8080, Width = 90 };
            var btnAddIp = DlgUi.SecondaryMin(L.FanOutAddBtn, 76);
            btnAddIp.Margin = new Thickness(10, 0, 0, 0);
            btnAddIp.Click += BtnAddIp_Click;
            var inputRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            lblIp.Margin = new Thickness(0, 0, 6, 0);
            inputRow.Children.Add(lblIp);
            inputRow.Children.Add(_ipBox);
            inputRow.Children.Add(lblPort);
            inputRow.Children.Add(_portBox);
            inputRow.Children.Add(btnAddIp);

            _btnRescan = DlgUi.SecondaryMin(L.ScanRescan, 100);
            var btnSend = DlgUi.PrimaryMin(L.FanOutSend, 110);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 100);
            _btnRescan.Click += async (s, e) => await ScanAsync();
            btnSend.Click += BtnSend_Click;
            btnClose.Click += (s, e) => Close();

            var scroll = new ScrollViewer
            {
                Content = _listPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(inputRow, 0);
            grid.Children.Add(inputRow);
            Grid.SetRow(scroll, 1);
            grid.Children.Add(scroll);
            var buttons = DlgUi.ButtonRowRight(_btnRescan, btnSend, btnClose);
            Grid.SetRow(buttons, 2);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
            Loaded += async (s, e) => await ScanAsync();
        }

        // ---- Manual IP entry ----

        private static void BlockIpChars(object sender, TextCompositionEventArgs e)
        {
            e.Handled = e.Text.Any(c => !char.IsDigit(c) && c != '.');
        }

        private static bool IsValidIpv4(string t)
        {
            return IPAddress.TryParse(t, out IPAddress ip) && ip.AddressFamily == AddressFamily.InterNetwork;
        }

        /// <summary>While typing, only judge complete-looking addresses (3+ dots);
        /// a failed live check paints the field red via the shared template trigger.</summary>
        private void ValidateIpLive()
        {
            string t = _ipBox.Text.Trim();
            if (t.Length == 0)
            {
                SetIpError(false);
                return;
            }
            bool ok = t.Count(c => c == '.') < 3 || IsValidIpv4(t);
            SetIpError(!ok);
        }

        private void SetIpError(bool invalid)
        {
            _ipBox.Tag = invalid ? "invalid" : null;
            _ipBox.ToolTip = invalid ? L.FieldIpInvalid : null;
        }

        private void BtnAddIp_Click(object sender, RoutedEventArgs e)
        {
            string ip = _ipBox.Text.Trim();
            if (!IsValidIpv4(ip))
            {
                SetIpError(true);
                return;
            }
            SetIpError(false);

            // Already listed (scan/known/manual)? Just check it
            foreach (var chk in _checks)
            {
                if (chk.Tag is DeviceInfo && ((DeviceInfo)chk.Tag).Ip == ip && ((DeviceInfo)chk.Tag).Port == _portBox.Value)
                {
                    chk.IsChecked = true;
                    _ipBox.Text = "";
                    return;
                }
            }

            var device = new DeviceInfo
            {
                Name = ip,
                Ip = ip,
                Port = _portBox.Value,
                SupportsTcp = true,
                SupportsUdt = true
            };
            AddCheck(L.FanOutManualTag + DiscoveryDialog.FormatDevice(device, true), device, isChecked: true);
            _ipBox.Text = "";
        }

        private async Task ScanAsync()
        {
            _btnRescan.IsEnabled = false;
            _listPanel.Children.Clear();
            _checks.Clear();
            AddHint(L.Scanning);
            try
            {
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                var devices = await DiscoveryClient.Scan(dPort, 2000);
                _listPanel.Children.Clear();
                _checks.Clear();

                // Known section first (live results merged in), then the rest online
                bool[] merged = new bool[devices.Length];
                if (_known.Count > 0)
                {
                    AddHint(L.ScanKnownTitle);
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
                        AddCheck(DiscoveryDialog.FormatDevice(shown, true), shown);
                    }
                }

                AddHint(L.ScanOnlineTitle);
                bool any = false;
                for (int i = 0; i < devices.Length; i++)
                {
                    if (merged[i]) continue;
                    AddCheck(DiscoveryDialog.FormatDevice(devices[i], true), devices[i]);
                    any = true;
                }
                if (!any && _known.Count == 0)
                {
                    AddHint(L.ScanEmpty);
                }
            }
            catch (Exception ex)
            {
                _listPanel.Children.Clear();
                _checks.Clear();
                AddHint(L.ErrorPrefix + ex.Message);
            }
            finally
            {
                _btnRescan.IsEnabled = true;
            }
        }

        private void AddHint(string text)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.Bold,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 8, 0, 4)
            };
            _listPanel.Children.Add(tb);
        }

        private void AddCheck(string text, DeviceInfo device, bool isChecked = false)
        {
            var chk = new CheckBox
            {
                Style = DlgUi.Res<Style>("ChkBox"),
                Content = text,
                Tag = device,
                IsChecked = isChecked,
                Margin = new Thickness(0, 3, 0, 3)
            };
            _checks.Add(chk);
            _listPanel.Children.Add(chk);
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            SelectedDevices.Clear();
            foreach (var chk in _checks)
            {
                if (chk.IsChecked == true && chk.Tag is DeviceInfo)
                    SelectedDevices.Add((DeviceInfo)chk.Tag);
            }
            if (SelectedDevices.Count == 0)
            {
                MessageBox.Show(this, L.FanOutNoSelection, L.FanOutTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
