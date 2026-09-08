using System;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Breakpoint-resume session list (single files + folders), WPF port of ResumeDialog.</summary>
    public class ResumeDialog : Window
    {
        public ResumeState SelectedState;
        public FolderResumeState SelectedFolderState;
        private readonly ListBox _list = DlgUi.DarkList();
        private ResumeState[] _states;
        private FolderResumeState[] _folderStates;

        public ResumeDialog(ResumeState[] states, FolderResumeState[] folderStates)
        {
            _states = states ?? new ResumeState[0];
            _folderStates = folderStates ?? new FolderResumeState[0];
            DlgUi.Init(this, L.ResumeListTitle, 560, 340, 460, 280);

            var btnContinue = DlgUi.PrimaryMin(L.ResumeBtn, 100);
            var btnDelete = DlgUi.SecondaryMin(L.ResumeDelete, 100);
            var btnClearAll = DlgUi.SecondaryMin(L.ResumeClearAll, 100);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 100);
            btnContinue.Click += BtnContinue_Click;
            btnDelete.Click += BtnDelete_Click;
            btnClearAll.Click += BtnClearAll_Click;
            btnClose.Click += (s, e) => Close();

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_list, 0);
            grid.Children.Add(_list);
            var buttons = DlgUi.ButtonRowRight(btnContinue, btnDelete, btnClearAll, btnClose);
            Grid.SetRow(buttons, 1);
            grid.Children.Add(buttons);

            Content = grid;
            FillList();
        }

        private void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            if (idx < _states.Length)
            {
                SelectedState = _states[idx];
            }
            else
            {
                int folderIdx = idx - _states.Length;
                if (folderIdx < _folderStates.Length)
                    SelectedFolderState = _folderStates[folderIdx];
            }
            DialogResult = true;
            Close();
        }

        private void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            int idx = _list.SelectedIndex;
            if (idx < 0) return;
            if (idx < _states.Length)
            {
                if (_states[idx] != null) ResumeState.Delete(_states[idx].SessionId);
                var newStates = new System.Collections.Generic.List<ResumeState>();
                for (int i = 0; i < _states.Length; i++)
                {
                    if (i != idx && _states[i] != null) newStates.Add(_states[i]);
                }
                _states = newStates.ToArray();
            }
            else
            {
                int folderIdx = idx - _states.Length;
                if (folderIdx < _folderStates.Length)
                {
                    if (_folderStates[folderIdx] != null)
                        FolderResumeState.Delete(_folderStates[folderIdx].SessionId);
                    var newFolders = new System.Collections.Generic.List<FolderResumeState>();
                    for (int i = 0; i < _folderStates.Length; i++)
                    {
                        if (i != folderIdx && _folderStates[i] != null) newFolders.Add(_folderStates[i]);
                    }
                    _folderStates = newFolders.ToArray();
                }
            }
            RefreshList();
        }

        private void BtnClearAll_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i < _states.Length; i++)
            {
                if (_states[i] != null) ResumeState.Delete(_states[i].SessionId);
            }
            for (int i = 0; i < _folderStates.Length; i++)
            {
                if (_folderStates[i] != null) FolderResumeState.Delete(_folderStates[i].SessionId);
            }
            _states = new ResumeState[0];
            _folderStates = new FolderResumeState[0];
            RefreshList();
        }

        private void RefreshList()
        {
            int selected = _list.SelectedIndex;
            _list.Items.Clear();
            FillList();
            if (selected >= 0 && selected < _list.Items.Count)
                _list.SelectedIndex = selected;
        }

        private void FillList()
        {
            foreach (var s in _states)
                if (s != null) _list.Items.Add(FormatState(s));
            foreach (var f in _folderStates)
                if (f != null) _list.Items.Add(FormatFolderState(f));
        }

        private static string FormatState(ResumeState s)
        {
            string progress = s.TotalSize > 0
                ? string.Format("{0:F1}%", 100.0 * s.SentBytes / s.TotalSize)
                : "?";
            return string.Format("{0} -> {1}:{2} [{3}] {4}",
                s.FileName, s.ServerIp, s.Port, progress,
                s.Created.ToLocalTime().ToString("g"));
        }

        private static string FormatFolderState(FolderResumeState f)
        {
            string progress = f.TotalBytes > 0
                ? string.Format("{0:F1}%", 100.0 * f.SentBytes / f.TotalBytes)
                : "?";
            return string.Format("{0}{1} ({2}) -> {3}:{4} [{5}] {6}",
                L.FolderTag, f.FolderName, f.FileCount, f.ServerIp, f.Port, progress,
                f.Created.ToLocalTime().ToString("g"));
        }
    }
}
