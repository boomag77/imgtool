using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using GiveFeedbackEventArgs = System.Windows.GiveFeedbackEventArgs;
using ListBox = System.Windows.Controls.ListBox;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Size = System.Windows.Size;


namespace ImgViewer.Views
{
    public partial class MainWindow : Window, IMainView
    {
        private readonly IImageProcessor _processor;
        private readonly IFileProcessor _explorer;
        private readonly ObservableCollection<PipeLineOperation> _pipeLineOperations = new();

        private PipeLineOperation? _draggedOperation;
        private PipeLineOperation? _activeOperation;
        private ListBoxItem? _activeContainer;
        private int _originalPipelineIndex = -1;
        private int _currentInsertionIndex = -1;
        private bool _isDragging;
        private bool _dropHandled;
        private Point _dragStartPoint;
        private DraggedItemAdorner? _draggedItemAdorner;
        private InsertionIndicatorAdorner? _insertionAdorner;

        public IViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                _viewModel = value;
            }
        }

        public ObservableCollection<PipeLineOperation> PipeLineOperations => _pipeLineOperations;

        private readonly IAppManager _manager;
        private IViewModel _viewModel;


        private CancellationTokenSource _cts;
        private CancellationTokenSource? _currentLoadPreviewCts;
        private CancellationTokenSource? _currentLoadThumbnailsCts;

        private string _lastOpenedFolder = string.Empty;



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
                //try
                //{
                //    _localCts?.Cancel();
                //    _localCts?.Dispose();
                //}
                //catch { /* ignore */ }

                //_localCts = CancellationTokenSource.CreateLinkedTokenSource(_parentToken, token);
                //var ct = _localCts.Token;

                //await _semaphore.WaitAsync(ct).ConfigureAwait(false);
                //try
                //{
                //    if (ct.IsCancellationRequested)
                //        return;
                //    var bmp = await Task.Run(() => Explorer.Load<BitmapImage>(Path, 50), ct).ConfigureAwait(false);
                //    if (!ct.IsCancellationRequested && bmp != null)
                //    {
                //        await _dispatcher.InvokeAsync(() => Thumb = bmp).Task.ConfigureAwait(false);
                //    }
                //}
                //finally
                //{
                //    try { _semaphore.Release(); } catch { }
                //    try
                //    {
                //        _localCts?.Dispose();
                //    }
                //    catch { }
                //    _localCts = null;
                //}
            }


            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string name = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }



        //public ObservableCollection<Thumbnail> Files { get; set; } = new ObservableCollection<Thumbnail>();

        public MainWindow()
        {
            InitializeComponent();

            _manager = new AppManager(this);
            DataContext = _viewModel;
            _cts = new CancellationTokenSource();

            //ImgListBox.ItemsSource = Files;

            _explorer = new FileProcessor(_cts.Token);
            _explorer.ErrorOccured += (msg) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(this, msg, " Explorer Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };

            InitializePipeLineOperations();

        }

        private void InitializePipeLineOperations()
        {
            _pipeLineOperations.Clear();

            _pipeLineOperations.Add(new PipeLineOperation(
                "Open Image",
                "Open",
                Array.Empty<PipeLineParameter>(),
                (window, operation) => window.OpenFile_Click(window, new RoutedEventArgs())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Deskew",
                "Run",
                new[]
                {
                    new PipeLineParameter("Angle", "DeskewAngle", 0, -15, 15, 0.5),
                    new PipeLineParameter("Threshold", "DeskewThreshold", 0.15, 0, 1, 0.05)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.Deskew, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Border Cleanup",
                "Run",
                new[]
                {
                    new PipeLineParameter("Margin", "BorderMargin", 4, 0, 100, 1),
                    new PipeLineParameter("Sensitivity", "BorderSensitivity", 0.5, 0, 1, 0.05)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.BorderRemove, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Auto Crop",
                "Run",
                new[]
                {
                    new PipeLineParameter("Padding", "CropPadding", 8, 0, 100, 1)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.AutoCropRectangle, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Despeckle",
                "Run",
                new[]
                {
                    new PipeLineParameter("Strength", "DespeckleStrength", 3, 1, 10, 1)
                },
                (window, operation) => window.ExecuteProcessorCommand(ProcessorCommands.Despeckle, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Auto Binarize",
                "Run",
                new[]
                {
                    new PipeLineParameter("Threshold", "BinarizeThreshold", 128, 0, 255, 5)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.Binarize, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Line Removal",
                "Run",
                new[]
                {
                    new PipeLineParameter("Width", "LineWidth", 2, 1, 10, 1)
                },
                (window, operation) => window.ExecuteProcessorCommand(ProcessorCommands.LineRemove, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Punch Removal",
                "Run",
                new[]
                {
                    new PipeLineParameter("Radius", "PunchRadius", 6, 1, 30, 1)
                },
                (window, operation) => window.ExecuteProcessorCommand(ProcessorCommands.DotsRemove, operation.CreateParameterDictionary())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Batch Processing",
                "Process Folder",
                new[]
                {
                    new PipeLineParameter("Parallel", "Parallelism", 1, 1, Environment.ProcessorCount, 1)
                },
                (window, operation) => window.ProcessFolderClick(window, new RoutedEventArgs())));

            _pipeLineOperations.Add(new PipeLineOperation(
                "Save Output",
                "Save As...",
                new[]
                {
                    new PipeLineParameter("Compression", "CompressionLevel", 0, 0, 10, 1)
                },
                (window, operation) => window.SaveAsClick(window, new RoutedEventArgs())));
        }

        private void PipelineRunButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PipeLineOperation operation)
            {
                operation.Execute(this);
                e.Handled = true;
            }
        }

        private void SpinnerUp_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PipeLineParameter parameter)
            {
                parameter.Increment();
                e.Handled = true;
            }
        }

        private void SpinnerDown_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PipeLineParameter parameter)
            {
                parameter.Decrement();
                e.Handled = true;
            }
        }

        private void PipelineListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(PipelineListBox);
            _activeContainer = ItemsControl.ContainerFromElement(PipelineListBox, (DependencyObject)e.OriginalSource) as ListBoxItem;
            _activeOperation = _activeContainer?.DataContext as PipeLineOperation;
            _isDragging = false;
            _dropHandled = false;
        }

        private void PipelineListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _isDragging)
            {
                return;
            }

            if (_activeOperation == null || _activeContainer == null)
            {
                return;
            }

            var position = e.GetPosition(PipelineListBox);
            if (Math.Abs(position.X - _dragStartPoint.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(position.Y - _dragStartPoint.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                BeginPipelineDrag();
            }
        }

        private void PipelineListBox_DragEnter(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(PipeLineOperation)) || _draggedOperation == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            EnsureInsertionAdorner();
            _insertionAdorner?.Update(PipelineListBox, _currentInsertionIndex);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void PipelineListBox_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(PipeLineOperation)) || _draggedOperation == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            var position = e.GetPosition(PipelineListBox);
            PipelineListBox.UpdateLayout();
            _currentInsertionIndex = GetInsertionIndex(PipelineListBox, position);
            EnsureInsertionAdorner();
            _insertionAdorner?.Update(PipelineListBox, _currentInsertionIndex);
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void PipelineListBox_DragLeave(object sender, DragEventArgs e)
        {
            if (!PipelineListBox.IsMouseOver)
            {
                RemoveInsertionAdorner();
            }
        }

        private void PipelineListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(PipeLineOperation)) || _draggedOperation == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            int insertionIndex = Math.Max(0, Math.Min(_pipeLineOperations.Count, _currentInsertionIndex));
            _pipeLineOperations.Insert(insertionIndex, _draggedOperation);
            _dropHandled = true;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private void PipelineListBox_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_draggedItemAdorner != null)
            {
                e.UseDefaultCursors = false;
                _draggedItemAdorner.Update(Mouse.GetPosition(PipelineListBox));
                Mouse.SetCursor(Cursors.Arrow);
                e.Handled = true;
            }
            else
            {
                e.UseDefaultCursors = true;
            }
        }

        private void BeginPipelineDrag()
        {
            if (_activeOperation == null || _activeContainer == null)
            {
                return;
            }

            _isDragging = true;
            _draggedOperation = _activeOperation;
            _originalPipelineIndex = _pipeLineOperations.IndexOf(_activeOperation);
            _currentInsertionIndex = _originalPipelineIndex;

            var bitmap = CaptureElementBitmap(_activeContainer);
            var layer = AdornerLayer.GetAdornerLayer(PipelineListBox);
            if (bitmap != null && layer != null)
            {
                _draggedItemAdorner = new DraggedItemAdorner(PipelineListBox, layer, bitmap);
                _draggedItemAdorner.Update(Mouse.GetPosition(PipelineListBox));
            }

            _pipeLineOperations.Remove(_activeOperation);

            var dragData = new DataObject(typeof(PipeLineOperation), _activeOperation);
            var effect = DragDrop.DoDragDrop(PipelineListBox, dragData, DragDropEffects.Move);

            CompleteDrag(effect != DragDropEffects.Move);
        }

        private void CompleteDrag(bool cancelled)
        {
            _draggedItemAdorner?.Remove();
            _draggedItemAdorner = null;
            RemoveInsertionAdorner();

            if (_draggedOperation != null)
            {
                if (!_dropHandled || cancelled)
                {
                    int index = cancelled ? _originalPipelineIndex : _currentInsertionIndex;
                    index = Math.Max(0, Math.Min(_pipeLineOperations.Count, index));
                    _pipeLineOperations.Insert(index, _draggedOperation);
                }
            }

            _draggedOperation = null;
            _activeOperation = null;
            _activeContainer = null;
            _isDragging = false;
            _dropHandled = false;
            _originalPipelineIndex = -1;
            _currentInsertionIndex = -1;
        }

        private void EnsureInsertionAdorner()
        {
            if (_insertionAdorner != null)
            {
                return;
            }

            var layer = AdornerLayer.GetAdornerLayer(PipelineListBox);
            if (layer != null)
            {
                _insertionAdorner = new InsertionIndicatorAdorner(PipelineListBox, layer);
            }
        }

        private void RemoveInsertionAdorner()
        {
            _insertionAdorner?.Remove();
            _insertionAdorner = null;
        }

        private int GetInsertionIndex(ListBox listBox, Point position)
        {
            for (int i = 0; i < listBox.Items.Count; i++)
            {
                if (listBox.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem item)
                {
                    continue;
                }

                var topLeft = item.TranslatePoint(new Point(0, 0), listBox);
                double midpoint = topLeft.Y + item.ActualHeight / 2;
                if (position.Y < midpoint)
                {
                    return i;
                }
            }

            return listBox.Items.Count;
        }

        private RenderTargetBitmap? CaptureElementBitmap(FrameworkElement element)
        {
            element.UpdateLayout();

            int width = (int)Math.Ceiling(element.ActualWidth);
            int height = (int)Math.Ceiling(element.ActualHeight);
            if (width <= 0 || height <= 0)
            {
                return null;
            }

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            return bitmap;
        }

        private void ExecuteManagerCommand(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            _manager?.ApplyCommandToProcessingImage(command, parameters);
        }

        private void ExecuteProcessorCommand(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            _processor?.ApplyCommandToCurrent(command, parameters);
        }

        private Dictionary<string, object> GetParametersFromSender(object sender)
        {
            if (sender is FrameworkElement element && element.DataContext is PipeLineOperation operation)
            {
                return operation.CreateParameterDictionary();
            }

            return new Dictionary<string, object>();
        }

        public void UpdatePreview(Stream stream)
        {
            var bitmap = streamToBitmapSource(stream);
            Dispatcher.InvokeAsync(() => PreviewImgBox.Source = bitmap);
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
            bitmap.Freeze(); // ????? ????? ???? ???????????? ?? ?????? ??????

            return bitmap;
        }

        //private async Task LoadFolder(string folderPath)
        //{
        //    _currentLoadThumbnailsCts?.Cancel();
        //    _currentLoadThumbnailsCts = new CancellationTokenSource();
        //    var ct = _currentLoadThumbnailsCts.Token;


        //    await Dispatcher.InvokeAsync(() =>
        //    {
        //        foreach (var old in Files.OfType<IDisposable>().ToList())
        //        {
        //            if (ct.IsCancellationRequested)
        //                return;
        //            old.Dispose();
        //        }


        //        Files.Clear();
        //    }, DispatcherPriority.Background).Task;

        //    string[] filePaths = await Task.Run(() =>
        //    {
        //        if (!Directory.Exists(folderPath))
        //            throw new DirectoryNotFoundException($"Directory does not exist: {folderPath}");
        //        return Directory.GetFiles(folderPath, "*.*")
        //                 .Where(file =>
        //                     file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
        //                     file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
        //                     file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
        //                     file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
        //                 .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase) // sort by name
        //                 .ToArray();
        //    }, ct);
        //    if (ct.IsCancellationRequested)
        //        return;
        //    const int preLoadCount = 20;
        //    const int batchSize = 10;
        //    int filesCount = filePaths.Length;
        //    for (int i = 0; i < filePaths.Length; i += batchSize)
        //    {
        //        if (ct.IsCancellationRequested)
        //            return;
        //        int end = Math.Min(i + batchSize, filesCount);
        //        var batch = new List<Thumbnail>(end - i);
        //        for (int j = i; j < end; j++)
        //        {
        //            if (ct.IsCancellationRequested)
        //                return;
        //            var item = filePaths[j];
        //            var thumb = new Thumbnail(ct, this.Dispatcher, _explorer, item, j < preLoadCount);
        //            batch.Add(thumb);
        //            Files.Add(thumb);
        //        }
        //        foreach (var t in batch)
        //        {
        //            _ = t.LoadThumbAsync(ct); // ?? await, ????? LoadThumbAsync ??? ??????? ??????????
        //        }
        //        await Dispatcher.Yield(DispatcherPriority.Background);
        //    }
        //    await Dispatcher.InvokeAsync(() =>
        //    {
        //        if (Files.Count > 0)
        //            ImgListBox.SelectedIndex = 0;
        //    }, DispatcherPriority.Background).Task;
        //}

        //private async void ImgList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        //{
        //    if (ImgListBox.SelectedItem is Thumbnail item)
        //    {
        //        //try
        //        //{
        //        //    await SetImgBoxSourceAsync(item.Path);
        //        //    _processor.Load(item.Path);
        //        //}
        //        //catch (Exception ex)
        //        //{
        //        //    System.Windows.MessageBox.Show
        //        //    (
        //        //        $"Error loading image for preview: {ex.Message}",
        //        //        "Error",
        //        //        MessageBoxButton.OK,
        //        //        MessageBoxImage.Error
        //        //    );
        //        //}
        //    }
        //}

        //private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        //{
        //    var dlg = new System.Windows.Forms.FolderBrowserDialog();

        //    if (!string.IsNullOrEmpty(_lastOpenedFolder) && Directory.Exists(_lastOpenedFolder))
        //        dlg.SelectedPath = _lastOpenedFolder;
        //    else if (Directory.Exists("G:\\My Drive\\LEAD"))
        //        dlg.SelectedPath = "G:\\My Drive\\LEAD";

        //    if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        //    {
        //        string folderPath = dlg.SelectedPath;

        //        try
        //        {
        //            await LoadFolder(dlg.SelectedPath);
        //            _lastOpenedFolder = dlg.SelectedPath;
        //            Title = $"ImgViewer - {dlg.SelectedPath}";

        //            if (Files.Count > 0)
        //                ImgListBox.SelectedIndex = 0;
        //        }
        //        catch (OperationCanceledException)
        //        {
        //            Console.WriteLine("???????? ????????.");
        //        }
        //    }
        //}

        //private async Task SetImgBoxSourceAsync(string filePath)
        //{
        //    _currentLoadPreviewCts?.Cancel();
        //    _currentLoadPreviewCts = new CancellationTokenSource();
        //    var ctoken = _currentLoadPreviewCts.Token;

        //    if (string.IsNullOrEmpty(filePath))
        //    {
        //        await Dispatcher.InvokeAsync(() => ImgBox.Source = null).Task;
        //        return;
        //    }
        //    try
        //    {
        //        var bitmap = await Task.Run(() =>
        //        {
        //            return _explorer.Load<BitmapSource>(filePath);
        //        }, ctoken).ConfigureAwait(false);

        //        ctoken.ThrowIfCancellationRequested();

        //        if (bitmap == null)
        //        {
        //            await Dispatcher.InvokeAsync(() => ImgBox.Source = null).Task;
        //        }
        //        await Dispatcher.InvokeAsync(() =>
        //        {
        //            RenderOptions.SetBitmapScalingMode(ImgBox, BitmapScalingMode.LowQuality);
        //            ImgBox.Source = bitmap;
        //            Title = $"ImgViewer - {Path.GetFileName(filePath)}";
        //        }, DispatcherPriority.Render).Task;
        //        await Dispatcher.InvokeAsync(() =>
        //        {
        //            RenderOptions.SetBitmapScalingMode(ImgBox, BitmapScalingMode.HighQuality);
        //        }, DispatcherPriority.ContextIdle).Task;
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        // Load was cancelled, do nothing
        //    }
        //    catch (Exception ex)
        //    {
        //        System.Windows.MessageBox.Show
        //        (
        //            $"Error loading image for preview: {ex.Message}",
        //            "Error",
        //            MessageBoxButton.OK,
        //            MessageBoxImage.Error
        //        );
        //    }
        //}

        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*";
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    //await SetImgBoxSourceAsync(dlg.FileName);
                    //await _mvm.LoadImagAsync(dlg.FileName);
                    var fileName = dlg.FileName;
                    await _manager.SetImageOnPreview(fileName);
                    _viewModel.LastOpenedFolder = Path.GetDirectoryName(fileName);
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
            ExecuteManagerCommand(ProcessorCommands.Deskew, GetParametersFromSender(sender));
        }

        private void ApplyAutoCropRectangleCurrentCommand(object sender, RoutedEventArgs e)
        {
            ExecuteManagerCommand(ProcessorCommands.AutoCropRectangle, GetParametersFromSender(sender));
        }

        private void ApplyDespeckleCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommands.Despeckle, GetParametersFromSender(sender));
        }


        private void ApplyBorderRemoveCommand_Click(object sender, RoutedEventArgs e)
        {

            ExecuteManagerCommand(ProcessorCommands.BorderRemove, GetParametersFromSender(sender));
        }

        private void ApplyAutoBinarizeCommand(object sender, RoutedEventArgs e)
        {
            ExecuteManagerCommand(ProcessorCommands.Binarize, GetParametersFromSender(sender));
        }

        private void ApplyLineRemoveCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommands.LineRemove, GetParametersFromSender(sender));
        }

        private void ApplyPunchesRemoveCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommands.DotsRemove, GetParametersFromSender(sender));
        }

        private void ProcessFolderClick(object sender, RoutedEventArgs e)
        {
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string folderPath = dlg.SelectedPath;
                ProcessorCommands[] commands =
                {
                    ProcessorCommands.Binarize,
                };
                var token = _cts.Token;
                var fileExplorer = new FileProcessor(token);
                var imgProcessor = new OpenCVImageProcessor(_manager, token);
                var sourceFolder = fileExplorer.GetImageFilesPaths(folderPath);
                _manager.ProcessFolder(folderPath);

                //var workerPool = new ImgWorkerPool(_cts, commands, 1, imgProcessor, fileExplorer, sourceFolder, 0);
                //StatusText.Text = "Processing...";
                //await Task.Yield();
                //workerPool.ProgressChanged += (done, total) =>
                //{
                //    Dispatcher.InvokeAsync(() =>
                //    {
                //        MyProgressBar.Maximum = total;
                //        MyProgressBar.Value = done;
                //    });
                //};
                //workerPool.ErrorOccured += (msg) =>
                //{
                //    Dispatcher.InvokeAsync(() =>
                //    {
                //        System.Windows.MessageBox.Show(this, msg, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                //    });
                //};
                //try
                //{
                //    await workerPool.RunAsync();
                //}
                //catch (OperationCanceledException)
                //{
                //    StatusText.Text = "Cancelled";

                //}
            }
            //StatusText.Text = "Ready";
            //MyProgressBar.Value = 0;
        }

        private void SaveAsClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.InitialDirectory = _lastOpenedFolder;
            //dlg.Filter = "TIFF Image|*.tif;*.tiff|PNG Image|*.png|JPEG Image|*.jpg;*.jpeg|Bitmap Image|*.bmp|All Files|*.*";
            dlg.Filter = "TIFF Image|*.tif;*.tiff";

            if (dlg.ShowDialog() == true)
            {
                var path = dlg.FileName;
                TiffCompression compression = TiffCompression.None;
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".tif" || ext == ".tiff")
                {
                    var tiffOptionsWindow = new TiffSavingOptionsWindow();
                    tiffOptionsWindow.Owner = this;

                    if (tiffOptionsWindow.ShowDialog() == true)
                    {
                        compression = tiffOptionsWindow.SelectedCompression;
                    }
                    else
                    {
                        // User cancelled the TIFF options dialog
                        return;
                    }
                }
                _manager.SaveProcessedImage(path,
                    ext switch
                    {
                        ".tif" or ".tiff" => ImageFormat.Tiff,
                        ".png" => ImageFormat.Png,
                        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                        ".bmp" => ImageFormat.Bmp,
                        _ => ImageFormat.Png
                    },
                    compression);
            }

        }

        private void ExitClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private sealed class DraggedItemAdorner : Adorner
        {
            private readonly RenderTargetBitmap _bitmap;
            private readonly AdornerLayer _layer;
            private Point _position;

            public DraggedItemAdorner(UIElement adornedElement, AdornerLayer layer, RenderTargetBitmap bitmap)
                : base(adornedElement)
            {
                _layer = layer;
                _bitmap = bitmap;
                IsHitTestVisible = false;
                _layer.Add(this);
            }

            public void Update(Point position)
            {
                _position = position;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                var size = new Size(_bitmap.Width, _bitmap.Height);
                var topLeft = new Point(_position.X - size.Width / 2, _position.Y - size.Height / 2);
                drawingContext.DrawImage(_bitmap, new Rect(topLeft, size));
            }

            public void Remove()
            {
                _layer.Remove(this);
            }
        }

        private sealed class InsertionIndicatorAdorner : Adorner
        {
            private readonly AdornerLayer _layer;
            private readonly Brush _brush;
            private readonly Pen _pen;
            private double _y;

            public InsertionIndicatorAdorner(UIElement adornedElement, AdornerLayer layer)
                : base(adornedElement)
            {
                _layer = layer;
                IsHitTestVisible = false;

                var color = Color.FromRgb(220, 64, 64);
                _brush = new SolidColorBrush(color);
                _brush.Freeze();
                _pen = new Pen(_brush, 2)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round
                };
                _pen.Freeze();

                _layer.Add(this);
            }

            public void Update(ListBox listBox, int index)
            {
                _y = CalculateY(listBox, index);
                InvalidateVisual();
            }

            private double CalculateY(ListBox listBox, int index)
            {
                if (listBox.Items.Count == 0)
                {
                    return 0;
                }

                if (index <= 0)
                {
                    if (listBox.ItemContainerGenerator.ContainerFromIndex(0) is FrameworkElement first)
                    {
                        return first.TranslatePoint(new Point(0, 0), listBox).Y;
                    }

                    return 0;
                }

                if (index >= listBox.Items.Count)
                {
                    if (listBox.ItemContainerGenerator.ContainerFromIndex(listBox.Items.Count - 1) is FrameworkElement last)
                    {
                        return last.TranslatePoint(new Point(0, last.ActualHeight), listBox).Y;
                    }

                    return listBox.RenderSize.Height;
                }

                var previous = listBox.ItemContainerGenerator.ContainerFromIndex(index - 1) as FrameworkElement;
                var next = listBox.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

                if (previous != null && next != null)
                {
                    double prevBottom = previous.TranslatePoint(new Point(0, previous.ActualHeight), listBox).Y;
                    double nextTop = next.TranslatePoint(new Point(0, 0), listBox).Y;
                    return prevBottom + (nextTop - prevBottom) / 2;
                }

                if (previous != null)
                {
                    return previous.TranslatePoint(new Point(0, previous.ActualHeight), listBox).Y;
                }

                if (next != null)
                {
                    return next.TranslatePoint(new Point(0, 0), listBox).Y;
                }

                return 0;
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                double width = AdornedElement.RenderSize.Width;
                drawingContext.DrawLine(_pen, new Point(0, _y), new Point(width, _y));

                var geometry = new StreamGeometry();
                using (var context = geometry.Open())
                {
                    context.BeginFigure(new Point(0, _y), true, true);
                    context.LineTo(new Point(10, _y - 5), true, false);
                    context.LineTo(new Point(10, _y + 5), true, false);
                }
                geometry.Freeze();
                drawingContext.DrawGeometry(_brush, null, geometry);
            }

            public void Remove()
            {
                _layer.Remove(this);
            }
        }
    }

    public class PipeLineOperation
    {
        private readonly ObservableCollection<PipeLineParameter> _parameters;
        private readonly Action<MainWindow, PipeLineOperation>? _execute;

        public PipeLineOperation(string displayName, string actionLabel, IEnumerable<PipeLineParameter> parameters, Action<MainWindow, PipeLineOperation> execute)
        {
            DisplayName = displayName;
            ActionLabel = actionLabel;
            _parameters = new ObservableCollection<PipeLineParameter>(parameters ?? Enumerable.Empty<PipeLineParameter>());
            _execute = execute;
        }

        public string DisplayName { get; }

        public string ActionLabel { get; }

        public ObservableCollection<PipeLineParameter> Parameters => _parameters;

        public void Execute(MainWindow window)
        {
            _execute?.Invoke(window, this);
        }

        public Dictionary<string, object> CreateParameterDictionary()
        {
            return _parameters.ToDictionary(parameter => parameter.Key, parameter => (object)parameter.Value);
        }
    }

    public class PipeLineParameter : INotifyPropertyChanged
    {
        private readonly double _min;
        private readonly double _max;
        private double _value;

        public PipeLineParameter(string label, string key, double value, double min, double max, double step)
        {
            Label = label;
            Key = key;
            _min = min;
            _max = max;
            Step = step <= 0 ? 1 : step;
            _value = Clamp(value);
        }

        public string Label { get; }

        public string Key { get; }

        public double Step { get; }

        public double Value
        {
            get => _value;
            set
            {
                var clamped = Clamp(value);
                if (!AreClose(_value, clamped))
                {
                    _value = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public void Increment()
        {
            Value += Step;
        }

        public void Decrement()
        {
            Value -= Step;
        }

        private double Clamp(double value)
        {
            if (!double.IsNaN(_min))
            {
                value = Math.Max(_min, value);
            }

            if (!double.IsNaN(_max))
            {
                value = Math.Min(_max, value);
            }

            return value;
        }

        private static bool AreClose(double value1, double value2)
        {
            return Math.Abs(value1 - value2) < 0.0001;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
