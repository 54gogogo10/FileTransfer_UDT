using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Known-devices manager: lists the remembered Name|Ip|Port|protocol entries,
    /// with use (fill the client panel), delete, and clear-all. Edits mutate the live
    /// list in place — the owner persists it via SaveKnownDevices() when the dialog closes.</summary>
    public class DevicesDialog : Window
    {
        private readonly List<DeviceInfo> _devices;
        private readonly Action<DeviceInfo> _onUse;
        private readonly ListBox _list = DlgUi.DarkList();

        public DevicesDialog(List<DeviceInfo> devices, Action<DeviceInfo> onUse)
        {
            _devices = devices;
            _onUse = onUse;

            DlgUi.Init(this, L.DevicesTitle, 520, 440, 440, 320);

            var btnUse = DlgUi.SecondaryMin(L.DevicesUse, 96);
            var btnDelete = DlgUi.SecondaryMin(L.DevicesDelete, 96);
            var btnClear = new Button { Style = DlgUi.Res<Style>("BtnDanger"), Content = L.DevicesClear, MinWidth = 96 };
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnUse.Click += (s, e) => UseSelected();
            btnDelete.Click += (s, e) => DeleteSelected();
            btnClear.Click += (s, e) => ClearAll();
            btnClose.Click += (s, e) => Close();
            _list.MouseDoubleClick += (s, e) => UseSelected();

            Repopulate();

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            var buttons = DlgUi.ButtonRowRight(btnUse, btnDelete, btnClear, btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private int SelectedIndex
        {
            get
            {
                int idx = _list.SelectedIndex;
                return (idx >= 0 && idx < _devices.Count) ? idx : -1;
            }
        }

        private void UseSelected()
        {
            int idx = SelectedIndex;
            if (idx < 0) return;
            _onUse(_devices[idx]);
            Close();
        }

        private void Repopulate()
        {
            _list.Items.Clear();
            if (_devices.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = L.DevicesEmpty,
                    Margin = new Thickness(8, 5, 0, 5),
                    Foreground = DlgUi.Res<Brush>("Brush.TextSecondary")
                };
                _list.Items.Add(hint);
                var container = _list.ItemContainerGenerator.ContainerFromItem(hint) as ListBoxItem;
                if (container != null) { container.IsEnabled = false; container.Focusable = false; }
                return;
            }
            foreach (var d in _devices)
            {
                string protos = d.SupportsTcp && d.SupportsUdt ? "TCP/UDT" : (d.SupportsUdt ? "UDT" : "TCP");
                var tb = new TextBlock
                {
                    Text = string.Format("{0}  —  {1}:{2}  ({3})",
                        string.IsNullOrEmpty(d.Name) || d.Name == "?" ? d.Ip : d.Name, d.Ip, d.Port, protos),
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                _list.Items.Add(tb);
            }
        }

        private void DeleteSelected()
        {
            int idx = SelectedIndex;
            if (idx < 0) return;
            string name = _devices[idx].Name;
            _devices.RemoveAt(idx);
            Repopulate();
            MessageBox.Show(this, L.DevicesDeleted(name), L.DevicesTitle,
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void ClearAll()
        {
            if (_devices.Count == 0) return;
            if (MessageBox.Show(this, L.DevicesClearConfirm, L.DevicesTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _devices.Clear();
            Repopulate();
        }
    }
}
