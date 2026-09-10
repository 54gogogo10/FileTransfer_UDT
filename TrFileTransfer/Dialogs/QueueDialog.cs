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
        private Button _btnAdd, _btnBatch, _btnDelete, _btnClear, _btnStart;
        private NumericBox _numRetries;
        private ComboBox _cmbAfter;
        private TextBox _txtAfterCmd;
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
            DlgUi.Init(this, L.QueueTitle, 800, 440, 740, 340);

            // Drag files/folders onto the dialog (form or the list filling it) to enqueue
            AllowDrop = true;
            DragEnter += QueueDialog_DragEnter;
            Drop += QueueDialog_DragDrop;
            _list.AllowDrop = true;
            _list.DragEnter += QueueDialog_DragEnter;
            _list.Drop += QueueDialog_DragDrop;

            _btnAdd = DlgUi.SecondaryMin(L.QueueAdd, 88);
            _btnBatch = DlgUi.SecondaryMin(L.QueueBatchAdd, 88);
            _btnDelete = DlgUi.SecondaryMin(L.QueueDelete, 88);
            _btnClear = DlgUi.SecondaryMin(L.QueueClear, 100);
            _btnStart = DlgUi.PrimaryMin(L.QueueStart, 100);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 90);
            _btnAdd.Click += BtnAdd_Click;
            _btnBatch.Click += BtnBatchAdd_Click;
            _btnDelete.Click += BtnDelete_Click;
            _btnClear.Click += BtnClear_Click;
            _btnStart.Click += BtnStart_Click;
            btnClose.Click += (s, e) => Close();

            _numRetries = new NumericBox
            {
                Min = 0,
                Max = 5,
                Value = Math.Max(0, Math.Min(5, Config.GetInt("QueueRetries", 1))),
                Width = 70
            };

            // After-queue action: shutdown / hibernate / arbitrary command — the
            // overnight batch workflow. Restored from Config; the command box
            // only participates when the "run command" mode is selected.
            _cmbAfter = new ComboBox { Style = DlgUi.Res<Style>("CmbInput"), MinWidth = 100 };
            _cmbAfter.Items.Add(L.AfterNone);
            _cmbAfter.Items.Add(L.AfterShutdown);
            _cmbAfter.Items.Add(L.AfterHibernate);
            _cmbAfter.Items.Add(L.AfterCommand);
            string savedAction = Config.Get("QueueAfterAction", "none");
            int actionIdx = savedAction == "shutdown" ? 1 : savedAction == "hibernate" ? 2 : savedAction == "command" ? 3 : 0;
            _cmbAfter.SelectedIndex = actionIdx;
            _txtAfterCmd = new TextBox
            {
                Style = DlgUi.Res<Style>("TxtInput"),
                Text = Config.Get("QueueAfterCommand", ""),
                MinWidth = 180,
                ToolTip = L.AfterCommandHint
            };
            UpdateAfterCmdState();
            _cmbAfter.SelectionChanged += (s, e) => UpdateAfterCmdState();

            var leftRow = new StackPanel { Orientation = Orientation.Horizontal };
            leftRow.Children.Add(_btnAdd);
            _btnBatch.Margin = new Thickness(8, 0, 0, 0);
            leftRow.Children.Add(_btnBatch);
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
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            Grid.SetRow(bottom, 1);
            bottom.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(bottom);

            // Own row so a long command never crowds the button rows on narrow dialogs
            var afterRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            afterRow.Children.Add(DlgUi.Label(L.QueueAfterLabel));
            _cmbAfter.Margin = new Thickness(6, 0, 0, 0);
            _cmbAfter.VerticalAlignment = VerticalAlignment.Center;
            afterRow.Children.Add(_cmbAfter);
            _txtAfterCmd.Margin = new Thickness(8, 0, 0, 0);
            _txtAfterCmd.VerticalAlignment = VerticalAlignment.Center;
            afterRow.Children.Add(_txtAfterCmd);
            Grid.SetRow(afterRow, 2);
            grid.Children.Add(afterRow);

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

        /// <summary>Batch-add: pick several files at once in a multi-select dialog;
        /// each becomes a queue task using the main panel's current settings.</summary>
        private void BtnBatchAdd_Click(object sender, RoutedEventArgs e)
        {
            if (_running || _captureFor == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Multiselect = true,
                Title = L.QueueBatchAdd
            };
            // Start where the main panel's current file lives, if any
            var current = _capture();
            if (current != null)
            {
                string dir = Path.GetDirectoryName(current.FilePath);
                if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
            }
            if (dlg.ShowDialog(this) != true) return;

            int added = 0, skipped = 0;
            foreach (string path in dlg.FileNames)
            {
                if (!File.Exists(path)) { skipped++; continue; }
                var task = _captureFor(path, false);
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
            if (added == 0 && skipped > 0)
                MessageBox.Show(this, L.DragDropSkipped(skipped), L.QueueTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
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

        private void UpdateAfterCmdState()
        {
            bool cmdMode = _cmbAfter.SelectedIndex == 3;
            _txtAfterCmd.IsEnabled = cmdMode;
            _txtAfterCmd.Opacity = cmdMode ? 1.0 : 0.45;
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
            _cmbAfter.IsEnabled = false;
            _txtAfterCmd.IsEnabled = false;
            Config.SetInt("QueueRetries", _numRetries.Value);
            string[] actionCodes = { "none", "shutdown", "hibernate", "command" };
            string action = actionCodes[Math.Max(0, Math.Min(3, _cmbAfter.SelectedIndex))];
            string command = _txtAfterCmd.Text.Trim();
            Config.Set("QueueAfterAction", action);
            Config.Set("QueueAfterCommand", command);
            int retries = _numRetries.Value;
            int failed = 0;
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
                    if (!ok) failed++;
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
                _cmbAfter.IsEnabled = true;
                UpdateAfterCmdState();
            }

            if (action != "none") RunAfterAction(action, command, failed);
        }

        /// <summary>Executes the chosen after-queue action once the batch is done —
        /// destructive choices pass through a confirmation first.</summary>
        private void RunAfterAction(string action, string command, int failed)
        {
            string label = action == "shutdown" ? L.AfterShutdown
                : action == "hibernate" ? L.AfterHibernate : L.AfterCommand;
            if (action == "command" && command.Length == 0) return;
            if (MessageBox.Show(this, L.QueueAfterConfirm(label, failed), L.QueueTitle,
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try
            {
                if (action == "shutdown")
                {
                    System.Diagnostics.Process.Start("shutdown.exe", "/s /t 60");
                    MessageBox.Show(this, L.QueueAfterShutdownStarted, L.QueueTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else if (action == "hibernate")
                {
                    System.Diagnostics.Process.Start("shutdown.exe", "/h");
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c " + command,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L.QueueAfterActionFailed(ex.Message), L.DlgError,
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string FormatTask(QueuedTask t)
        {
            return string.Format("{0} -> {1}:{2} ({3})", t.DisplayName, t.ServerIp, t.Port, t.IsUdp ? "UDT" : "TCP");
        }
    }
}
