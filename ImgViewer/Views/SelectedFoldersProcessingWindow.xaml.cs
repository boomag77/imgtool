using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class SelectedFoldersProcessingWindow : Window
    {
        private readonly IAppManager _appManager;
        private readonly Pipeline _pipeline;
        private readonly ObservableCollection<string> _folders = new();

        private bool _isRunning;
        private bool _stopRequested;

        public SelectedFoldersProcessingWindow(IAppManager appManager, Pipeline pipeline)
        {
            InitializeComponent();
            _appManager = appManager;
            _pipeline = pipeline;
            FoldersListBox.ItemsSource = _folders;
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                FileName = "Select folder"
            };

            if (dialog.ShowDialog() != true)
                return;

            string? selectedFolder = Path.GetDirectoryName(dialog.FileName);
            if (string.IsNullOrWhiteSpace(selectedFolder))
                return;

            if (_folders.Any(f => string.Equals(f, selectedFolder, StringComparison.OrdinalIgnoreCase)))
                return;

            _folders.Add(selectedFolder);
        }

        private void RemoveSelectedFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FoldersListBox.SelectedItem is string selected)
                _folders.Remove(selected);
        }

        private void ClearFolders_Click(object sender, RoutedEventArgs e)
        {
            _folders.Clear();
        }

        private void StartProcessing_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
            {
                _stopRequested = true;
                _appManager.CancelBatchProcessing();
                return;
            }

            if (_pipeline == null || _pipeline.Operations.Count == 0)
            {
                MessageBox.Show("Pipeline is empty. Add at least one operation before running.",
                                "Batch processing",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            if (_folders.Count == 0)
            {
                MessageBox.Show("Add at least one folder.",
                                "Batch processing",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                return;
            }

            _isRunning = true;
            _stopRequested = false;
            StartProcessingButton.Content = "Stop processing";

            var foldersToProcess = _folders.ToArray();
            _ = _appManager.ProcessSelectedFolders(foldersToProcess, _pipeline);
            Close();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            if (_isRunning)
                return;

            Close();
        }
    }
}
