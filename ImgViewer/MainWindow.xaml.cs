using ImgViewer.Internal;
using LeadImgProcessor;
using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Configuration;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media.Imaging;
using System.Text.Json;


namespace ImgViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private LeadImageProcessor _processor;

        private CancellationTokenSource _cts;

        private readonly FileExplorer _explorer = new FileExplorer();

        public class FileItem
        {
            public string Name { get; set; }
            public BitmapImage Thumb { get; set; }
            public string Path { get; set; }
        }
            
        private class LicenseCredentials
        {
            public string? LicenseFilePath { get; set; }
            public string? LicenseKey { get; set; }
        }

        public ObservableCollection<FileItem> Files { get; set; } = new ObservableCollection<FileItem>();

        public MainWindow()
        {
            InitializeComponent();
            ImgListBox.ItemsSource = Files;
            var creds = ReadLicenseCreds();
            string licPath = creds.LicenseFilePath;
            string key = creds.LicenseKey;
            _processor = new LeadImageProcessor(licPath, key);
        }

        private LicenseCredentials ReadLicenseCreds()
        {
            try
            {
                var secret = File.ReadAllText("secret.json");
                var creds = JsonSerializer.Deserialize<LicenseCredentials>(secret);
                if (creds == null || string.IsNullOrWhiteSpace(creds.LicenseFilePath) || string.IsNullOrWhiteSpace(creds.LicenseKey))
                    throw new Exception("Invalid license credentials in secret.json");
                return creds;
            }
            catch (Exception ex)
            {
                throw new Exception("Error reading license credentials: " + ex.Message, ex);
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _cts?.Cancel();
            base.OnClosing(e);
        }

        private async Task LoadFolder(string folderPath, CancellationToken token)
        {
            
            Files.Clear();
            await Task.Run(() =>
                {
                    foreach (var file in Directory.EnumerateFiles(folderPath, "*.*"))
                    {
                        if (token.IsCancellationRequested)
                            break;

                        if (file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                            file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                        {
                            try
                            {
                                var bmp = _explorer.LoadImage(file, 80);
                                Dispatcher.InvokeAsync(() =>
                                {
                                    if (!IsLoaded || token.IsCancellationRequested)
                                        return; 
                                    Files.Add(new FileItem
                                    {
                                        Name = System.IO.Path.GetFileName(file),
                                        Thumb =bmp,
                                        Path = file
                                    });
                                    if (Files.Count == 1)
                                        ImgListBox.SelectedIndex = 0;
                                });
                            }
                            catch (Exception ex)
                            {
                                // Handle exceptions (e.g., log them)
                                System.Windows.MessageBox.Show(
                                     string.Format("Error loading image {0}: {1}", file, ex.Message),
                                     "Error",
                                     MessageBoxButton.OK,
                                     MessageBoxImage.Error);
                            }
                        }
                    }
                }, token);
            
        }

        private void ImgList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ImgListBox.SelectedItem is FileItem item)
            {
                //ImgBox.Source = _explorer.LoadImage(item.Path);
                ImgBox.Source = _processor.LoadImage(item.Path);
            }
        }

        private async void OpenFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();

            if (dlg.ShowDialog() ==System.Windows.Forms.DialogResult.OK)
            {
                string folderPath = dlg.SelectedPath;
                _cts?.Cancel();
                _cts = new CancellationTokenSource();

                try
                {
                    await LoadFolder(dlg.SelectedPath, _cts.Token);
                    Title = $"ImgViewer - {dlg.SelectedPath}";

                    if (Files.Count > 0)
                        ImgListBox.SelectedIndex = 0;
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine("Загрузка отменена.");
                }
            }
        }

        private void OpenFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    //using var stream = new FileStream(dlg.FileName, FileMode.Open, FileAccess.Read);
                    //var bitmap = new BitmapImage();
                    //bitmap.BeginInit();
                    //bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    //bitmap.UriSource = null;
                    //bitmap.StreamSource = stream;
                    //bitmap.EndInit();
                    //bitmap.Freeze();

                    //ImgBox.Source = bitmap;
                    ImgBox.Source = _processor.LoadImage(dlg.FileName);
                    Title = $"ImgViewer - {Path.GetFileName(dlg.FileName)}";

                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }



        private void ProcessAllImgs(object sender, RoutedEventArgs e)
        {
            var updated = _processor.ApplyDeskewCurrent();
            ImgBox.Source = updated;
            Console.WriteLine("Processing all images... -> Done..");
        }
        private void ExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}