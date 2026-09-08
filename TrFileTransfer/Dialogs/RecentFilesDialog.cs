using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Recent received files dialog: list, open location. WPF port of
    /// RecentFilesDialog — entries carry their path as data (no display-string parsing).</summary>
    public class RecentFilesDialog : Window
    {
        private class RecentEntry
        {
            public string Path;
            public long Size;
        }

        private readonly ListBox _list = DlgUi.DarkList();
        private readonly List<RecentEntry> _entries = new List<RecentEntry>();

        public RecentFilesDialog(string[] entries)
        {
            DlgUi.Init(this, L.RecentFiles, 600, 380, 500, 300);

            if (entries != null)
            {
                for (int i = entries.Length - 1; i >= 0; i--)
                {
                    string entry = entries[i];
                    int idx = entry.LastIndexOf('|');
                    if (idx <= 0) continue;
                    var re = new RecentEntry
                    {
                        Path = entry.Substring(0, idx),
                        Size = 0
                    };
                    long.TryParse(entry.Substring(idx + 1), out re.Size);
                    _entries.Add(re);
                }
            }

            var btnOpen = DlgUi.SecondaryMin(L.RecentOpen, 110);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 110);
            btnOpen.Click += BtnOpen_Click;
            btnClose.Click += (s, e) => Close();
            _list.MouseDoubleClick += (s, e) => BtnOpen_Click(null, null);

            if (_entries.Count == 0)
            {
                var hint = new TextBlock { Text = L.RecentFilesEmpty, Margin = new Thickness(8, 5, 0, 5) };
                hint.Foreground = DlgUi.Res<System.Windows.Media.Brush>("Brush.TextSecondary");
                _list.Items.Add(hint);
                var hintContainer = _list.ItemContainerGenerator.ContainerFromItem(hint) as ListBoxItem;
                if (hintContainer != null) { hintContainer.IsEnabled = false; hintContainer.Focusable = false; }
            }
            else
            {
                foreach (var entry in _entries)
                {
                    var tb = new TextBlock
                    {
                        Text = FormatEntry(entry.Path, entry.Size),
                        TextTrimming = TextTrimming.CharacterEllipsis
                    };
                    _list.Items.Add(tb);
                }
            }

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            var buttons = DlgUi.ButtonRowRight(btnOpen, btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private static string FormatEntry(string path, long size)
        {
            return Path.GetFileName(path) + "  (" + Utils.FormatSize(size) + ")" + "  — " + path;
        }

        private void BtnOpen_Click(object sender, RoutedEventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0 || idx >= _entries.Count) return;
            string path = _entries[idx].Path;
            if (File.Exists(path))
            {
                try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
            }
        }
    }
}
