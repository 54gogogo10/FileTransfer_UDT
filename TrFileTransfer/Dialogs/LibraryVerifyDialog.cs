using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Integrity self-check ("scrub") over the receive directory/directories:
    /// re-hashes every file and compares against the digests the app recorded when it
    /// received them, listing anything that no longer matches (edited or corrupted after
    /// arrival). Several roots (one per protocol tab) are scanned sequentially in one
    /// pass and the reports are merged. Runs on a worker thread with a progress bar; a
    /// second click stops the scan. Entries from the digest cache follow its normal
    /// rules — TTL-expired or never-seen files show as "no record".</summary>
    public class LibraryVerifyDialog : Window
    {
        private readonly TextBox _txtDir = DlgUi.Input();
        private readonly ProgressBar _bar = new ProgressBar { Style = DlgUi.Res<Style>("BarModern"), Minimum = 0, Maximum = 1 };
        private readonly TextBlock _status = DlgUi.Label("");
        private readonly ListBox _list = DlgUi.DarkList();
        private readonly Button _btnStart = DlgUi.PrimaryMin(L.LibVerifyStart, 96);
        private CancellationTokenSource _cts;
        private bool _running;

        public LibraryVerifyDialog(IList<string> dirs)
        {
            DlgUi.Init(this, L.LibVerifyTitle, 560, 480, 460, 380);

            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 6; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var dirRow = new StackPanel { Orientation = Orientation.Horizontal };
            dirRow.Children.Add(DlgUi.Label(L.LibVerifyDirLabel));
            _txtDir.Text = string.Join("; ", dirs);
            _txtDir.MinWidth = 380;
            _txtDir.Margin = new Thickness(8, 0, 0, 0);
            dirRow.Children.Add(_txtDir);
            Grid.SetRow(dirRow, 0);
            grid.Children.Add(dirRow);

            var hint = new TextBlock
            {
                Text = L.LibVerifyIdle,
                FontSize = 11,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 8, 0, 0),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hint, 1);
            grid.Children.Add(hint);

            _bar.Height = 6;
            _bar.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(_bar, 2);
            grid.Children.Add(_bar);

            _status.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_status, 3);
            grid.Children.Add(_status);

            _list.Margin = new Thickness(0, 8, 0, 0);
            Grid.SetRow(_list, 4);
            grid.Children.Add(_list);

            _btnStart.Click += delegate { ToggleRun(); };
            var btnClose = DlgUi.SecondaryMin(L.LibVerifyClose, 96);
            btnClose.Click += delegate { if (_cts != null) _cts.Cancel(); DialogResult = false; };
            var buttons = DlgUi.ButtonRowRight(_btnStart, btnClose);
            buttons.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(buttons, 5);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private void ToggleRun()
        {
            if (_running)
            {
                if (_cts != null) _cts.Cancel();
                return;
            }

            // The box holds one or more roots separated by ';' (same convention as the
            // bind-address config) — the user may edit it down to a single directory
            List<string> roots = _txtDir.Text.Split(';')
                .Select(d => d.Trim())
                .Where(d => d.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(System.IO.Directory.Exists)
                .ToList();
            if (roots.Count == 0)
            {
                _status.Text = L.DirNotExist;
                return;
            }

            _list.Items.Clear();
            _bar.Value = 0;
            _running = true;
            _btnStart.Content = L.LibVerifyStop;
            _cts = new CancellationTokenSource();
            var cb = new WireCallbacks();
            var token = _cts.Token;
            Task.Run(delegate
            {
                try
                {
                    // Sequential scan over every root; reports merged into one
                    var combined = new VerifyReport();
                    for (int i = 0; i < roots.Count; i++)
                    {
                        string root = roots[i];
                        string prefix = roots.Count > 1 ? "[" + (i + 1) + "/" + roots.Count + "] " : "";
                        cb.Progress = delegate(TransferProgress p)
                        {
                            Dispatcher.BeginInvoke(new Action(delegate
                            {
                                _bar.Value = p.TotalBytes > 0 ? (double)p.BytesTransferred / p.TotalBytes : 0;
                                _status.Text = prefix + p.FileName;
                            }));
                        };
                        VerifyReport part = LibraryVerifier.Verify(root, FileHashCache.ForServer, cb, token);
                        combined.Verified.AddRange(part.Verified);
                        combined.Changed.AddRange(part.Changed);
                        combined.Unverified.AddRange(part.Unverified);
                        combined.Skipped.AddRange(part.Skipped);
                    }
                    return combined;
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, token).ContinueWith(delegate(Task<VerifyReport> t)
            {
                // Marshaled explicitly: ContinueWith without a scheduler runs on the
                // thread pool, where the first UI touch threw and the whole results
                // rendering silently died (the scan finished, the dialog showed nothing).
                _running = false;
                _btnStart.Content = L.LibVerifyStart;
                _bar.Value = 1;
                var report = t.Status == TaskStatus.RanToCompletion ? t.Result : null;
                if (report == null)
                {
                    _status.Text = L.LibVerifyCancelled;
                    return;
                }

                foreach (var p in report.Changed)
                    _list.Items.Add(L.LibVerifyTagChanged + p);
                foreach (var p in report.Unverified)
                    _list.Items.Add(L.LibVerifyTagUnverified + p);
                foreach (var p in report.Skipped)
                    _list.Items.Add(L.LibVerifyTagSkipped + p);
                _status.Text = report.Changed.Count == 0 && report.Unverified.Count == 0 && report.Skipped.Count == 0
                    ? L.LibVerifyAllOk + " (" + report.Verified.Count + ")"
                    : L.CliVerifySummary(report.Verified.Count, report.Changed.Count, report.Unverified.Count, report.Skipped.Count);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }
    }
}
