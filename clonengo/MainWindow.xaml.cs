using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace clonengo
{
    /// <summary>Profiililistan rivi.</summary>
    public sealed class UserProfileItem : INotifyPropertyChanged
    {
        private string _sizeText = "Lasketaan...";
        public string Name { get; set; } = "";
        public string Sid { get; set; } = "";
        public string Path { get; set; } = "";
        public long SizeBytes { get; set; }
        public bool SizeReady { get; set; }
        public string SizeText
        {
            get => _sizeText;
            set { _sizeText = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed partial class MainWindow : Window
    {
        public ObservableCollection<UserProfileItem> Profiles { get; } = new();
        private readonly CancellationTokenSource _cts = new();

        public List<ExclusionOption> Exclusions { get; } = ExclusionOption.CreateDefaults();

        private async void LocationsMenu_Click(object sender, RoutedEventArgs e)
        {
            var boxes = new List<(CheckBox Box, ExclusionOption Option)>();
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(new TextBlock
            {
                Text = "Rastitetut kohteet jätetään pois profiilia kopioitaessa.",
                TextWrapping = TextWrapping.Wrap,
            });
            foreach (var option in Exclusions)
            {
                var box = new CheckBox
                {
                    IsChecked = option.IsExcluded,
                    Content = new StackPanel
                    {
                        Children =
                        {
                            new TextBlock { Text = option.Label },
                            new TextBlock
                            {
                                Text = option.Description,
                                TextWrapping = TextWrapping.Wrap,
                                FontSize = 12,
                                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                            },
                        },
                    },
                };
                boxes.Add((box, option));
                panel.Children.Add(box);
            }

            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = "Kopioitavat sijainnit: pois jätettävät kohteet",
                Content = new ScrollViewer { Content = panel, MaxHeight = 450 },
                PrimaryButtonText = "Tallenna",
                CloseButtonText = "Peruuta",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                foreach (var (box, option) in boxes)
                    option.IsExcluded = box.IsChecked == true;
        }

        private void ThemeMenu_Click(object sender, RoutedEventArgs e)
        {
            var tag = (sender as FrameworkElement)?.Tag as string;
            RootGrid.RequestedTheme = tag switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        /// <summary>Valittu pakkaustapa (vain kopiointitilassa).</summary>
        public CompressionMethod SelectedCompression => CompressionBox.SelectedIndex switch
        {
            0 => CompressionMethod.None,
            1 => CompressionMethod.Zip,
            _ => CompressionMethod.SevenZip,
        };

        private bool IsCopyMode => ModeSelector.SelectedIndex == 0;

        public MainWindow()
        {
            InitializeComponent();
            RootGrid.RequestedTheme = ElementTheme.Dark;
            Closed += (_, _) => _cts.Cancel();
            LoadProfiles();
        }

        private void LoadProfiles()
        {
            foreach (var (name, sid, path) in ProfileService.GetProfiles())
            {
                var item = new UserProfileItem { Name = name, Sid = sid, Path = path };
                Profiles.Add(item);
                var queue = DispatcherQueue;
                _ = Task.Run(() =>
                {
                    try
                    {
                        long size = ProfileService.GetDirectorySize(path, _cts.Token);
                        queue.TryEnqueue(() =>
                        {
                            item.SizeBytes = size;
                            item.SizeReady = true;
                            item.SizeText = ProfileService.FormatSize(size);
                        });
                    }
                    catch (OperationCanceledException) { }
                });
            }
        }

        private void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (RunButton is null) return; // kutsutaan alustuksen aikana

            if (IsCopyMode)
            {
                DescriptionText.Text = "Kopioi valitut käyttäjäprofiilit tiedostoon.";
                FileLabel.Text = "Kohdetiedosto";
                ListLabel.Text = "Valitse kopioitavat profiilit";
                RunButton.Content = "Kopioi";
                CompressPanel.Visibility = Visibility.Visible;
            }
            else
            {
                DescriptionText.Text = "Palauta käyttäjäprofiilit tiedostosta tälle tietokoneelle.";
                FileLabel.Text = "Lähdetiedosto";
                ListLabel.Text = "Valitse palautettavat profiilit";
                RunButton.Content = "Palauta";
                CompressPanel.Visibility = Visibility.Collapsed;
            }

            FilePathBox.Text = "";
            ProfileList.SelectedItems.Clear();
            UpdateState();
        }

        private async void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (IsCopyMode)
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = "profiilit" };
                picker.FileTypeChoices.Add("Clonengo-tiedosto", new List<string> { ".clonengo" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSaveFileAsync();
                if (file != null) FilePathBox.Text = file.Path;
            }
            else
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                picker.FileTypeFilter.Add(".clonengo");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSingleFileAsync();
                if (file != null) FilePathBox.Text = file.Path;
            }
            UpdateState();
        }

        private void SelectAll_Click(object sender, RoutedEventArgs e) => ProfileList.SelectAll();

        private void ClearSelection_Click(object sender, RoutedEventArgs e) => ProfileList.SelectedItems.Clear();

        private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

        private CancellationTokenSource? _runCts;

        private async void RunButton_Click(object sender, RoutedEventArgs e)
        {
            if (_runCts != null) { _runCts.Cancel(); return; } // käynnissä: peruuta

            if (!IsCopyMode)
            {
                await ShowMessageAsync("Palautus", "Palautus ei ole vielä toteutettu.");
                return;
            }

            var selected = ProfileList.SelectedItems.Cast<UserProfileItem>().ToList();
            var sources = selected.Select(p => new ProfileSource(p.Name, p.Sid, p.Path)).ToList();
            bool allSized = selected.All(p => p.SizeReady);
            long total = allSized ? selected.Sum(p => p.SizeBytes) : 0;
            string output = FilePathBox.Text;
            if (!output.EndsWith(".clonengo", StringComparison.OrdinalIgnoreCase)) output += ".clonengo";
            var compression = SelectedCompression;

            _runCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var ct = _runCts.Token;
            SetBusy(true, total > 0);
            var progress = new Progress<ArchiveProgress>(p =>
            {
                if (total > 0) Progress.Value = Math.Min(100, p.BytesDone * 100.0 / total);
                StatusText.Text = string.IsNullOrEmpty(p.CurrentFile) ? "Viimeistellään..." : "Kopioidaan: " + p.CurrentFile;
            });

            try
            {
                var result = await Task.Run(() =>
                    ProfileArchiver.Create(output, sources, compression, Exclusions, progress, ct));
                Progress.Value = 100;
                await ShowMessageAsync("Kopiointi valmis", BuildSummary(result, output));
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Kopiointi peruttu";
            }
            catch (Exception ex)
            {
                await ShowMessageAsync("Kopiointi epäonnistui", ex.Message);
            }
            finally
            {
                _runCts.Dispose();
                _runCts = null;
                SetBusy(false, false);
                UpdateState();
            }
        }

        private static string BuildSummary(ArchiveResult r, string path)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(path);
            sb.AppendLine($"{r.Profiles.Count} profiilia, {r.Files} tiedostoa");
            sb.AppendLine($"Alkuperäinen koko {ProfileService.FormatSize(r.Bytes)}, tiedoston koko {ProfileService.FormatSize(r.ArchiveSize)}");
            if (r.SkippedCount > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"Ohitettu {r.SkippedCount} tiedostoa tai kansiota (lukitus tai ei oikeuksia). Täysi lista on tiedoston manifest.json:ssa.");
                foreach (var s in r.Profiles.SelectMany(p => p.Skipped).Take(10))
                    sb.AppendLine("- " + s.Path);
            }
            return sb.ToString();
        }

        private void SetBusy(bool busy, bool determinate)
        {
            SetEnabledRecursive(ModePanel, !busy);
            SetEnabledRecursive(FilePanel, !busy);
            SetEnabledRecursive(ListPanel, !busy);
            MainMenu.IsEnabled = !busy;
            Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            Progress.IsIndeterminate = busy && !determinate;
            Progress.Value = 0;
            RunButton.Content = busy ? "Peruuta" : (IsCopyMode ? "Kopioi" : "Palauta");
            if (busy) RunButton.IsEnabled = true;
        }

        private static void SetEnabledRecursive(DependencyObject root, bool enabled)
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is Control c) c.IsEnabled = enabled;
                SetEnabledRecursive(child, enabled);
            }
        }

        private async Task ShowMessageAsync(string title, string message)
        {
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = title,
                Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 400 },
                CloseButtonText = "OK",
            }.ShowAsync();
        }

        private void UpdateState()
        {
            int count = ProfileList.SelectedItems.Count;
            StatusText.Text = count == 0
                ? "Ei valittuja profiileja"
                : $"{count} profiilia valittu";
            RunButton.IsEnabled = count > 0 && !string.IsNullOrWhiteSpace(FilePathBox.Text);
        }
    }
}
