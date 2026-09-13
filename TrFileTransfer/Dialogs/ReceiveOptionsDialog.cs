using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Server-side receive options: receive confirmation mode, IP filter,
    /// per-device subfolders, and duplicate-file handling. Edits Config directly;
    /// directory-layout options apply on the next server start.</summary>
    public class ReceiveOptionsDialog : Window
    {
        private readonly ComboBox _cmbConfirm = new ComboBox { Style = DlgUi.Res<Style>("CmbInput"), MinWidth = 140 };
        private readonly ComboBox _cmbIpFilter = new ComboBox { Style = DlgUi.Res<Style>("CmbInput"), MinWidth = 140 };
        private readonly TextBox _txtIpList = DlgUi.Input();
        private readonly CheckBox _chkPerDevice = new CheckBox { Style = DlgUi.Res<Style>("ChkBox") };
        private readonly ComboBox _cmbDuplicate = new ComboBox { Style = DlgUi.Res<Style>("CmbInput"), MinWidth = 140 };
        private readonly NumericBox _numRecvSpeed = new NumericBox { Min = 0, Max = 1048576, Width = 110 };
        private readonly CheckBox _chkVerifyContent = new CheckBox { Style = DlgUi.Res<Style>("ChkBox") };
        private readonly NumericBox _numHashCacheDays = new NumericBox { Min = 0, Max = 3650, Width = 110 };
        private readonly NumericBox _numPairingLength = new NumericBox { Min = 4, Max = 12, Width = 110 };

        public ReceiveOptionsDialog()
        {
            DlgUi.Init(this, L.RecvOptionsTitle, 470, 540, 420, 500);

            _cmbConfirm.Items.Add(L.ROConfirmOff);       // 0 -> off
            _cmbConfirm.Items.Add(L.ROConfirmUnknown);   // 1 -> unknown
            _cmbConfirm.Items.Add(L.ROConfirmAll);       // 2 -> all
            _cmbIpFilter.Items.Add(L.ROIpOff);           // 0 -> off
            _cmbIpFilter.Items.Add(L.ROIpAllow);         // 1 -> allow
            _cmbIpFilter.Items.Add(L.ROIpDeny);          // 2 -> deny
            _cmbDuplicate.Items.Add(L.RODupRename);      // 0 -> rename
            _cmbDuplicate.Items.Add(L.RODupSkip);        // 1 -> skip

            string confirm = Config.Get("ConfirmReceive", "off");
            _cmbConfirm.SelectedIndex = confirm == "all" ? 2 : (confirm == "unknown" ? 1 : 0);
            string filter = Config.Get("IpFilterMode", "off");
            _cmbIpFilter.SelectedIndex = filter == "allow" ? 1 : (filter == "deny" ? 2 : 0);
            _txtIpList.Text = Config.Get("IpFilterList", "");
            _txtIpList.ToolTip = L.ROIpHint;
            _chkPerDevice.IsChecked = Config.GetBool("PerDeviceFolder", false);
            _cmbDuplicate.SelectedIndex = Config.Get("DuplicateFiles", "rename") == "skip" ? 1 : 0;
            _numRecvSpeed.Value = Math.Max(0, Math.Min(1048576, Config.GetInt("RecvSpeedLimit", 0)));
            _chkVerifyContent.IsChecked = Config.GetBool("SyncVerifyContent", false);
            _chkVerifyContent.Content = L.ROVerifyContent;
            _chkVerifyContent.ToolTip = L.RORestartHint;
            _numHashCacheDays.Value = Math.Max(0, Math.Min(3650, Config.GetInt("HashCacheDays", 7)));
            _numPairingLength.Value = Math.Max(4, Math.Min(12, Config.GetInt("PairingLength", 6)));

            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 13; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddRow(grid, 0, DlgUi.Label(L.ROConfirmLabel), _cmbConfirm);
            AddRow(grid, 1, DlgUi.Label(L.ROIpFilterLabel), _cmbIpFilter);
            var ipHint = new TextBlock
            {
                Text = L.ROIpHint,
                FontSize = 11,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 3, 0, 0)
            };
            Grid.SetRow(_txtIpList, 2);
            _txtIpList.Margin = new Thickness(0, 4, 0, 0);
            grid.Children.Add(_txtIpList);
            Grid.SetRow(ipHint, 3);
            grid.Children.Add(ipHint);

            _chkPerDevice.Content = L.ROPerDevice;
            Grid.SetRow(_chkPerDevice, 4);
            _chkPerDevice.Margin = new Thickness(0, 12, 0, 0);
            grid.Children.Add(_chkPerDevice);

            AddRow(grid, 5, DlgUi.Label(L.RODuplicateLabel), _cmbDuplicate);
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            AddRow(grid, 6, DlgUi.Label(L.RORecvSpeedLabel), _numRecvSpeed);

            Grid.SetRow(_chkVerifyContent, 7);
            _chkVerifyContent.Margin = new Thickness(0, 12, 0, 0);
            grid.Children.Add(_chkVerifyContent);

            AddRow(grid, 8, DlgUi.Label(L.ROHashCacheDaysLabel), _numHashCacheDays);
            AddRow(grid, 9, DlgUi.Label(L.ROPairingLengthLabel), _numPairingLength);

            var note = new TextBlock
            {
                Text = L.RORestartHint,
                FontSize = 11,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(note, 9);
            grid.Children.Add(note);

            var btnOk = DlgUi.PrimaryMin(L.QueueStart, 96);
            var btnCancel = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnOk.Click += (s, e) => { Save(); DialogResult = true; };
            btnCancel.Click += (s, e) => DialogResult = false;
            var buttons = DlgUi.ButtonRowRight(btnOk, btnCancel);
            Grid.SetRow(buttons, 11);
            buttons.Margin = new Thickness(0, 16, 0, 0);
            grid.Children.Add(buttons);

            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        private static void AddRow(Grid grid, int row, FrameworkElement label, FrameworkElement control)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, row == 0 ? 0 : 10, 0, 0) };
            label.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(label);
            control.Margin = new Thickness(8, 0, 0, 0);
            control.VerticalAlignment = VerticalAlignment.Center;
            panel.Children.Add(control);
            Grid.SetRow(panel, row);
            grid.Children.Add(panel);
        }

        private void Save()
        {
            Config.Set("ConfirmReceive", _cmbConfirm.SelectedIndex == 2 ? "all" : (_cmbConfirm.SelectedIndex == 1 ? "unknown" : "off"));
            Config.Set("IpFilterMode", _cmbIpFilter.SelectedIndex == 1 ? "allow" : (_cmbIpFilter.SelectedIndex == 2 ? "deny" : "off"));
            Config.Set("IpFilterList", _txtIpList.Text.Trim());
            Config.SetBool("PerDeviceFolder", _chkPerDevice.IsChecked == true);
            Config.Set("DuplicateFiles", _cmbDuplicate.SelectedIndex == 1 ? "skip" : "rename");
            Config.SetInt("RecvSpeedLimit", _numRecvSpeed.Value);
            Config.SetBool("SyncVerifyContent", _chkVerifyContent.IsChecked == true);
            Config.SetInt("HashCacheDays", _numHashCacheDays.Value);
            Config.SetInt("PairingLength", _numPairingLength.Value);
            Config.Save();
        }
    }
}
