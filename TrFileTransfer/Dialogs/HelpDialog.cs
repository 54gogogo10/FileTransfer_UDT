using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Usage guide dialog: static bilingual instructions (L.HelpText),
    /// opened from the header "使用说明/Guide" button.</summary>
    public class HelpDialog : Window
    {
        public HelpDialog()
        {
            DlgUi.Init(this, L.HelpBtn, 620, 520, 520, 400);
            MinWidth = 520;
            MinHeight = 400;

            var text = new TextBlock
            {
                Text = L.HelpText(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                LineHeight = 21,
                Foreground = DlgUi.Res<Brush>("Brush.TextPrimary")
            };
            var scroll = new ScrollViewer
            {
                Content = text,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var btnClose = DlgUi.PrimaryMin(L.CancelBtn, 100);
            btnClose.Click += (s, e) => Close();

            var grid = new Grid { Margin = new Thickness(14) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(scroll, 0);
            grid.Children.Add(scroll);
            var buttons = DlgUi.ButtonRowRight(btnClose);
            Grid.SetRow(buttons, 1);
            buttons.Margin = new Thickness(0, 10, 0, 0);
            grid.Children.Add(buttons);
            Content = grid;
        }
    }
}
