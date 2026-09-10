using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TrFileTransfer
{
    /// <summary>Renders a URL (the HTTP share address) as a scannable QR code next to
    /// the plain text link, with a copy button. Non-modal so a running share stays usable.
    /// When the machine exposes several IPv4 addresses a selector picks which one the
    /// QR is generated for; the choice is remembered across reopenings.</summary>
    public class QrDialog : Window
    {
        private readonly List<string> _addresses;
        private readonly int _port;
        private Image _qrImage;
        private TextBox _urlBox;
        private string _url;

        public QrDialog(List<string> addresses, int port)
        {
            _addresses = addresses ?? new List<string> { "127.0.0.1" };
            _port = port;

            DlgUi.Init(this, L.QrTitle, 420, 540, 360, 460);

            var grid = new Grid { Margin = new Thickness(16) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // hint
            if (_addresses.Count > 1)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // address picker
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // qr
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // url
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // buttons

            var hint = new TextBlock
            {
                Text = L.QrHint,
                TextWrapping = TextWrapping.Wrap,
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Margin = new Thickness(0, 0, 0, 10)
            };
            Grid.SetRow(hint, 0);
            grid.Children.Add(hint);

            // Rows are handed out incrementally: with a single address the picker
            // row doesn't exist and the QR must land on row 1, not overlap the hint
            int nextRow = 1;
            int pickerRow = -1;
            if (_addresses.Count > 1) pickerRow = nextRow++;
            int qrRow = nextRow++;
            int urlRow = nextRow++;
            int btnRow = nextRow;
            if (pickerRow >= 0)
            {
                var picker = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
                picker.Children.Add(DlgUi.Label(L.QrAddressLabel));
                var cmb = new ComboBox
                {
                    Style = DlgUi.Res<Style>("CmbInput"),
                    MinWidth = 170,
                    VerticalAlignment = VerticalAlignment.Center
                };
                foreach (var ip in _addresses) cmb.Items.Add(ip);
                // Remembered choice from a previous session wins over the default ordering
                int selected = 0;
                string saved = Config.Get("QrAddress", "");
                if (saved.Length > 0) selected = Math.Max(0, _addresses.IndexOf(saved));
                cmb.SelectedIndex = selected;
                cmb.SelectionChanged += (s, e) => OnAddressChanged(cmb.SelectedItem as string);
                picker.Children.Add(cmb);
                Grid.SetRow(picker, pickerRow);
                grid.Children.Add(picker);

                _url = BuildUrl(_addresses[selected]);
            }
            else
            {
                _url = BuildUrl(_addresses[0]);
            }

            _qrImage = new Image { HorizontalAlignment = HorizontalAlignment.Center };
            RenderIntoImage();
            Grid.SetRow(_qrImage, qrRow);
            grid.Children.Add(_qrImage);

            _urlBox = new TextBox
            {
                Text = _url,
                IsReadOnly = true,
                Style = DlgUi.Res<Style>("TxtInput"),
                Margin = new Thickness(0, 12, 0, 0),
                FontFamily = DlgUi.Res<FontFamily>("Font.Mono"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetRow(_urlBox, urlRow);
            grid.Children.Add(_urlBox);

            var btnCopy = DlgUi.SecondaryMin(L.CopyBtn, 96);
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 96);
            btnCopy.Click += (s, e) =>
            {
                try { Clipboard.SetText(_url); } catch { }
            };
            btnClose.Click += (s, e) => Close();
            var buttons = DlgUi.ButtonRowRight(btnCopy, btnClose);
            Grid.SetRow(buttons, btnRow);
            buttons.Margin = new Thickness(0, 12, 0, 0);
            grid.Children.Add(buttons);

            Content = grid;
        }

        private string BuildUrl(string ip)
        {
            return HttpShareServer.BuildLanUrl(ip, _port);
        }

        private void OnAddressChanged(string ip)
        {
            if (string.IsNullOrEmpty(ip)) return;
            _url = BuildUrl(ip);
            _urlBox.Text = _url;
            Config.Set("QrAddress", ip);
            RenderIntoImage();
        }

        private void RenderIntoImage()
        {
            try
            {
                _qrImage.Source = RenderQr(_url);
            }
            catch (ArgumentException)
            {
                _qrImage.Source = null; // payload overflow — the URL text below still shows
            }
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
