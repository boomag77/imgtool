using ImgProcessor.Abstractions;
using ImgViewer.Internal;
using LeadImgProcessor;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;


namespace ImgViewer
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private IImageProcessor _processor;

        private CancellationTokenSource _cts;

        private readonly FileExplorer _explorer = new FileExplorer();

        public class Thumbnail
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

        public ObservableCollection<Thumbnail> Files { get; set; } = new ObservableCollection<Thumbnail>();

        public MainWindow()
        {
            InitializeComponent();
            ImgListBox.ItemsSource = Files;
            var creds = ReadLicenseCreds();
            string licPath = creds.LicenseFilePath;
            string key = creds.LicenseKey;
            IImageProcessorFactory factory = new LeadImgProcessorFactory(licPath, key);
            _processor = factory.CreateProcessor();
            _processor.ImageUpdated += (stream) =>
            {
                var bitmap = streamToBitmapSource(stream);
                Dispatcher.InvokeAsync(() => ImgBox.Source = bitmap);
            };
            _processor.ErrorOccured += (msg) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(this, msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };
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

        private BitmapSource streamToBitmapSource(Stream stream)
        {
            if (stream == null || stream == Stream.Null)
                return null!;

            if (stream.CanSeek)
                stream.Position = 0;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze(); // чтобы можно было использовать из любого потока

            return bitmap;
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
                                    Files.Add(new Thumbnail
                                    {
                                        Name = System.IO.Path.GetFileName(file),
                                        Thumb = bmp,
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
            if (ImgListBox.SelectedItem is Thumbnail item)
            {
                _processor.LoadImage(item.Path);
            }
        }

        private async void OpenFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
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
                    _processor.LoadImage(dlg.FileName);
                    Title = $"ImgViewer - {Path.GetFileName(dlg.FileName)}";

                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show($"Error loading image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }



        private void ApplyDeskew(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.Deskew, new Dictionary<string, object>());
            //var updated = _processor.ApplyDeskewCurrent();
            //ImgBox.Source = updated;
        }

        private void ApplyAutoCropRectangleCurrentCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.AutoCropRectangle, new Dictionary<string, object>());
        }

        private void ApplyDespeckleCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.Despeckle, new Dictionary<string, object>());
        }


        private void ApplyBorderRemoveCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.BorderRemove, new Dictionary<string, object>());
        }

        private void ApplyAutoBinarizeCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.Binarize, new Dictionary<string, object>());
        }

        private void ApplyLineRemoveCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.LineRemove, new Dictionary<string, object>());
        }

        private void SaveAsClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.Filter = "PNG Image|*.png|JPEG Image|*.jpg;*.jpeg|TIFF Image|*.tif;*.tiff|Bitmap Image|*.bmp|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                var path = dlg.FileName;
                _processor.SaveCurrentImage(path);
            }
        }

        private void ExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}