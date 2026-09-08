using System;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Non-modal viewer for received text messages; accumulates messages while
    /// open. Closing hides instead of closing so messages accumulate across sessions.
    /// WPF port of TextReceivedDialog (also reused for the one-time firewall hint).</summary>
    public class TextReceivedDialog : Window
    {
        private readonly TextBox _txt;

        public TextReceivedDialog(string firstMessage)
            : this(firstMessage, L.TextReceivedTitle)
        {
        }

        public TextReceivedDialog(string firstMessage, string title)
        {
            DlgUi.Init(this, title, 460, 240, 380, 200);
            MinWidth = 380;
            MinHeight = 200;
            Closing += (s, e) =>
            {
                Hide();
                e.Cancel = true; // user close = hide; the window dies with the app
            };

            _txt = DlgUi.Input();
            _txt.IsReadOnly = true;
            _txt.AcceptsReturn = true;
            _txt.VerticalContentAlignment = VerticalAlignment.Top;
            _txt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _txt.Text = "[" + DateTime.Now.ToString("HH:mm:ss") + "]\r\n" + firstMessage;

            var btnCopy = DlgUi.PrimaryMin(L.CopyBtn, 100);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 100);
            btnCopy.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(_txt.Text);
                    MessageBox.Show(this, L.Copied, L.TextReceivedTitle,
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            };
            btnClose.Click += (s, e) => Close();

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_txt, 0);
            grid.Children.Add(_txt);
            var buttons = DlgUi.ButtonRowRight(btnCopy, btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        /// <summary>Appends another received message with a timestamp separator.</summary>
        public void AppendMessage(string text)
        {
            _txt.AppendText("\r\n\r\n[" + DateTime.Now.ToString("HH:mm:ss") + "]\r\n" + text);
        }
    }
}
