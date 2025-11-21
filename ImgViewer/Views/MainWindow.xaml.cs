using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
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

        //private readonly ObservableCollection<PipeLineOperation> _pipeLineOperations = new();
        private readonly HashSet<PipelineOperation> _handlingOps = new();

        private PipelineOperation? _draggedOperation;
        private PipelineOperation? _activeOperation;
        private ListBoxItem? _activeContainer;
        private int _originalPipelineIndex = -1;
        private int _currentInsertionIndex = -1;
        private bool _isDragging;
        private bool _dropHandled;
        private Point _dragStartPoint;
        private DraggedItemAdorner? _draggedItemAdorner;
        private InsertionIndicatorAdorner? _insertionAdorner;

        private double EraseOffset = AppSettings.EraseOperationOffset;   // пикселей от левого/правого края
        private bool _eraseModeActive;
        private bool _operationErased;

        private bool _livePipelineRunning = false;
        private readonly HashSet<PipelineOperation> _liveRunning = new();

        // debounce для Live-пайплайна
        private CancellationTokenSource? _liveDebounceCts;
        private readonly TimeSpan _liveDebounceDelay = AppSettings.ParametersChangedDebounceDelay;

        public IViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                _viewModel = value;
            }
        }

        public Pipeline Pipeline => _pipeline;

        //public ObservableCollection<PipeLineOperation> PipeLineOperations => _pipeLineOperations;

        private readonly IAppManager _manager;
        private readonly Pipeline _pipeline;
        private IViewModel _viewModel;


        private CancellationTokenSource _cts;
        private CancellationTokenSource? _currentLoadPreviewCts;
        private CancellationTokenSource? _currentLoadThumbnailsCts;

        //private string _lastOpenedFolder = string.Empty;

        public MainWindow()
        {
            InitializeComponent();

            _cts = new CancellationTokenSource();
            _manager = new AppManager(this, _cts);
            _pipeline = new Pipeline(_manager);

            DataContext = _viewModel;


            _explorer = new FileProcessor(_cts.Token);
            _explorer.ErrorOccured += (msg) =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    System.Windows.MessageBox.Show(this, msg, " Explorer Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            };



            SubscribeParameterChangedHandlers();
            HookLiveHandlers();

        }

        private void UpdatePipeline(List<Operation> ops)
        {
            //_pipeline.SetOperationsToPipeline(ops);
            //InitializePipeLineOperations(ops);
        }

        private void ScheduleLivePipelineRun()
        {
            // отменяем предыдущий запланированный запуск
            _liveDebounceCts?.Cancel();

            var cts = new CancellationTokenSource();
            _liveDebounceCts = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    // ждём паузу между изменениями
                    await Task.Delay(_liveDebounceDelay, cts.Token);
                    cts.Token.ThrowIfCancellationRequested();

                    // если в этот момент уже идёт прогон — ждём, пока закончится
                    //while (_livePipelineRunning)
                    //{
                    //    await Task.Delay(50, cts.Token);
                    //    cts.Token.ThrowIfCancellationRequested();
                    //}

                    await RunLivePipelineFromOriginalAsync();
                }
                catch (TaskCanceledException)
                {
                    // это нормально — сработал debounce
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Debounced live pipeline run failed: {ex}");
                }
            });
        }

        private bool IsEraseDropPosition()
        {
            if (PipelineListBox == null)
                return false;

            var pos = Mouse.GetPosition(PipelineListBox);
            double width = PipelineListBox.ActualWidth;

            // "зона удаления" — когда ушли дальше, чем EraseOffset за левый/правый край списка
            return pos.X < -EraseOffset || pos.X > width + EraseOffset;
        }


        private async Task RunLivePipelineFromOriginalAsync()
        {
            if (_viewModel.OriginalImage == null) return;
            // защита от повторных запусков, пока предыдущий ещё работает
            if (_livePipelineRunning)
                return;

            _livePipelineRunning = true;
            try
            {
                // 1) сброс к оригиналу на UI-потоке
                try
                {
                    Dispatcher.Invoke(() => ResetPreview());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Reset failed in RunLivePipelineFromOriginalAsync: {ex}");
                }

                // 2) пройти по всем операциям в порядке списка
                foreach (var pipelineOp in _pipeline.Operations)
                {
                    if (!pipelineOp.InPipeline || !pipelineOp.Live)
                        continue;

                    // защита от параллельных запусков одной и той же операции
                    if (!_liveRunning.Add(pipelineOp))
                        continue;

                    try
                    {
                        await Task.Run(() =>
                        {
                            try
                            {
                                pipelineOp.Execute();
                            }
                            catch (Exception exExec)
                            {
                                Debug.WriteLine($"Live execution failed for {pipelineOp.DisplayName}: {exExec}");
                            }
                        });
                    }
                    finally
                    {
                        _liveRunning.Remove(pipelineOp);
                    }
                }
            }
            finally
            {
                _livePipelineRunning = false;
            }
        }

        private void HookLiveHandlers()
        {
            // attach to existing operations
            foreach (var op in _pipeline.Operations)
                op.LiveChanged += OnOperationLiveChanged;

            // attach to future additions/removals if collection changes
            if (_pipeline.Operations is INotifyCollectionChanged coll)
            {
                coll.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (PipelineOperation added in e.NewItems)
                            added.LiveChanged += OnOperationLiveChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (PipelineOperation removed in e.OldItems)
                            removed.LiveChanged -= OnOperationLiveChanged;
                    }
                };
            }
        }

        private void OnOperationLiveChanged(PipelineOperation op)
        {

            // при ЛЮБОМ изменении Live (ON/OFF) пересобираем весь pipeline
            _manager.CancelImageProcessing();
            if (op.Live)
            {
                ScheduleLivePipelineRun();
            }
        }

        private void SubscribeParameterChangedHandlers()
        {
            foreach (var op in _pipeline.Operations)
            {
                op.ParameterChanged += OnOperationParameterChanged;
            }

            // If PipeLineOperations can change at runtime, hook new items as well:
            if (_pipeline.Operations is INotifyCollectionChanged coll)
            {
                coll.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (PipelineOperation added in e.NewItems)
                            added.ParameterChanged += OnOperationParameterChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (PipelineOperation removed in e.OldItems)
                            removed.ParameterChanged -= OnOperationParameterChanged;
                    }
                };
            }
        }

        // --- Replace this existing method with the code below ---
        private void OnOperationParameterChanged(PipelineOperation op, PipeLineParameter? param)
        {
            // если операция не включена в pipeline, игнорируем изменение параметров
            if (!op.InPipeline || !op.Live)
                return;
            _manager.CancelImageProcessing();
            ScheduleLivePipelineRun();
        }

        private void StopProcessing_Click(object sender, RoutedEventArgs e)
        {
            _manager.CancelBatchProcessing();
            Debug.WriteLine("Stopping");
        }




        private void PipelineRunButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PipelineOperation operation)
            {
                operation.Execute();
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

        private async Task RunLiveOperationsForNewImageAsync()
        {
            await RunLivePipelineFromOriginalAsync();
        }




        private void PipelineListBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPoint = e.GetPosition(PipelineListBox);
            _activeContainer = ItemsControl.ContainerFromElement(PipelineListBox, (DependencyObject)e.OriginalSource) as ListBoxItem;
            _activeOperation = _activeContainer?.DataContext as PipelineOperation;
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
            if (!e.Data.GetDataPresent(typeof(PipelineOperation)) || _draggedOperation == null)
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
            if (!e.Data.GetDataPresent(typeof(PipelineOperation)) || _draggedOperation == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }

            // «сырая» позиция курсора относительно ListBox
            var rawPos = e.GetPosition(PipelineListBox);
            double width = PipelineListBox.ActualWidth;

            // включаем режим удаления, как и раньше
            bool erase =
                rawPos.X < EraseOffset ||
                rawPos.X > width - EraseOffset;

            _eraseModeActive = erase;
            _draggedItemAdorner?.SetEraseMode(erase);

            // КЛЭМПИМ X для визуального призрака:
            // не даём ему уйти дальше EraseOffset от краёв
            double clampedX = Math.Max(EraseOffset, Math.Min(rawPos.X, width - EraseOffset));
            var clampedPos = new Point(clampedX, rawPos.Y);

            // двигаем drag-ghost по зажатой позиции
            _draggedItemAdorner?.Update(clampedPos);

            if (erase)
            {
                // В режиме удаления не показываем линию вставки
                RemoveInsertionAdorner();
                e.Effects = DragDropEffects.None;
                e.Handled = true;
                return;
            }

            PipelineListBox.UpdateLayout();
            _currentInsertionIndex = GetInsertionIndex(PipelineListBox, rawPos); // для индекса используем сырую позицию
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
                //_eraseModeActive = false;
                //_draggedItemAdorner?.SetEraseMode(false);
            }
        }

        private async void PipelineListBox_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(typeof(PipelineOperation)) || _draggedOperation == null)
            {
                e.Effects = DragDropEffects.None;
                return;
            }




            if (_eraseModeActive)
            {



                // ----- РЕЖИМ УДАЛЕНИЯ -----
                _dropHandled = true;
                _operationErased = true;
                e.Effects = DragDropEffects.Move;
                e.Handled = true;

                // если ты уже работаешь через Pipeline:
                //_pipeline.Remove(_draggedOperation);

                // в текущей реализации _pipeLineOperations.Remove уже был
                // сделан в BeginPipelineDrag(), поэтому просто НЕ вставляем обратно

                _eraseModeActive = false;
                _draggedItemAdorner?.SetEraseMode(false);

                try
                {
                    await RunLiveOperationsForNewImageAsync();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Pipeline delete live-run failed: {ex}");
                }

                return;
            }

            int insertionIndex = Math.Max(0, Math.Min(_pipeline.Count, _currentInsertionIndex));
            _pipeline.Insert(insertionIndex, _draggedOperation);
            _dropHandled = true;

            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            // ----- NEW: после изменения порядка ресетим и перезапускаем live-пайплайн -----
            try
            {
                // RunLiveOperationsForNewImageAsync уже делает ResetPreview в начале, поэтому просто вызываем его.
                await RunLiveOperationsForNewImageAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Pipeline reorder live-run failed: {ex}");
            }
        }



        private void PipelineListBox_GiveFeedback(object sender, GiveFeedbackEventArgs e)
        {
            if (_draggedItemAdorner != null)
            {
                e.UseDefaultCursors = false;
                //_draggedItemAdorner.Update(Mouse.GetPosition(RootGrid));
                //_draggedItemAdorner.Update(e.GetPosition(PipelineListBox)); // вместо Mouse.GetPosition
                //Mouse.SetCursor(Cursors.Arrow);
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
            _eraseModeActive = false;
            _operationErased = false;
            _originalPipelineIndex = _pipeline.IndexOf(_activeOperation);
            _currentInsertionIndex = _originalPipelineIndex;

            var bitmap = CaptureElementBitmap(_activeContainer);
            var layer = AdornerLayer.GetAdornerLayer(PipelineListBox);
            if (bitmap != null && layer != null)
            {
                _draggedItemAdorner = new DraggedItemAdorner(PipelineListBox, layer, bitmap);
                _draggedItemAdorner.Update(Mouse.GetPosition(RootGrid));
            }

            _pipeline.Remove(_activeOperation);

            var dragData = new DataObject(typeof(PipelineOperation), _activeOperation);
            var effect = DragDrop.DoDragDrop(PipelineListBox, dragData, DragDropEffects.Move);

            CompleteDrag(effect != DragDropEffects.Move);
        }

        private void CompleteDrag(bool cancelled)
        {
            _draggedItemAdorner?.Remove();
            _draggedItemAdorner = null;
            RemoveInsertionAdorner();

            bool eraseOnCancel = false;

            if (cancelled && IsEraseDropPosition())
            {
                // тут мышь ушла далеко за края → пользователь явно "выбросил" карточку
                var res = System.Windows.MessageBox.Show(
                    $"WARNING! Are you sure you want to remove {_draggedOperation?.DisplayName} from current pipeline?",
                    "Confirm",
                    MessageBoxButton.OKCancel,
                    MessageBoxImage.Warning);

                eraseOnCancel = (res == MessageBoxResult.OK);
            }

            if (_draggedOperation != null && !_operationErased && !eraseOnCancel)
            {
                if (!_dropHandled || cancelled)
                {
                    int index = cancelled ? _originalPipelineIndex : _currentInsertionIndex;
                    index = Math.Max(0, Math.Min(_pipeline.Count, index));
                    _pipeline.Insert(index, _draggedOperation);
                }
            }

            _draggedOperation = null;
            _activeOperation = null;
            _activeContainer = null;
            _isDragging = false;
            _dropHandled = false;
            _originalPipelineIndex = -1;
            _currentInsertionIndex = -1;
            _operationErased = false;
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
            if (element == null)
                return null;

            // Обновляем layout, чтобы ActualWidth/ActualHeight были валидными
            element.UpdateLayout();

            // Границы содержимого элемента (в его собственной системе координат)
            var bounds = VisualTreeHelper.GetDescendantBounds(element);
            if (bounds.IsEmpty)
                return null;

            int width = (int)Math.Ceiling(bounds.Width);
            int height = (int)Math.Ceiling(bounds.Height);
            if (width <= 0 || height <= 0)
                return null;

            const double dpi = 96.0;
            var rtb = new RenderTargetBitmap(width, height, dpi, dpi, PixelFormats.Pbgra32);

            // Рисуем элемент через VisualBrush, чтобы "отвязаться" от глобальных координат
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
            {
                var brush = new VisualBrush(element)
                {
                    Stretch = Stretch.None,
                    AlignmentX = AlignmentX.Left,
                    AlignmentY = AlignmentY.Top

                };

                ctx.DrawRectangle(brush, null, new Rect(new Point(0, 0), new Size(width, height)));
            }

            rtb.Render(dv);
            return rtb;
        }


        private void ExecuteManagerCommand(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            _manager?.ApplyCommandToProcessingImage(command, parameters);
        }

        private void ExecuteProcessorCommand(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            _processor?.ApplyCommand(command, parameters);
        }

        private Dictionary<string, object> GetParametersFromSender(object sender)
        {
            if (sender is FrameworkElement element && element.DataContext is PipelineOperation operation)
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
            bitmap.Freeze();

            return bitmap;
        }


        private async void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "Image Files|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.tif;*.tiff|All Files|*.*";
            dlg.Multiselect = false;
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    //await SetImgBoxSourceAsync(dlg.FileName);
                    //await _mvm.LoadImagAsync(dlg.FileName);
                    var fileName = dlg.FileName;
                    await _manager.SetImageOnPreview(fileName);
                    _viewModel.CurrentImagePath = fileName;
                    _manager.LastOpenedFolder = System.IO.Path.GetDirectoryName(fileName);

                    await RunLiveOperationsForNewImageAsync();
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

        private void LoadPipelinePreset_Click(object sender, RoutedEventArgs e)
        {
            var res = System.Windows.MessageBox.Show($"WARNING! All unsaved parameters will be lost! Are you sure?",
                                                         "Confirm",
                                                         MessageBoxButton.OKCancel,
                                                         MessageBoxImage.Warning);
            if (res == MessageBoxResult.Cancel) return;
            LoadPipelineFromFile();
        }

        private void AddPipelineOperation_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
            {
                btn.ContextMenu.PlacementTarget = btn;
                btn.ContextMenu.IsOpen = true;
            }
        }

        private void AddOperationMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem menuItem)
                return;

            if (menuItem.Tag is not PipelineOperationType type)
                return;

            // создаём новую операцию нужного типа
            var op = _pipeline.CreatePipelineOperation(type);  // см. шаг 4

            // вставляем в начало pipeline (индекс 0)
            _pipeline.Insert(0, op);

            // опционально: сразу пересчитать live-pipeline
            ScheduleLivePipelineRun();
        }

        private void LoadPipelineFromFile()
        {

            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "IGpreset files|*.igpreset";
            List<Operation> pipeline = new List<Operation>();
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var fileName = dlg.FileName;
                    string json = File.ReadAllText(fileName);
                    _pipeline.LoadPipelineFromJson(json);
                }
                catch (OperationCanceledException)
                {
                    // Load was cancelled, do nothing
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show
                    (
                        $"Error loading preset: {ex.Message}",
                        "Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error
                    );
                }
            }

#if DEBUG
            foreach (var op in pipeline)
            {
                Debug.WriteLine($"Command: {op.Command}");
                foreach (var p in op.Parameters)
                {
                    Debug.WriteLine($"  {p.Name} = {p.Value} (type: {p.Value?.GetType().Name ?? "null"})");
                }
            }
#endif
        }

        private void SavePipelinePreset_Click(object sender, RoutedEventArgs e)
        {

            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.InitialDirectory = _manager.LastOpenedFolder;
            dlg.Filter = "*.igpreset|*.*";

            if (dlg.ShowDialog() == true)
            {
                var fileName = dlg.FileName;

                // если пользователь не указал расширение — добавим .igpreset
                if (!Path.HasExtension(fileName))
                    fileName += ".igpreset";

                var json = _pipeline.BuildPipelineForSave();
                Debug.WriteLine(json);

                _manager.SavePipelineToJSON(fileName, json);
                _manager.LastOpenedFolder = Path.GetDirectoryName(fileName);
            }


        }



        private void ResetPipelineToDefaults_Click(object sender, RoutedEventArgs e)
        {
            //TODO
            var res = System.Windows.MessageBox.Show($"WARNING! All parameters will be set to the default values! Are you sure?",
                                                         "Confirm",
                                                         MessageBoxButton.OKCancel,
                                                         MessageBoxImage.Warning);
            if (res == MessageBoxResult.Cancel) return;

            _pipeline.ResetToDefault();
        }


        private async void OpenNextFile_Click(object sender, RoutedEventArgs e)
        {
            await OpenSiblingFileAsync(true);
        }

        private async void OpenPrevFile_Click(object sender, RoutedEventArgs e)
        {
            await OpenSiblingFileAsync(false);
        }

        private async Task OpenSiblingFileAsync(bool next)
        {
            string[] _imageExtensions = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" };
            try
            {
                // проверим папку
                var folder = _manager.LastOpenedFolder;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    System.Windows.MessageBox.Show("Folder unknown or doesn't exist. Open a file first.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                // собрать файлы с нужными расширениями
                var files = Directory.GetFiles(folder)
                                     .Where(f => _imageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                                     .ToArray();

                int i = Array.IndexOf(files, _viewModel.CurrentImagePath);
                if (i == -1)
                    return;
                int newIdx = next ? i + 1 : i - 1;
                if (newIdx < 0 || newIdx >= files.Length) return;

                var target = files[newIdx];
                await _manager.SetImageOnPreview(target);
                _viewModel.CurrentImagePath = target;
                _manager.LastOpenedFolder = folder;

                await RunLiveOperationsForNewImageAsync();
            }
            catch (OperationCanceledException)
            {
                // cancelled — игнорируем
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    $"Error loading neighboring file: {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ApplyDeskew(object sender, RoutedEventArgs e)
        {
            ExecuteManagerCommand(ProcessorCommand.Deskew, GetParametersFromSender(sender));
        }

        private void ApplyAutoCropRectangleCurrentCommand(object sender, RoutedEventArgs e)
        {
            ExecuteManagerCommand(ProcessorCommand.AutoCropRectangle, GetParametersFromSender(sender));
        }

        private void ApplyDespeckleCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommand.Despeckle, GetParametersFromSender(sender));
        }


        private void ApplyBorderRemoveCommand_Click(object sender, RoutedEventArgs e)
        {

            ExecuteManagerCommand(ProcessorCommand.BordersRemove, GetParametersFromSender(sender));
        }

        private void ApplyAutoBinarizeCommand(object sender, RoutedEventArgs e)
        {
            ExecuteManagerCommand(ProcessorCommand.Binarize, GetParametersFromSender(sender));
        }

        private void ApplyLineRemoveCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommand.LineRemove, GetParametersFromSender(sender));
        }

        private void ApplyPunchesRemoveCommand(object sender, RoutedEventArgs e)
        {
            ExecuteProcessorCommand(ProcessorCommand.DotsRemove, GetParametersFromSender(sender));
        }


        private (ProcessorCommand Value, Dictionary<string, object>)[]? GetPipelineParameters()
        {
            var pl = _pipeline.Operations
                    .Where(op => op.InPipeline && op.Command.HasValue)
                    .Select(op => (op.Command.Value, op.CreateParameterDictionary()))
                    .ToArray();

            return pl;
        }

        private void ApplyCurrentPipelineToSelectedRootFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_pipeline == null) return;
            if (_pipeline.Operations.Count == 0)
            {
                System.Windows.MessageBox.Show("Pipeline is empty — choose at least one operation before running.", "Warning!", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string rootFolder = string.Empty;
            var dlg = new System.Windows.Forms.FolderBrowserDialog();
            dlg.SelectedPath = _manager.LastOpenedFolder;
            if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                rootFolder = dlg.SelectedPath;
            }
            if (rootFolder == string.Empty) return;
            _manager.LastOpenedFolder = rootFolder;

            // опционально: спросить подтверждение у пользователя
            var res = System.Windows.MessageBox.Show($"Apply current pipeline to all sub-folders in:\n{rootFolder} ?",
                                                     "Confirm",
                                                     MessageBoxButton.OKCancel,
                                                     MessageBoxImage.Question);
            if (res != MessageBoxResult.OK) return;


            try
            {
                _manager.ProcessRootFolder(rootFolder, _pipeline, true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to process Root folder {rootFolder}: {ex.Message}",
                                               "Error",
                                               MessageBoxButton.OK,
                                               MessageBoxImage.Error);
            }

        }

        private void ApplyCurrentPipelineToCurrentFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // попытка взять папку  из пути текущего файла
                //string? folder = _viewModel?.LastOpenedFolder;


                var pipeline = GetPipelineParameters();

                if (pipeline.Length == 0)
                {
                    System.Windows.MessageBox.Show("Pipeline is empty — choose at least one operation before running.", "Warning!", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                string folder = string.Empty;
                if (string.IsNullOrWhiteSpace(folder) && !string.IsNullOrWhiteSpace(_viewModel?.CurrentImagePath))
                {
                    folder = System.IO.Path.GetDirectoryName(_viewModel.CurrentImagePath);
                }

                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    System.Windows.MessageBox.Show("Folder unknown or doesn't exist. Open a file first.",
                                                   "Info",
                                                   MessageBoxButton.OK,
                                                   MessageBoxImage.Information);
                    return;
                }

                // опционально: спросить подтверждение у пользователя
                var res = System.Windows.MessageBox.Show($"Apply current pipeline to all images in:\n{folder} ?",
                                                         "Confirm",
                                                         MessageBoxButton.OKCancel,
                                                         MessageBoxImage.Question);
                if (res != MessageBoxResult.OK) return;






                // вызываем менеджер, передавая команды
                _manager.ProcessFolder(folder, _pipeline);


                //_manager.ProcessFolder(folder);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to process folder: {ex.Message}",
                                               "Error",
                                               MessageBoxButton.OK,
                                               MessageBoxImage.Error);
            }
        }

        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            //if (_viewModel.OriginalImage == null) return;
            //var originalImage = _viewModel.OriginalImage;

            //_viewModel.ImageOnPreview = originalImage;
            //_manager.SetImageForProcessing(originalImage);
            Dispatcher.Invoke(() => ResetPreview());
        }

        private async void ResetPreview()
        {
            if (_viewModel.OriginalImage == null) return;
            await _manager.SetImageForProcessing(_viewModel.OriginalImage);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel.OriginalImage == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.InitialDirectory = _manager.LastOpenedFolder;
            //dlg.Filter = "TIFF Image|*.tif;*.tiff|PNG Image|*.png|JPEG Image|*.jpg;*.jpeg|Bitmap Image|*.bmp|All Files|*.*";
            dlg.Filter = "TIFF Image|*.tif;*.tiff";

            if (dlg.ShowDialog() == true)
            {
                var path = dlg.FileName;
                TiffCompression compression = _manager.CurrentTiffCompression;
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                //if (ext == ".tif" || ext == ".tiff")
                //{
                //    var tiffOptionsWindow = new TiffSavingOptionsWindow();
                //    tiffOptionsWindow.Owner = this;

                //    if (tiffOptionsWindow.ShowDialog() == true)
                //    {
                //        compression = tiffOptionsWindow.SelectedCompression;
                //    }
                //    else
                //    {
                //        // User cancelled the TIFF options dialog
                //        return;
                //    }
                //}
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

        private void SavingOptions_Click(object sender, RoutedEventArgs e)
        {


            var tiffOptionsWindow = new TiffSavingOptionsWindow();
            tiffOptionsWindow.Owner = this;

            if (tiffOptionsWindow.ShowDialog() == true)
            {
                _manager.CurrentTiffCompression = tiffOptionsWindow.SelectedCompression;
                _viewModel.TiffCompressionLabel = tiffOptionsWindow.SelectedCompression.ToString();
            }
            else
            {
                // User cancelled the TIFF options dialog
                return;
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
            private bool _eraseMode;

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

            public void SetEraseMode(bool erase)
            {
                if (_eraseMode == erase)
                    return;

                _eraseMode = erase;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                // Корректно пересчитаем размер из пикселей в DIPs
                double scaleX = _bitmap.DpiX / 96.0;
                double scaleY = _bitmap.DpiY / 96.0;

                var widthDip = _bitmap.PixelWidth / scaleX;
                var heightDip = _bitmap.PixelHeight / scaleY;

                var size = new Size(widthDip, heightDip);

                var rect = new Rect(
                    new Point(_position.X - size.Width / 2, _position.Y - size.Height / 2),
                    size);

                if (_eraseMode)
                {
                    // полупрозрачный ghost + красный overlay ровно по тому же rect
                    drawingContext.PushOpacity(0.5);
                    drawingContext.DrawImage(_bitmap, rect);
                    drawingContext.Pop();

                    var redBrush = new SolidColorBrush(Color.FromArgb(160, 255, 0, 0));
                    drawingContext.DrawRectangle(redBrush, null, rect);
                }
                else
                {
                    drawingContext.PushOpacity(0.8);
                    drawingContext.DrawImage(_bitmap, rect);
                    drawingContext.Pop(); // важно!
                }
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


}
