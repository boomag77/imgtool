using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Collections.ObjectModel;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class BatchResizeWindow : Window
    {
        private readonly IAppManager _appManager;
        private readonly ObservableCollection<string> _folders = new();
        private bool _isRunning;
        private readonly HashSet<string> _folderSet = new(StringComparer.OrdinalIgnoreCase);

        public BatchResizeWindow(IAppManager appManager)
        {
            InitializeComponent();

            _appManager = appManager;
            FoldersListBox.ItemsSource = _folders;
            ResizeMethodComboBox.ItemsSource = Enum.GetValues(typeof(ResizeMethod)).Cast<ResizeMethod>();
            ResizeMethodComboBox.SelectedItem = ResizeMethod.NearestNeighbor;
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog()
            {
                Multiselect = true,
                Title = "Select folder"
            };

            if (dialog.ShowDialog() != true)
                return;

            foreach (string selectedFolder in dialog.FolderNames)
            {
                if (string.IsNullOrWhiteSpace(selectedFolder))
                    continue;

                if (!_folderSet.Add(selectedFolder))
                    continue;

                _folders.Add(selectedFolder);
            }
        }

        private void RemoveSelectedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FoldersListBox.SelectedItem is string selected)
            {
                _folders.Remove(selected);
            }
        }

        private void ClearFolders_Click(object sender, RoutedEventArgs e)
        {
            _folders.Clear();
        }

        private async void StartResize_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
                return;

            if (_folders.Count == 0)
            {
                MessageBox.Show("Add at least one folder.", "Batch resize", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(LongestDimensionTextBox.Text?.Trim(), out int longestDimension) || longestDimension <= 0)
            {
                MessageBox.Show("Longest dimension must be a positive integer.", "Batch resize", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (ResizeMethodComboBox.SelectedItem is not ResizeMethod method)
            {
                MessageBox.Show("Select resize method.", "Batch resize", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!int.TryParse(JpegQualityTextBox.Text?.Trim(), out int jpegQuality) || jpegQuality < 1 || jpegQuality > 100)
            {
                MessageBox.Show("JPG quality must be in range 1..100.", "Batch resize", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var parameters = new ResizeParameters
            {
                MaxWidth = longestDimension,
                MaxHeight = longestDimension,
                KeepAspectRatio = true,
                Method = method,
                JpegQuality = jpegQuality
            };

            _isRunning = true;
            StartResizeButton.IsEnabled = false;

            using var cts = new CancellationTokenSource();
            var progressWindow = new ResizeProgressWindow
            {
                Owner = this
            };
            progressWindow.CancelRequested += () =>
            {
                if (!cts.IsCancellationRequested)
                    cts.Cancel();
            };
            progressWindow.Show();

            try
            {
                bool success = await _appManager.ResizeFolders(
                    _folders.ToArray(),
                    parameters,
                    cts.Token,
                    Math.Max(1, Environment.ProcessorCount - 2),
                    (processed, total, currentFile) =>
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            if (progressWindow.IsLoaded)
                                progressWindow.UpdateProgress(processed, total, currentFile);
                        });
                    });

                if (progressWindow.IsLoaded)
                    progressWindow.CloseByOwner();

                MessageBox.Show(
                    success ? "Batch resizing finished." : "Batch resizing finished with errors.",
                    "Batch resize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (progressWindow.IsLoaded)
                    progressWindow.CloseByOwner();

                MessageBox.Show(
                    $"Batch resizing failed: {ex.Message}",
                    "Batch resize",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                _isRunning = false;
                StartResizeButton.IsEnabled = true;
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
                return;

            Close();
        }
    }
}
