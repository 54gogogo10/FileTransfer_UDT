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
    /// per-device volume breakdown read from the append-only stats log.</summary>
    public class StatsDialog : Window
    {
        public StatsDialog(StatsStore store)
        {
            DlgUi.Init(this, L.StatsTitle, 560, 480, 460, 380);

            var entries = store.LoadAll();
            var summary = StatsStore.Aggregate(entries, DateTime.Now);

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
            AddCell(totals, 1, 1, summary.TodayCount.ToString(CultureInfo.CurrentCulture), false);
            AddCell(totals, 1, 2, Utils.FormatSize(summary.TodayBytes), false);
            AddCell(totals, 2, 0, L.StatsWeek, false);
            AddCell(totals, 2, 1, summary.WeekCount.ToString(CultureInfo.CurrentCulture), false);
            AddCell(totals, 2, 2, Utils.FormatSize(summary.WeekBytes), false);
            AddCell(totals, 3, 0, L.StatsTotal, false);
            AddCell(totals, 3, 1, summary.AllCount.ToString(CultureInfo.CurrentCulture), false);
            AddCell(totals, 3, 2, Utils.FormatSize(summary.AllBytes), false);
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

            var list = DlgUi.DarkList();
            if (summary.Peers.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = L.StatsEmpty,
                    Margin = new Thickness(8, 5, 0, 5),
                    Foreground = DlgUi.Res<Brush>("Brush.TextSecondary")
                };
                list.Items.Add(hint);
            }
            else
            {
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
                    list.Items.Add(new TextBlock { Text = sb.ToString() });
                }
            }
            Grid.SetRow(list, 2);
            grid.Children.Add(list);

            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnClose.Click += (s, e) => Close();
            var buttons = DlgUi.ButtonRowRight(btnClose);
            Grid.SetRow(buttons, 3);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private static void AddCell(Grid grid, int row, int col, string text, bool header)
        {
            var tb = new TextBlock
            {
                Text = text,
                FontWeight = header ? FontWeights.Bold : FontWeights.Normal,
                Margin = new Thickness(0, 3, 8, 3)
            };
            if (col > 0) tb.TextAlignment = TextAlignment.Right;
            Grid.SetRow(tb, row);
            Grid.SetColumn(tb, col);
            grid.Children.Add(tb);
        }
    }
}
