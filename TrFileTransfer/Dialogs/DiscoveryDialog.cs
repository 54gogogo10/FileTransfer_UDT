using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>LAN device scan dialog: lists known + discovered servers, picks one to
    /// connect to. WPF port of DiscoveryDialog — rows are typed objects (no more
    /// Ip=null placeholder entries); string rows are section headers.</summary>
    public class DiscoveryDialog : Window
    {
        private readonly Action<DeviceInfo> _useDevice;
        private readonly List<DeviceInfo> _known = new List<DeviceInfo>();
        private readonly ListBox _list = DlgUi.DarkList();
        private readonly List<object> _items = new List<object>(); // DeviceInfo or header string
        private Button _btnRescan;

        public DiscoveryDialog(Action<DeviceInfo> useDevice,
            IEnumerable<DeviceInfo> knownDevices = null)
        {
            _useDevice = useDevice;
            if (knownDevices != null)
            {
                foreach (var d in knownDevices) _known.Add(d);
            }
            DlgUi.Init(this, L.ScanTitle, 520, 360, 440, 300);

            var btnUse = DlgUi.PrimaryMin(L.ScanUse, 100);
            _btnRescan = DlgUi.SecondaryMin(L.ScanRescan, 100);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 100);
            btnUse.Click += BtnUse_Click;
            _btnRescan.Click += async (s, e) => await ScanAsync();
            btnClose.Click += (s, e) => Close();

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            var buttons = DlgUi.ButtonRowRight(_btnRescan, btnUse, btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
            Loaded += async (s, e) => await ScanAsync();
        }

        private async Task ScanAsync()
        {
            _btnRescan.IsEnabled = false;
            _list.Items.Clear();
            _items.Clear();
            _list.Items.Add(L.Scanning);
            try
            {
                int dPort = Config.GetInt("DiscoveryPort", DiscoveryProtocol.DefaultPort);
                var devices = await DiscoveryClient.Scan(dPort, 2000);
                _list.Items.Clear();
                _items.Clear();

                // Merge live results into the known list by Ip+Port so each device
                // appears exactly once; a matching known entry is shown with live data
                bool[] merged = new bool[devices.Length];
                if (_known.Count > 0)
                {
                    AddRow(L.ScanKnownTitle, null);
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
                        AddRow(FormatDevice(d, online), d);
                    }
                }

                // Then live scan results not already shown in the known section
                AddRow(L.ScanOnlineTitle, null);
                if (devices.Length == 0)
                {
                    AddRow(L.ScanEmpty, null);
                }
                else
                {
                    for (int i = 0; i < devices.Length; i++)
                    {
                        if (merged[i]) continue;
                        AddRow(FormatDevice(devices[i], true), devices[i]);
                    }
                }
            }
            catch (Exception ex)
            {
                _list.Items.Clear();
                _items.Clear();
                AddRow(L.ErrorPrefix + ex.Message, null);
            }
            finally
            {
                _btnRescan.IsEnabled = true;
            }
        }

        private void AddRow(string text, DeviceInfo? device)
        {
            var tb = new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis };
            if (device == null)
            {
                // Section header / hint row — bold dim text, not selectable
                tb.FontWeight = FontWeights.Bold;
                tb.Foreground = DlgUi.Res<System.Windows.Media.Brush>("Brush.TextSecondary");
                tb.Margin = new Thickness(0, 6, 0, 2);
            }
            _items.Add(device != null ? (object)device : text);
            _list.Items.Add(tb);
            if (device == null)
            {
                var container = _list.ItemContainerGenerator.ContainerFromItem(tb) as ListBoxItem;
                if (container != null) { container.IsEnabled = false; container.Focusable = false; }
            }
        }

        internal static string FormatDevice(DeviceInfo d, bool online)
        {
            string prot = (d.SupportsTcp ? "TCP" : "") + (d.SupportsUdt ? (d.SupportsTcp ? "+UDT" : "UDT") : "");
            string tag = online ? "" : "  [" + L.ScanOffline + "]";
            string pairTag = d.RequiresPairing ? "  " + L.ScanNeedsPairing : "";
            return string.Format("{0}  {1}:{2}  ({3}){4}{5}", d.Name, d.Ip, d.Port, prot, tag, pairTag);
        }

        private void BtnUse_Click(object sender, RoutedEventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _items.Count && _items[idx] is DeviceInfo)
            {
                _useDevice((DeviceInfo)_items[idx]);
                Close();
            }
        }
    }
}
