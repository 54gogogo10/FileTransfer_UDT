using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Send-queue dialog: batch tasks executed serially, with optional retries
    /// and per-item timing. WPF port of QueueDialog (drag-drop enqueue included).</summary>
    public class QueueDialog : Window
    {
        private readonly Func<QueuedTask> _capture;
        private readonly Func<string, bool, QueuedTask> _captureFor;
        private readonly Func<QueuedTask, Task<bool>> _executor;
        private readonly List<QueuedTask> _tasks = new List<QueuedTask>();
        private readonly ListBox _list = DlgUi.DarkList();
        private Button _btnAdd, _btnDelete, _btnClear, _btnStart;
        private NumericBox _numRetries;
        private bool _running;

        public QueueDialog(Func<QueuedTask> capture, Func<QueuedTask, Task<bool>> executor,
            IEnumerable<QueuedTask> initial = null,
            Func<string, bool, QueuedTask> captureFor = null)
        {
            _capture = capture;
            _captureFor = captureFor;
            _executor = executor;
            if (initial != null)
            {
                foreach (var t in initial)
                    _tasks.Add(t);
            }
            DlgUi.Init(this, L.QueueTitle, 680, 420, 600, 340);

            // Drag files/folders onto the dialog (form or the list filling it) to enqueue
            AllowDrop = true;
            DragEnter += QueueDialog_DragEnter;
            Drop += QueueDialog_DragDrop;
            _list.AllowDrop = true;
            _list.DragEnter += QueueDialog_DragEnter;
            _list.Drop += QueueDialog_DragDrop;

            _btnAdd = DlgUi.SecondaryMin(L.QueueAdd, 100);
            _btnDelete = DlgUi.SecondaryMin(L.QueueDelete, 100);
            _btnClear = DlgUi.SecondaryMin(L.QueueClear, 100);
            _btnStart = DlgUi.PrimaryMin(L.QueueStart, 100);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 90);
            _btnAdd.Click += BtnAdd_Click;
            _btnDelete.Click += BtnDelete_Click;
            _btnClear.Click += BtnClear_Click;
            _btnStart.Click += BtnStart_Click;
            btnClose.Click += (s, e) => Close();

            _numRetries = new NumericBox
            {
                Min = 0,
                Max = 5,
                Value = Math.Max(0, Math.Min(5, Config.GetInt("QueueRetries", 1))),
                Width = 64
            };

            var leftRow = new StackPanel { Orientation = Orientation.Horizontal };
            leftRow.Children.Add(_btnAdd);
            _btnDelete.Margin = new Thickness(8, 0, 0, 0);
            leftRow.Children.Add(_btnDelete);

            var rightRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var lblRetry = DlgUi.Label(L.QueueRetriesLabel);
            lblRetry.Margin = new Thickness(0, 0, 6, 0);
            rightRow.Children.Add(lblRetry);
            rightRow.Children.Add(_numRetries);
            _btnClear.Margin = new Thickness(10, 0, 0, 0);
            rightRow.Children.Add(_btnClear);
            _btnStart.Margin = new Thickness(8, 0, 0, 0);
            rightRow.Children.Add(_btnStart);
            btnClose.Margin = new Thickness(8, 0, 0, 0);
            rightRow.Children.Add(btnClose);

            // Dock instead of grid columns: on a narrow dialog the two groups
            // butt together instead of the right group overlapping the left one
            var bottom = new DockPanel { LastChildFill = false };
            DockPanel.SetDock(leftRow, Dock.Left);
            bottom.Children.Add(leftRow);
            DockPanel.SetDock(rightRow, Dock.Right);
            bottom.Children.Add(rightRow);

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            Grid.SetRow(bottom, 1);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(bottom);

            Content = grid;
            for (int i = 0; i < _tasks.Count; i++)
                _list.Items.Add(FormatTask(_tasks[i]));
        }

        private void BtnAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            var task = _capture();
            if (task == null)
            {
                MessageBox.Show(this, L.QueueInvalidTask, L.DlgError, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            _tasks.Add(task);
            _list.Items.Add(FormatTask(task));
        }

        private void QueueDialog_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
                ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void QueueDialog_DragDrop(object sender, DragEventArgs e)
        {
            if (_running || _captureFor == null) return;
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            int added = 0, skipped = 0;
            foreach (string path in files)
            {
                bool isDir = Directory.Exists(path);
                if (!isDir && !File.Exists(path))
                {
                    skipped++;
                    continue;
                }
                var task = _captureFor(path, isDir);
                if (task != null)
                {
                    _tasks.Add(task);
                    _list.Items.Add(FormatTask(task));
                    added++;
                }
                else
                {
                    skipped++;
                }
            }
            if (skipped > 0)
                MessageBox.Show(this, L.DragDropSkipped(skipped), L.QueueTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            int idx = _list.SelectedIndex;
            if (idx >= 0 && idx < _tasks.Count)
            {
                _tasks.RemoveAt(idx);
                _list.Items.RemoveAt(idx);
            }
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            if (_running) return;
            _tasks.Clear();
            _list.Items.Clear();
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _tasks.Count == 0) return;
            _running = true;
            _btnStart.IsEnabled = false;
            _btnAdd.IsEnabled = false;
            _btnDelete.IsEnabled = false;
            _btnClear.IsEnabled = false;
            _numRetries.IsEnabled = false;
            Config.SetInt("QueueRetries", _numRetries.Value);
            int retries = _numRetries.Value;
            try
            {
                for (int i = 0; i < _tasks.Count; i++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    bool ok = false;
                    for (int attempt = 0; ; attempt++)
                    {
                        string prefix = attempt > 0
                            ? "\u25B6 " + FormatTask(_tasks[i]) + " (" + L.RetryWord + " " + attempt + "/" + retries + ")"
                            : "\u25B6 " + FormatTask(_tasks[i]); // ▶
                        _list.Items[i] = prefix;
                        ok = await _executor(_tasks[i]);
                        if (ok || attempt >= retries) break;
                        await Task.Delay(500);
                    }
                    sw.Stop();
                    _list.Items[i] = (ok ? "\u2713 " : "\u2717 ") + FormatTask(_tasks[i])
                        + "  [" + sw.Elapsed.TotalSeconds.ToString("F1") + "s]"; // ✓ / ✗
                }
            }
            finally
            {
                _running = false;
                _btnStart.IsEnabled = true;
                _btnAdd.IsEnabled = true;
                _btnDelete.IsEnabled = true;
                _btnClear.IsEnabled = true;
                _numRetries.IsEnabled = true;
            }
        }

        private static string FormatTask(QueuedTask t)
        {
            return string.Format("{0} -> {1}:{2} ({3})", t.DisplayName, t.ServerIp, t.Port, t.IsUdp ? "UDT" : "TCP");
        }
    }
}
