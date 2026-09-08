using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrFileTransfer
{
    /// <summary>Renders a URL (the HTTP share address) as a scannable QR code next to
    /// the plain text link, with a copy button. Non-modal so a running share stays usable.</summary>
    public class QrDialog : Window
    {
        public QrDialog(string url)
        {
            DlgUi.Init(this, L.QrTitle, 420, 540, 360, 460);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var hint = new TextBlock
            {
                Text = L.QrHint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            grid.Children.Add(hint);

            Image qrImage;
            try
            {
                qrImage = new Image { Source = RenderQr(url), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center };
            }
            catch (ArgumentException)
            {
                qrImage = new Image(); // payload overflow — the URL text below still shows
            }
            Grid.SetRow(qrImage, 1);
            grid.Children.Add(qrImage);

            var urlBox = new TextBox
            {
                Text = url,
                IsReadOnly = true,
                Style = DlgUi.Res<Style>("TxtInput"),
                Margin = new Thickness(0, 12, 0, 0),
                FontFamily = DlgUi.Res<FontFamily>("Font.Mono"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(urlBox, 2);
            grid.Children.Add(urlBox);

            var btnCopy = DlgUi.SecondaryMin(L.CopyBtn, 96);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(url); } catch { }
            };
            btnClose.Click += (s, e) => Close();
            var buttons = DlgUi.ButtonRowRight(btnCopy, btnClose);
            Grid.SetRow(buttons, 3);
            buttons.Margin = new Thickness(0, 12, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        /// <summary>Rasterizes the module matrix into a BitmapSource (8 px per module,
        /// 4-module quiet zone — enough for phone cameras).</summary>
        private static BitmapSource RenderQr(string url)
        {
            var qr = QrEncoder.Encode(url);
            const int Scale = 8;
            const int Quiet = 4;
            int px = (qr.Size + Quiet * 2) * Scale;
            var bmp = new WriteableBitmap(px, px, 96, 96, PixelFormats.Bgra32, null);

            byte[] pixels = new byte[px * px * 4];
            SetAll(pixels, 255);
            for (int y = 0; y < qr.Size; y++)
            {
                for (int x = 0; x < qr.Size; x++)
                {
                    if (!qr.Modules[y, x]) continue;
                    int x0 = (x + Quiet) * Scale;
                    int y0 = (y + Quiet) * Scale;
                    for (int dy = 0; dy < Scale; dy++)
                    {
                        int rowStart = ((y0 + dy) * px + x0) * 4;
                        for (int dx = 0; dx < Scale; dx++)
                        {
                            int idx = rowStart + dx * 4;
                            pixels[idx] = 16;
                            pixels[idx + 1] = 16;
                            pixels[idx + 2] = 20;
                            pixels[idx + 3] = 255;
                        }
                    }
                }
            }
            bmp.WritePixels(new Int32Rect(0, 0, px, px), pixels, px * 4, 0);
            return bmp;
        }

        /// <summary>Fills every pixel white — including the ALPHA byte: Bgra32 starts
        /// zeroed, and alpha=0 means the finished QR is fully transparent (invisible).</summary>
        private static void SetAll(byte[] pixels, byte value)
        {
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i] = value;
                pixels[i + 1] = value;
                pixels[i + 2] = value;
                pixels[i + 3] = 255;
            }
        }
    }
}
