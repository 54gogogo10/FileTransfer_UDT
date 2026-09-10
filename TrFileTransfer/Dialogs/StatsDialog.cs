using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Transfer statistics viewer: today / 7-day / all-time totals plus a
    /// per-device volume breakdown read from the append-only stats log. "Clear All"
    /// wipes the log (statistics and history share it) and refreshes in place.</summary>
    public class StatsDialog : Window
    {
        private readonly StatsStore _store;
        private readonly ListBox _list = DlgUi.DarkList();
        private TextBlock _countToday, _sizeToday, _countWeek, _sizeWeek, _countAll, _sizeAll;

        public StatsDialog(StatsStore store)
        {
            _store = store;
            DlgUi.Init(this, L.StatsTitle, 560, 480, 460, 380);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Totals table (period | count | volume)
            var totals = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            for (int i = 0; i < 3; i++)
            {
                totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                totals.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                totals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                totals.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }
            AddCell(totals, 0, 0, "", true);
            AddCell(totals, 0, 1, L.StatsCountCol, true);
            AddCell(totals, 0, 2, L.StatsDataCol, true);
            AddCell(totals, 1, 0, L.StatsToday, false);
            _countToday = AddCell(totals, 1, 1, "", false);
            _sizeToday = AddCell(totals, 1, 2, "", false);
            AddCell(totals, 2, 0, L.StatsWeek, false);
            _countWeek = AddCell(totals, 2, 1, "", false);
            _sizeWeek = AddCell(totals, 2, 2, "", false);
            AddCell(totals, 3, 0, L.StatsTotal, false);
            _countAll = AddCell(totals, 3, 1, "", false);
            _sizeAll = AddCell(totals, 3, 2, "", false);
            Grid.SetRow(totals, 0);
            grid.Children.Add(totals);

            var peerHeader = new TextBlock
            {
                Text = L.StatsDeviceCol,
                Style = (Style)DlgUi.Res<Style>("SectionTitle"),
                Margin = new Thickness(0, 4, 0, 6)
            };
            Grid.SetRow(peerHeader, 1);
            grid.Children.Add(peerHeader);

            Grid.SetRow(_list, 2);
            grid.Children.Add(_list);

            var btnClear = new Button { Style = DlgUi.Res<Style>("BtnDanger"), Content = L.StatsClear, MinWidth = 96 };
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnClear.Click += (s, e) => ClearAll();
            btnClose.Click += (s, e) => Close();
            var buttons = DlgUi.ButtonRowRight(btnClear, btnClose);
            Grid.SetRow(buttons, 3);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;

            Refresh();
        }

        private void ClearAll()
        {
            if (MessageBox.Show(this, L.StatsClearConfirm, L.StatsTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _store.ClearAll();
            Refresh();
        }

        /// <summary>Re-reads the store and repaints the totals table and device list.</summary>
        private void Refresh()
        {
            var summary = StatsStore.Aggregate(_store.LoadAll(), DateTime.Now);

            _countToday.Text = summary.TodayCount.ToString(CultureInfo.CurrentCulture);
            _sizeToday.Text = Utils.FormatSize(summary.TodayBytes);
            _countWeek.Text = summary.WeekCount.ToString(CultureInfo.CurrentCulture);
            _sizeWeek.Text = Utils.FormatSize(summary.WeekBytes);
            _countAll.Text = summary.AllCount.ToString(CultureInfo.CurrentCulture);
            _sizeAll.Text = Utils.FormatSize(summary.AllBytes);

            _list.Items.Clear();
            if (summary.Peers.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = L.StatsEmpty,
                    Margin = new Thickness(8, 5, 0, 5),
                    Foreground = DlgUi.Res<Brush>("Brush.TextSecondary")
                };
                _list.Items.Add(hint);
                return;
            }
            foreach (var ps in summary.Peers)
            {
                var sb = new StringBuilder();
                sb.Append(ps.Peer);
                sb.Append("   ↑ ");
                sb.Append(Utils.FormatSize(ps.Sent));
                sb.Append("   ↓ ");
                sb.Append(Utils.FormatSize(ps.Received));
                sb.Append("   (");
                sb.Append(ps.Count);
                sb.Append(")");
                _list.Items.Add(new TextBlock { Text = sb.ToString() });
            }
        }

        private static TextBlock AddCell(Grid grid, int row, int col, string text, bool header)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 3, 8, 3)
            };
            if (header) tb.Foreground = DlgUi.Res<Brush>("Brush.TextSecondary");
            if (col > 0) tb.TextAlignment = TextAlignment.Right;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
            return tb;
        }
    }
}
