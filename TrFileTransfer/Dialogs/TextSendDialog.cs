using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Modal input dialog for sending a text message (0x06) to the server.
    /// WPF port of TextSendDialog.</summary>
    public class TextSendDialog : Window
    {
        private readonly TextBox _txt;

        public string MessageText
        {
            get { return _txt.Text; }
        }

        public TextSendDialog()
        {
            DlgUi.Init(this, L.SendTextTitle, 460, 240, 380, 200);
            MinWidth = 380;
            MinHeight = 200;

            _txt = DlgUi.Input();
            _txt.AcceptsReturn = true;
            _txt.VerticalContentAlignment = VerticalAlignment.Top;
            _txt.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;

            var btnSend = DlgUi.PrimaryMin(L.SendTextSend, 100);
            var btnCancel = DlgUi.SecondaryMin(L.CancelBtn, 100);
            btnSend.Click += BtnSend_Click;
            btnCancel.Click += (s, e) => { DialogResult = false; Close(); };

            var grid = new Grid { Margin = new Thickness(12) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_txt, 0);
            grid.Children.Add(_txt);
            var buttons = DlgUi.ButtonRowRight(btnSend, btnCancel);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private void BtnSend_Click(object sender, RoutedEventArgs e)
        {
            if (_txt.Text.Length == 0)
            {
                MessageBox.Show(this, L.SendTextEmpty, L.SendTextTitle,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
            Close();
        }
    }
}
