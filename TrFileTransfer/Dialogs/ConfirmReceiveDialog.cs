using System;
using System.Windows;
using System.Windows.Controls;

namespace TrFileTransfer
{
    /// <summary>Receive-confirmation prompt shown when an unknown/any device starts a
    /// transfer. Shown non-modal by MainWindow, which watches Closed for the verdict.</summary>
    public class ConfirmReceiveDialog : Window
    {
        /// <summary>True when the user clicked Accept before the window closed.</summary>
        public bool Accepted { get; private set; }

        public ConfirmReceiveDialog(string ip, string name, long size, int files, bool isFolder)
        {
            DlgUi.Init(this, L.ConfirmTitle, 440, 220, 380, 190);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            string sizeStr = size >= 0 ? Utils.FormatSize(size) : "?";
            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var text = new TextBlock
            {
                Text = L.ConfirmText(ip, name, sizeStr, isFolder, files),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            };
            Grid.SetRow(text, 0);
            grid.Children.Add(text);

            var btnAccept = DlgUi.PrimaryMin(L.ConfirmAccept, 96);
            var btnDeny = DlgUi.SecondaryMin(L.ConfirmDeny, 96);
            btnAccept.Click += (s, e) => { Accepted = true; Close(); };
            btnDeny.Click += (s, e) => { Accepted = false; Close(); };
            var buttons = DlgUi.ButtonRowRight(btnAccept, btnDeny);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 12, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }
    }
}
