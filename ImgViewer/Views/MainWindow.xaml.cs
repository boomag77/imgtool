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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
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
        private CancellationTokenSource? _currentLoadPreviewCts;
        private CancellationTokenSource? _currentLoadThumbnailsCts;



        public class Thumbnail : INotifyPropertyChanged, IDisposable
        {
            private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount / 2));
            private IFileProcessor Explorer { get; }
            private readonly Dispatcher _dispatcher;
            private readonly CancellationToken _parentToken;
            private CancellationTokenSource? _localCts;
            public string Name { get; }
            public string Path { get; }
            private BitmapImage? _thumb;
            public BitmapImage Thumb
            {
                get => _thumb;
                private set
                {
                    _thumb = value;
                    OnPropertyChanged();
                }
            }
            public Thumbnail(CancellationToken parentToken, Dispatcher dispatcher, IFileProcessor explorer, string path, bool preload = false)
            {
                _parentToken = parentToken;
                Explorer = explorer;
                _dispatcher = dispatcher;
                Name = System.IO.Path.GetFileName(path);
                Path = path;

                if (preload)
                {
                    _ = LoadThumbAsync(_parentToken);
                }

            }
            public void Dispose()
            {
                try
                {
                    _localCts?.Cancel();
                    _localCts?.Dispose();
                    _localCts = null;
                }
                catch { }

                Thumb = null;
            }

            public async Task LoadThumbAsync(CancellationToken token = default)
            {
                try
                {
                    _localCts?.Cancel();
                    _localCts?.Dispose();
                }
                catch { /* ignore */ }

                _localCts = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, token);
                var ct = _localCts.Token;

                await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (ct.IsCancellationRequested)
                        return;
                    var bmp = await Task.Run(() => Explorer.Load<BitmapImage>(Path, 50), ct).ConfigureAwait(false);
                    if (!ct.IsCancellationRequested && bmp != null)
                    {
                        await _dispatcher.InvokeAsync(() => Thumb = bmp).Task.ConfigureAwait(false);
                    }
                }
                finally
                {
                    try { _semaphore.Release(); } catch { }
                    try
                    {
                        _localCts?.Dispose();
                    }
                    catch { }
                    _localCts = null;
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
                    System.Windows.MessageBox.Show(this, msg, "Processor Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };
            _explorer = new FileExplorer(_cts.Token);
            _explorer.ErrorOccured += (msg) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(this, msg, " Explorer Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            _currentLoadPreviewCts?.Cancel();
            _currentLoadThumbnailsCts?.Cancel();
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

        private async Task LoadFolder(string folderPath)
        {
            _currentLoadThumbnailsCts?.Cancel();
            _currentLoadThumbnailsCts = new CancellationTokenSource();
            var ct = _currentLoadThumbnailsCts.Token;

            
            await Dispatcher.InvokeAsync(() =>
            {
                foreach (var old in Files.OfType<IDisposable>().ToList())
                {
                    if (ct.IsCancellationRequested)
                        return;
                    old.Dispose();
                }

                   
                Files.Clear();
            }, DispatcherPriority.Background).Task;

            string[] filePaths = await Task.Run(() =>
            {
                if (!Directory.Exists(folderPath))
                    throw new DirectoryNotFoundException($"Directory does not exist: {folderPath}");
                return Directory.GetFiles(folderPath, "*.*")
                         .Where(file =>
                             file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                             file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) // sort by name
                         .ToArray();
            }, ct);
            if (ct.IsCancellationRequested)
                return;
            const int preLoadCount = 20;
            const int batchSize = 10;
            int filesCount = filePaths.Length;
            for (int i = 0; i < filePaths.Length; i += batchSize)
            {
                if (ct.IsCancellationRequested)
                    return;
                int end = Math.Min(i + batchSize, filesCount);
                var batch = new List<Thumbnail>(end - i);
                for (int j = i; j < end; j++)
                {
                    if (ct.IsCancellationRequested)
                        return;
                    var item = filePaths[j];
                    var thumb = new Thumbnail(ct, this.Dispatcher, _explorer, item, j < preLoadCount);
                    batch.Add(thumb);
                    Files.Add(thumb);
                }
                foreach (var t in batch)
                {
                    _ = t.LoadThumbAsync(ct); // не await, пусть LoadThumbAsync сам поймает исключения
                }
                await Dispatcher.Yield(DispatcherPriority.Background);
            }
            await Dispatcher.InvokeAsync(() =>
            {
                if (Files.Count > 0)
                    ImgListBox.SelectedIndex = 0;
            }, DispatcherPriority.Background).Task;
        }

        private async void ImgList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (ImgListBox.SelectedItem is Thumbnail item)
            {
                try
                {
                    await SetImgBoxSourceAsync(item.Path);
                    _processor.Load(item.Path);
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show
                    (
                        $"Error loading image for preview: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }
        }

        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.SelectedPath = "G:\\My Drive\\LEAD";

            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string folderPath = dlg.SelectedPath;

                try
                {
                    await LoadFolder(dlg.SelectedPath);
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

        private async Task SetImgBoxSourceAsync(string filePath)
        {
            _currentLoadPreviewCts?.Cancel();
            _currentLoadPreviewCts = new CancellationTokenSource();
            var ctoken = _currentLoadPreviewCts.Token;

            if (string.IsNullOrEmpty(filePath))
            {
                await Dispatcher.InvokeAsync(() => ImgBox.Source = null).Task;
                return;
            }
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    return _explorer.Load<BitmapImage>(filePath);
                }, ctoken).ConfigureAwait(false);

                ctoken.ThrowIfCancellationRequested();

                if (bitmap == null)
                {
                    await Dispatcher.InvokeAsync(() => ImgBox.Source = null).Task;
                }
                await Dispatcher.InvokeAsync(() =>
                {
                    RenderOptions.SetBitmapScalingMode(ImgBox, BitmapScalingMode.LowQuality);
                    ImgBox.Source = bitmap;
                    Title = $"ImgViewer - {Path.GetFileName(filePath)}";
                }, DispatcherPriority.Render).Task;
                await Dispatcher.InvokeAsync(() =>
                {
                    RenderOptions.SetBitmapScalingMode(ImgBox, BitmapScalingMode.HighQuality);
                }, DispatcherPriority.ContextIdle).Task;
            }
            catch (OperationCanceledException)
            {
                // Load was cancelled, do nothing
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show
                (
                    $"Error loading image for preview: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    await SetImgBoxSourceAsync(dlg.FileName);
                }
                catch (OperationCanceledException)
                {
                    // Load was cancelled, do nothing
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show
                    (
                        $"Error loading image for preview: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
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
