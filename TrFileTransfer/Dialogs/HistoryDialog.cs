using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Transfer history browser over the append-only stats log: filterable by
    /// direction, newest first, with CSV export. Rows carry their StatsEntry as data;
    /// double-click opens the first file's location when the row has a saved path.</summary>
    public class HistoryDialog : Window
    {
        private const int MaxRows = 1000;

        private readonly ListBox _list = DlgUi.DarkList();
        private readonly ComboBox _cmbFilter = new ComboBox { Style = DlgUi.Res<Style>("CmbInput"), MinWidth = 110 };
        private readonly List<StatsEntry> _shown = new List<StatsEntry>();
        private readonly List<StatsEntry> _all = new List<StatsEntry>();
        private readonly StatsStore _store;
        private TextBlock _countLabel;

        public HistoryDialog(StatsStore store)
        {
            _store = store;
            DlgUi.Init(this, L.HistoryTitle, 640, 500, 520, 380);

            _all.AddRange(store.LoadAll());

            _cmbFilter.Items.Add(L.HistoryAll);   // 0 -> all
            _cmbFilter.Items.Add(L.StatsSentCol); // 1 -> sent
            _cmbFilter.Items.Add(L.StatsRecvCol); // 2 -> received
            _cmbFilter.SelectedIndex = 0;
            _cmbFilter.SelectionChanged += (s, e) => Repopulate();

            var btnExport = DlgUi.SecondaryMin(L.HistoryExportCsv, 110);
            var btnClear = new Button { Style = DlgUi.Res<Style>("BtnDanger"), Content = L.StatsClear, MinWidth = 96 };
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnExport.Click += BtnExport_Click;
            btnClear.Click += (s, e) => ClearAll();
            btnClose.Click += (s, e) => Close();
            _list.MouseDoubleClick += (s, e) => OpenSelectedLocation();

            var topRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            topRow.Children.Add(DlgUi.Label(L.HistoryFilterLabel));
            _cmbFilter.Margin = new Thickness(8, 0, 0, 0);
            topRow.Children.Add(_cmbFilter);
            _countLabel = new TextBlock
            {
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            topRow.Children.Add(_countLabel);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            Grid.SetRow(topRow, 0);
            grid.Children.Add(topRow);
            Grid.SetRow(_list, 1);
            grid.Children.Add(_list);
            var buttons = DlgUi.ButtonRowRight(btnExport, btnClear, btnClose);
            Grid.SetRow(buttons, 2);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;

            Repopulate();
        }

        /// <summary>Wipes the shared stats log (the stats dialog reads the same data)
        /// after confirmation, then refreshes the list in place.</summary>
        private void ClearAll()
        {
            if (MessageBox.Show(this, L.StatsClearConfirm, L.HistoryTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            _store.ClearAll();
            _all.Clear();
            Repopulate();
        }

        private void UpdateCountLabel()
        {
            _countLabel.Text = L.HistoryRowCount(_shown.Count, _all.Count);
        }

        private void Repopulate()
        {
            _list.Items.Clear();
            _shown.Clear();

            for (int i = _all.Count - 1; i >= 0 && _shown.Count < MaxRows; i--)
            {
                var e = _all[i];
                if (_cmbFilter.SelectedIndex == 1 && e.Direction != 'S') continue;
                if (_cmbFilter.SelectedIndex == 2 && e.Direction != 'R') continue;
                _shown.Add(e);

                var tb = new TextBlock
                {
                    Text = FormatEntry(e),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    FontFamily = DlgUi.Res<FontFamily>("Font.Mono")
                };
                _list.Items.Add(tb);
            }

            if (_shown.Count == 0)
            {
                var hint = new TextBlock
                {
                    Text = L.StatsEmpty,
                    Margin = new Thickness(8, 5, 0, 5),
                    Foreground = DlgUi.Res<Brush>("Brush.TextSecondary")
                };
                _list.Items.Add(hint);
                var container = _list.ItemContainerGenerator.ContainerFromItem(hint) as ListBoxItem;
                if (container != null) { container.IsEnabled = false; container.Focusable = false; }
            }

            // Filter changes reshape _shown — the count label must follow along
            UpdateCountLabel();
        }

        private static string FormatEntry(StatsEntry e)
        {
            string when = e.When.Date == DateTime.Now.Date
                ? e.When.ToString("HH:mm:ss")
                : e.When.ToString("MM-dd HH:mm");
            string arrow = e.Direction == 'S' ? "↑" : "↓";
            string detail = string.IsNullOrEmpty(e.Detail) ? "" : "  " + e.Detail;
            string speed = e.Seconds > 0.5
                ? "  " + Utils.FormatSize((long)(e.Bytes / e.Seconds)) + "/s"
                : "";
            string dur = e.Seconds >= 1
                ? "  " + (e.Seconds >= 60
                    ? (int)(e.Seconds / 60) + "m" + (int)(e.Seconds % 60) + "s"
                    : (int)Math.Ceiling(e.Seconds) + "s")
                : "";
            return string.Format("{0}  {1} {2}{3}  {4}  ({5}){6}{7}",
                when, arrow, e.Peer, detail, Utils.FormatSize(e.Bytes), e.Files, speed, dur);
        }

        /// <summary>Opens Explorer on the first received/sent file recorded for the row.</summary>
        private void OpenSelectedLocation()
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _shown.Count) return;
            string path = _shown[idx].Path;
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else if (Directory.Exists(path))
                    System.Diagnostics.Process.Start("explorer.exe", path);
            }
            catch { }
        }

        private void BtnExport_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = L.HistoryExportCsv,
                Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                DefaultExt = "csv",
                FileName = "TrFileTransfer_history_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv"
            };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.Append(L.HistoryCsvHeader).Append("\r\n");
                foreach (var e2 in _all)
                {
                    sb.Append(CsV(e2.When.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',');
                    sb.Append(CsV(e2.Direction == 'S' ? "S" : "R")).Append(',');
                    sb.Append(CsV(e2.Peer)).Append(',');
                    sb.Append(CsV(e2.Detail)).Append(',');
                    sb.Append(e2.Bytes.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(e2.Files.ToString(CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(e2.Seconds.ToString("F1", CultureInfo.InvariantCulture)).Append(',');
                    sb.Append(CsV(e2.Path)).Append("\r\n");
                }
                // BOM so Excel reads the UTF-8 detail/path columns correctly
                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));
                MessageBox.Show(this, L.HistoryCsvDone(dlg.FileName), L.HistoryTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.ExportLogFailed + ex.Message, L.DlgError,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>Quotes a CSV field only when it contains a separator, quote or newline.</summary>
        private static string CsV(string field)
        {
            string f = field ?? "";
            if (f.IndexOf(',') >= 0 || f.IndexOf('"') >= 0 || f.IndexOf('\n') >= 0)
                return "\"" + f.Replace("\"", "\"\"") + "\"";
            return f;
        }
    }
}
