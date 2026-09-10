using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrFileTransfer
{
    /// <summary>Auto-update dialog: manifest URL + auto-check setting, check-now, and
    /// verified download that hands off to MainWindow.ApplyUpdateAndRestart.
    /// WPF port of UpdateDialog.</summary>
    public class UpdateDialog : Window
    {
        private TextBlock _lblCurrent;
        private TextBox _txtUrl;
        private CheckBox _chkAuto;
        private Button _btnCheck, _btnInstall, _btnSkip;
        private TextBlock _lblStatus;
        private ProgressBar _progress;
        private TextBlock _lblNotes;
        private UpdateManifest _manifest;

        public UpdateDialog()
        {
            DlgUi.Init(this, L.UpdTitle, 540, 330, 480, 300);

            _lblCurrent = new TextBlock
            {
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = DlgUi.Res<Brush>("Brush.TextPrimary")
            };

            _txtUrl = DlgUi.Input();
            _txtUrl.Text = Config.Get("UpdateUrl", MainWindow.DefaultUpdateUrl);
            _txtUrl.TextChanged += (s, e) =>
            {
                // Live format check: the updater itself rejects bad URLs at check time
                string u = _txtUrl.Text.Trim();
                bool ok = u.Length == 0
                    || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || u.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                _txtUrl.Tag = ok ? null : "invalid";
                _txtUrl.ToolTip = ok ? null : L.FieldUrlInvalid;
            };

            _chkAuto = new CheckBox
            {
                Style = DlgUi.Res<Style>("ChkBox"),
                IsChecked = Config.GetBool("AutoUpdateCheck", true),
                Content = L.UpdAutoCheck
            };
            _chkAuto.Checked += (s, e) => Config.SetBool("AutoUpdateCheck", true);
            _chkAuto.Unchecked += (s, e) => Config.SetBool("AutoUpdateCheck", false);

            _btnCheck = DlgUi.PrimaryMin(L.UpdCheckNow, 110);
            _btnInstall = DlgUi.PrimaryMin(L.UpdDownloadBtn, 130);
            _btnInstall.IsEnabled = false;
            _btnSkip = DlgUi.SecondaryMin(L.UpdSkip, 120);
            _btnSkip.Visibility = Visibility.Collapsed;
            var btnClose = DlgUi.SecondaryMin(L.CancelBtn, 100);
            _btnCheck.Click += BtnCheck_Click;
            _btnInstall.Click += BtnInstall_Click;
            _btnSkip.Click += BtnSkip_Click;
            btnClose.Click += (s, e) => Close();

            _lblStatus = new TextBlock
            {
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Text = "",
                TextWrapping = TextWrapping.Wrap
            };

            _progress = new ProgressBar { Style = DlgUi.Res<Style>("BarModern"), Visibility = Visibility.Collapsed };

            _lblNotes = new TextBlock
            {
                Foreground = DlgUi.Res<Brush>("Brush.TextSecondary"),
                Text = "",
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None
            };

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            actions.Children.Add(_btnCheck);
            _btnInstall.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(_btnInstall);
            _btnSkip.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(_btnSkip);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            buttons.Children.Add(btnClose);

            var grid = new Grid { Margin = new Thickness(14) };
            for (int i = 0; i < 8; i++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            void Add(FrameworkElement el, int row)
            {
                Grid.SetRow(el, row);
                grid.Children.Add(el);
            }

            Add(_lblCurrent, 0);
            _lblCurrent.Margin = new Thickness(0, 0, 0, 10);

            var urlRow = new Grid();
            urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            urlRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lblUrl = DlgUi.Label(L.UpdUrlLabel);
            lblUrl.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(lblUrl, 0);
            urlRow.Children.Add(lblUrl);
            Grid.SetColumn(_txtUrl, 1);
            urlRow.Children.Add(_txtUrl);
            Add(urlRow, 1);
            _txtUrl.Margin = new Thickness(0, 0, 0, 8);

            Add(_chkAuto, 2);
            _chkAuto.Margin = new Thickness(0, 0, 0, 8);

            Add(actions, 3);
            actions.Margin = new Thickness(0, 0, 0, 8);

            Add(_lblStatus, 4);
            _lblStatus.Margin = new Thickness(0, 0, 0, 6);

            Add(_progress, 5);
            _progress.Margin = new Thickness(0, 0, 0, 6);

            Add(_lblNotes, 6);

            Add(buttons, 8);
            buttons.Margin = new Thickness(0, 8, 0, 0);

            Content = new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            ApplyLanguage();
        }

        private void ApplyLanguage()
        {
            _lblCurrent.Text = L.UpdCurrentVersion(Updater.CurrentVersion.ToString());
            _btnCheck.Content = L.UpdCheckNow;
            _btnInstall.Content = L.UpdDownloadBtn;
            _btnSkip.Content = L.UpdSkip;
        }

        private async void BtnCheck_Click(object sender, RoutedEventArgs e)
        {
            string url = _txtUrl.Text.Trim();
            if (url.Length == 0)
            {
                _lblStatus.Text = L.UpdNoUrl;
                return;
            }
            Config.Set("UpdateUrl", url);
            _btnCheck.IsEnabled = false;
            _btnInstall.IsEnabled = false;
            _lblStatus.Text = L.UpdChecking;
            _lblNotes.Text = "";
            try
            {
                UpdateManifest m = await Updater.CheckAnyAsync(url, 15000).ConfigureAwait(true);
                _manifest = m;
                if (m.IsNewerThan(Updater.CurrentVersion))
                {
                    _lblStatus.Text = L.UpdAvailable(Updater.CurrentVersion, m.Version);
                    _lblNotes.Text = string.IsNullOrEmpty(m.Notes) ? "" : L.UpdNotesLabel + "\n" + m.Notes;
                    _btnInstall.IsEnabled = true;
                    _btnSkip.Visibility = Visibility.Visible;
                }
                else
                {
                    _lblStatus.Text = L.UpdLatest(Updater.CurrentVersion);
                    _btnSkip.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = L.UpdCheckFailed(ex.Message);
            }
            _btnCheck.IsEnabled = true;
        }

        /// <summary>Remembers the offered version so the silent startup check stops
        /// nagging about it. A manual "Check Now" still shows it.</summary>
        private void BtnSkip_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest == null) return;
            Config.Set("SkippedVersion", _manifest.Version.ToString());
            Config.Save();
            Close();
        }

        private async void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (_manifest == null) return;
            _btnCheck.IsEnabled = false;
            _btnInstall.IsEnabled = false;
            _progress.Visibility = Visibility.Visible;
            _progress.Value = 0;
            string staged = Path.Combine(Updater.StagingDir, "TrFileTransfer.update.exe");
            try
            {
                await Updater.DownloadAsync(_manifest, staged, (read, total) =>
                {
                    try
                    {
                        Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (_windowGone) return;
                            if (total > 0)
                            {
                                _progress.Maximum = 100;
                                _progress.Value = (int)Math.Min(100, read * 100 / total);
                                _lblStatus.Text = L.UpdDownloading(_progress.Value);
                            }
                            else
                            {
                                _progress.Maximum = (int)Math.Max(1, read);
                                _progress.Value = (int)read;
                                _lblStatus.Text = L.UpdDownloading("-");
                            }
                        }));
                    }
                    catch { }
                }, 30000).ConfigureAwait(true);

                _lblStatus.Text = L.UpdDownloadDone;
                if (MessageBox.Show(this, L.UpdRestartPrompt, L.UpdTitle,
                    MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    var main = Owner as MainWindow;
                    string expect = _manifest.Sha256Hex;
                    Close();
                    if (main != null) main.ApplyUpdateAndRestart(staged, expect);
                }
                else
                {
                    _btnCheck.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                _lblStatus.Text = L.UpdDownloadFailed(ex.Message);
                _progress.Visibility = Visibility.Collapsed;
                _btnCheck.IsEnabled = true;
                _btnInstall.IsEnabled = true;
            }
        }

        private bool _windowGone;

        protected override void OnClosed(EventArgs e)
        {
            _windowGone = true;
            Config.Save();
            base.OnClosed(e);
        }
    }
}
