using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Lists the server-side 0x04 sync/resume session directories (detected by the
    /// "name.8hex" naming the protocol derives) and lets the operator reset one. A reset
    /// deletes the server's copy of the session data, so the next sync starts over from
    /// scratch — the escape hatch when a session is suspected of having drifted (the digest
    /// cache entries for the deleted files are dropped with them). Double-protected: a
    /// two-step confirm dialog states exactly what will be deleted, in bytes.</summary>
    public class SyncSessionsDialog : Window
    {
        private class SessionRow
        {
            public string Dir;
            public string DisplayName;
            public long Bytes;
            public int Files;
        }

        private readonly string _saveDir;
        private readonly ListBox _list = DlgUi.DarkList();
        private readonly Button _btnReset = DlgUi.SecondaryMin(L.SyncReset, 96);

        public SyncSessionsDialog(string saveDir)
        {
            _saveDir = saveDir;
            DlgUi.Init(this, L.SyncTitle, 620, 440, 500, 360);

            var grid = new Grid { Margin = new Thickness(16) };
            for (int i = 0; i < 4; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

            var hint = new TextBlock
            {
                Text = L.SyncHint,
                FontSize = 11,
                Foreground = DlgUi.Res<System.Windows.Media.Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(hint, 0);
            grid.Children.Add(hint);

            Grid.SetRow(_list, 1);
            _list.Margin = new Thickness(0, 0, 0, 8);
            grid.Children.Add(_list);

            _btnReset.IsEnabled = false;
            _btnReset.Click += delegate { ResetSelected(); };
            var btnClose = DlgUi.PrimaryMin(L.LibVerifyClose, 96);
            btnClose.Click += delegate { DialogResult = true; };
            var buttons = DlgUi.ButtonRowRight(_btnReset, btnClose);
            Grid.SetRow(buttons, 3);
            grid.Children.Add(buttons);

            Content = grid;
            Reload();
        }

        private void Reload()
        {
            _list.Items.Clear();
            _btnReset.IsEnabled = false;
            if (string.IsNullOrEmpty(_saveDir) || !Directory.Exists(_saveDir))
                return;

            foreach (var dir in Directory.GetDirectories(_saveDir, "*.*"))
            {
                string name = Path.GetFileName(dir);
                int dot = name.LastIndexOf('.');
                if (dot <= 0 || dot != name.Length - 9) continue; // "<name>.<8 hex>"
                string suffix = name.Substring(dot + 1);
                bool hex = true;
                foreach (char c in suffix)
                    if (!Uri.IsHexDigit(c)) hex = false;
                if (!hex) continue;

                long bytes = 0;
                int files = 0;
                try
                {
                    foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { bytes += new FileInfo(f).Length; files++; } catch { }
                    }
                }
                catch { }

                var row = new SessionRow { Dir = dir, DisplayName = name, Bytes = bytes, Files = files };
                string label = name + "  —  " + files + " " + L.SyncFilesWord + ", " + Utils.FormatSize(bytes);
                _list.Items.Add(new ListBoxItem { Content = label, Tag = row });
            }
        }

        private void ResetSelected()
        {
            var item = _list.SelectedItem as ListBoxItem;
            var row = item == null ? null : item.Tag as SessionRow;
            if (row == null) return;

            string detail = L.SyncResetConfirm(row.DisplayName, row.Files, Utils.FormatSize(row.Bytes));
            var confirm = new Window
            {
                Title = L.SyncReset,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                MinWidth = 420
            };
            DlgUi.Init(confirm, L.SyncReset, 460, 220, 420, 200);
            confirm.SizeToContent = SizeToContent.WidthAndHeight;

            var g = new Grid { Margin = new Thickness(16) };
            g.RowDefinitions.Add(new RowDefinition { Height = System.Windows.GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = System.Windows.GridLength.Auto });
            var text = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
            Grid.SetRow(text, 0);
            g.Children.Add(text);
            var yes = DlgUi.PrimaryMin(L.SyncResetDo, 96);
            var no = DlgUi.SecondaryMin(L.CancelBtn, 96);
            yes.Click += delegate
            {
                try
                {
                    Directory.Delete(row.Dir, true);
                    // The digests described files that no longer exist — drop them so
                    // nothing can answer "identical" against a re-sync of this session.
                    FileHashCache.ForServer.RemoveByPrefix(row.Dir);
                }
                catch { }
                confirm.DialogResult = true;
                Reload();
            };
            no.Click += delegate { confirm.DialogResult = false; };
            var row2 = DlgUi.ButtonRowRight(yes, no);
            row2.Margin = new Thickness(0, 12, 0, 0);
            Grid.SetRow(row2, 1);
            g.Children.Add(row2);
            confirm.Content = g;
            confirm.ShowDialog();
        }
    }
}
