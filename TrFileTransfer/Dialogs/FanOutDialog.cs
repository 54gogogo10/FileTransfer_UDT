using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Multi-select device picker for fan-out: saved devices + live scan,
    /// checkbox list. WPF port of FanOutDialog — section headers are dim bold rows,
    /// devices are checkbox rows (checked state tracked per row).</summary>
    public class FanOutDialog : Window
    {
        private readonly List<DeviceInfo> _known = new List<DeviceInfo>();
        private readonly StackPanel _listPanel = new StackPanel();
        private readonly List<CheckBox> _checks = new List<CheckBox>();
        private Button _btnRescan;

        public List<DeviceInfo> SelectedDevices = new List<DeviceInfo>();

        public FanOutDialog(IEnumerable<DeviceInfo> knownDevices)
        {
            if (knownDevices != null)
            {
                foreach (var d in knownDevices) _known.Add(d);
            }
            DlgUi.Init(this, L.FanOutTitle, 540, 400, 460, 320);

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
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 0);
            grid.Children.Add(scroll);
            var buttons = DlgUi.ButtonRowRight(_btnRescan, btnSend, btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
            Loaded += async (s, e) => await ScanAsync();
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

        private void AddCheck(string text, DeviceInfo device)
        {
            var chk = new CheckBox
            {
                Style = DlgUi.Res<Style>("ChkBox"),
                Content = text,
                Tag = device,
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
