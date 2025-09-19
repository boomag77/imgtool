using ImgProcessor.Abstractions;
using ImgViewer.Internal;
using ImgViewer.Internal.Abstractions;
using LeadImgProcessor;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;


namespace ImgViewer
{
    public partial class MainWindow : Window
    {
        private readonly IImageProcessor _processor;
        private readonly IFileProcessor _explorer;
        private readonly IImageProcessorFactory _factory;


        private CancellationTokenSource _cts;



        public class Thumbnail : INotifyPropertyChanged
        {
            private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(8);
            private IFileProcessor Explorer { get; }
            private readonly Dispatcher _dispatcher;
            private readonly CancellationToken _token;
            public string Name { get; }
            public string Path { get; }
            private BitmapImage? _thumb;
            public BitmapImage Thumb
            {
                get
                {
                    if (_thumb == null)
                        LoadThumbAsync();
                    return _thumb!;
                }
                private set
                {
                    _thumb = value;
                    OnPropertyChanged();
                }
            }
            public Thumbnail(CancellationToken token, Dispatcher dispatcher, IFileProcessor explorer, string path, bool visible = false)
            {
                _token = token;
                Explorer = explorer;
                _dispatcher = dispatcher;
                Name = System.IO.Path.GetFileName(path);
                Path = path;

                if (visible)
                {
                    Thumb = Explorer.Load<BitmapImage>(Path, 80);
                }
                else
                {
                    Task.Run(() => LoadThumbAsync(token));
                }


            }

            private async void LoadThumbAsync(CancellationToken token = default)
            {
                await _semaphore.WaitAsync();
                try
                {
                    if (token.IsCancellationRequested)
                        return;
                    var bmp = Explorer.Load<BitmapImage>(Path, 80);
                    if (!token.IsCancellationRequested)
                    {
                        await _dispatcher.InvokeAsync(() => Thumb = bmp);
                    }
                }
                finally
                {
                    _semaphore.Release();
                }
            }


            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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

            _cts = new CancellationTokenSource();

            ImgListBox.ItemsSource = Files;
            var creds = ReadLicenseCreds();
            string licPath = creds.LicenseFilePath;
            string key = creds.LicenseKey;
            IImageProcessorFactory factory = new LeadImgProcessorFactory(licPath, key);
            _factory = factory;
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
            _explorer = new FileExplorer(_cts.Token);
            _explorer.ErrorOccured += (msg) =>
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
                var secretPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secret.json");
                var secret = File.ReadAllText(secretPath);
                var creds = JsonSerializer.Deserialize<LicenseCredentials>(secret);
                if (creds == null || string.IsNullOrWhiteSpace(creds.LicenseFilePath) || string.IsNullOrWhiteSpace(creds.LicenseKey))
                    throw new Exception("Invalid license credentials in secret.json");
                creds.LicenseFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, creds.LicenseFilePath);
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
                    var files = Directory.GetFiles(folderPath, "*.*")
                             .Where(file =>
                                 file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                 file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                 file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                 file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                                 file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                             .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) // сортировка по имени
                             .ToArray();
                    if (token.IsCancellationRequested)
                        return;
                    var thumbs = files.Select((f, v) => new Thumbnail(token, this.Dispatcher, _explorer, f, v < 30)).ToList();

                    Dispatcher.InvokeAsync(async () =>
                    {
                        uint i = 0;
                        foreach (var thumb in thumbs)
                        {
                            Files.Add(thumb);
                            if (i++ % 10 == 0) 
                                 await Task.Yield();
                        }
                               

                        if (Files.Count > 0)
                            ImgListBox.SelectedIndex = 0;
                    });
                }, token);

        }

        private void ImgList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ImgListBox.SelectedItem is Thumbnail item)
            {
                SetImgBoxSource(item.Path);

            }
        }

        private async void OpenFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string folderPath = dlg.SelectedPath;

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

        private void SetImgBoxSource(string filePath)
        {
            try
            {
                var bitmap = _explorer.Load<BitmapImage>(filePath);
                Dispatcher.InvokeAsync(() => ImgBox.Source = bitmap);
                Title = $"ImgViewer - {Path.GetFileName(filePath)}";
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show
                (
                    $"Error loading image for private: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private void OpenFile(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                SetImgBoxSource(dlg.FileName);
            }
        }



        private void ApplyDeskew(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.Deskew, new Dictionary<string, object>());
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

        private void ApplyPunchesRemoveCommand(object sender, RoutedEventArgs e)
        {
            _processor.ApplyCommandToCurrent(ProcessorCommands.DotsRemove, new Dictionary<string, object>());
        }

        private async void ProcessFolderClick(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string folderPath = dlg.SelectedPath;
                ProcessorCommands[] commands =
                {
                    ProcessorCommands.Binarize,
                    ProcessorCommands.Deskew,


                    ProcessorCommands.DotsRemove
                };
                var fileExplorer = new FileExplorer(_cts.Token);
                var sourceFolder = fileExplorer.GetImageFilesPaths(folderPath);
                var workerPool = new ImgWorkerPool(_cts, commands, 1, _factory, fileExplorer, sourceFolder, 0);
                StatusText.Text = "Processing...";
                await Task.Yield();
                workerPool.ProgressChanged += (done, total) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        MyProgressBar.Maximum = total;
                        MyProgressBar.Value = done;
                    });
                };
                workerPool.ErrorOccured += (msg) =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        System.Windows.MessageBox.Show(this, msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    });
                };
                try
                {
                    await workerPool.RunAsync();
                }
                catch (OperationCanceledException)
                {
                    StatusText.Text = "Cancelled";

                }
            }
            StatusText.Text = "Ready";
            MyProgressBar.Value = 0;
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

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}