using ImgViewer.Interfaces;
using ImgViewer.Models;
using ImgViewer.Models.Onnx;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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
        //private readonly IImageProcessor _processor;
        //private readonly IFileProcessor _explorer;

        //private readonly HashSet<PipelineOperation> _handlingOps = new();

        private DraggedItemAdorner? _draggedItemAdorner;
        private MagnifierAdorner? _magnifierAdorner;
        private MagnifierAdorner? _originalMagnifierAdorner;
        private SelectionAdorner? _selectionAdorner;
        private Rect _selectedRect = Rect.Empty;


        private PipelineOperation? _draggedOperation;
        private PipelineOperation? _activeOperation;
        private ListBoxItem? _activeContainer;
        private int _originalPipelineIndex = -1;
        private int _currentInsertionIndex = -1;
        private bool _isDragging;
        private bool _dropHandled;
        private Point _dragStartPoint;
        
        private InsertionIndicatorAdorner? _insertionAdorner;

        private GridLength _originalImageColumnWidth = new GridLength(4, GridUnitType.Star);

        // Magnifier
        private bool _magnifierEnabled;
        private double _magnifierZoom = 2.0;
        private const double MagnifierMinZoom = 1.0;
        private const double MagnifierMaxZoom = 12.0;
        private const double MagnifierZoomStep = 0.2;

        private double _magnifierSize = 300.0;
        private const double MagnifierMinSize = 80.0;
        private const double MagnifierMaxSize = 1000.0;
        private const double MagnifierSizeStep = 20.0;
        private bool _originalMagnifierEnabled;

        private Point _magnifierNormalizedPos = new Point(0.5, 0.5);

        private DocumentationWindow? _documentationWindow;

        private double _eraseOffset;   // пикселей от левого/правого края
        private bool _eraseModeActive;
        private bool _operationErased;

        private bool _livePipelineRunning;
        private bool _livePipelineRestartPending;
        private readonly object _liveLock = new();
        private readonly HashSet<PipelineOperation> _liveRunning = new();



        // Rect selection

        private double _leftSelected;
        private double _topSelected;
        private double _rightSelected;
        private double _bottomSelected;

        private enum SelectionMode
        {
            None,
            Creating,
            Moving,
            ResizeLeft,
            ResizeRight,
            ResizeTop,
            ResizeBottom,
            ResizeTopLeft,
            ResizeTopRight,
            ResizeBottomLeft,
            ResizeBottomRight
        }

        private SelectionMode _selectionMode = SelectionMode.None;
        private Point _selectionDragStart;   // точка, где начался drag
        private Rect _selectionStartRect;    // прямоугольник на момент начала drag

        // radius hit-zones вокруг углов/граней
        private const double SelectionHandleHit = 8.0;
        private const double SelectionMinSize = 5.0;

        // debounce для Live-пайплайна
        private CancellationTokenSource? _liveDebounceCts;
        private TimeSpan _liveDebounceDelay;

        public IViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                _viewModel = value;
            }
        }

        // for xaml binding
        public Pipeline Pipeline => _manager.CurrentPipeline;

        //public ObservableCollection<PipeLineOperation> PipeLineOperations => _pipeLineOperations;

        private readonly IAppManager _manager;
        //private readonly Pipeline _pipeline;
        private IViewModel _viewModel;


        //private CancellationTokenSource _cts;

        public bool SavePipelineToMd
        {
            get => _manager.IsSavePipelineToMd;
            set => _manager.IsSavePipelineToMd = value;
        }

        //private string _lastOpenedFolder = string.Empty;

        public MainWindow()
        {
            InitializeComponent();

        #if DEBUG
            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var modelPath = System.IO.Path.Combine(baseDir, "Models", "ML", "model.onnx");
                OnnxModelInspector.PrintModelInfo(modelPath);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ONNX inspector failed: {ex}");
            }
        #endif

            //OnnxModelInspector.PrintModelInfo("Models/ML/model.onnx");


            _originalImageColumnWidth = RootGrid.ColumnDefinitions[0].Width;

            //_cts = new CancellationTokenSource();
            var cts = new CancellationTokenSource();
            _manager = new AppManager(this, cts);
            //_pipeline = new Pipeline(_manager);

            _eraseOffset = _manager.EraseOperationOffset;
            _liveDebounceDelay = _manager.ParametersChangedDebounceDelay;

            DataContext = _viewModel;


            //_explorer = new FileProcessor(_cts.Token);
            //_explorer.ErrorOccured += (msg) =>
            //{
            //    Dispatcher.InvokeAsync(() =>
            //    {
            //        System.Windows.MessageBox.Show(this, msg, "Explorer Error", MessageBoxButton.OK, MessageBoxImage.Error);
            //    });
            //};



            SubscribeParameterChangedHandlers();
            HookLiveHandlers();

        }

        private void OrigExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            // Прячем левую колонку с оригиналом
            _viewModel.OriginalImageIsExpanded = false;
            RootGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Auto);
            RootGrid.ColumnDefinitions[2].Width = new GridLength(8, GridUnitType.Star);
        }

        private void OrigExpander_Expanded(object sender, RoutedEventArgs e)
        {
            // Возвращаем исходную ширину (4*)
            _viewModel.OriginalImageIsExpanded = true;
            RootGrid.ColumnDefinitions[0].Width = _originalImageColumnWidth;
            RootGrid.ColumnDefinitions[2].Width = new GridLength(4, GridUnitType.Star);
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
            return pos.X < -_eraseOffset || pos.X > width + _eraseOffset;
        }


        private async Task RunLivePipelineFromOriginalAsync()
        {
            if (_viewModel.OriginalImage == null) return;

            List<PipelineOperation> opsSnapshot;

            lock (_liveLock)
            {

                if (_livePipelineRunning)
                {
                    _livePipelineRestartPending = true;
                    return;
                }
                // не запускаем параллельно, просто пропускаем лишний вызов
                _livePipelineRunning = true;
            }

            try
            {
                opsSnapshot = await Dispatcher.InvokeAsync(() =>
                     Pipeline.Operations
                     .Where(op => op.InPipeline && op.Live)
                     .ToList()
                );

                //_manager.CancelImageProcessing();
                await _manager.ResetWorkingImagePreview();

                foreach (var pipelineOp in opsSnapshot)
                {
                    ProcessorCommand opCommand = pipelineOp.Command;

                    if (opCommand == null) continue;

                    await _manager.ApplyCommandToProcessingImage(opCommand, pipelineOp.CreateParameterDictionary());
                }
            }
            finally
            {
                bool restart;
                lock (_liveLock)
                {
                    _livePipelineRunning = false;
                    restart = _livePipelineRestartPending;
                    _livePipelineRestartPending = false;
                }
                if (restart)
                    _ = RunLiveOperationsForNewImageAsync();
            }
        }

        private void HookLiveHandlers()
        {
            // attach to existing operations
            foreach (var op in Pipeline.Operations)
                op.LiveChanged += OnOperationLiveChanged;

            // attach to future additions/removals if collection changes
            if (Pipeline.Operations is INotifyCollectionChanged coll)
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
            ScheduleLivePipelineRun();
            //if (op.Live)
            //{
            //    ScheduleLivePipelineRun();
            //}
        }

        private void SubscribeParameterChangedHandlers()
        {
            foreach (var op in Pipeline.Operations)
            {
                op.ParameterChanged += OnOperationParameterChanged;
            }

            // If PipeLineOperations can change at runtime, hook new items as well:
            if (Pipeline.Operations is INotifyCollectionChanged coll)
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
                        {
                            removed.ParameterChanged -= OnOperationParameterChanged;
                            
                        }  
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
            //Debug.WriteLine("Stopping");
        }

        private void SplitCountButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button button && button.ContextMenu != null)
            {
                button.ContextMenu.PlacementTarget = button;
                button.ContextMenu.IsOpen = true;
            }
        }

        private void SplitCountMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.MenuItem menuItem)
                return;

            if (!int.TryParse(menuItem.Tag?.ToString(), out int targetCount))
                return;

            if (_viewModel is MainViewModel vm)
            {
                vm.SetPreviewSplitCount(targetCount);
            }
        }

        private void SplitPreviewTile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel is not MainViewModel vm)
                return;

            if (sender is not FrameworkElement element)
                return;

            int index;
            if (element.Tag is int directIndex)
            {
                index = directIndex;
            }
            else if (!int.TryParse(element.Tag?.ToString(), out index))
            {
                return;
            }

            vm.ToggleFocusedSplitPreview(index);
            e.Handled = true;
        }

        private void CloseFocusedPreviewButton_Click(object sender, RoutedEventArgs e)
        {
            if (_viewModel is MainViewModel vm)
            {
                vm.ClearFocusedSplitPreview();
            }

            if (_magnifierEnabled)
            {
                DisableMagnifier();
            }
        }


        private void GetFromSelection_Click(object sender, RoutedEventArgs e)
        {

            var (success, x, y, w, h) = GetWorkingSelectionPixelRect();
            if (!success)
            {
                System.Windows.MessageBox.Show("No selection on working image.");
                return;
            }

            foreach (var op in Pipeline.Operations)
            {
                    if (op.Type == PipelineOperationType.BordersRemove)
                    {   
                        foreach (var p in op.Parameters)
                        {
                            if (p.IsCombo && p.SelectedOption.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                            {
                                Rect viewboxRect = _selectedRect;

                                // 1) Viewbox → Image (DIPs в системе координат PreviewImgBox)
                                GeneralTransform transform = PreviewViewbox.TransformToVisual(PreviewImgBox);
                                Rect imageRectDip = transform.TransformBounds(viewboxRect);

                                if (PreviewImgBox.Source is not BitmapSource bmp)
                                    return;

                                int imgW = bmp.PixelWidth;
                                int imgH = bmp.PixelHeight;

                                // если параметры — "толщина от краёв"
                                int manualLeft = x;
                                int manualTop = y;
                                int manualRight = imgW - (x + w);
                                int manualBottom = imgH - (y + h);


                                // Set parameters
                                foreach (var param in op.Parameters)
                                {
                                    switch (param.Key)
                                    {
                                        case "manualLeft":
                                            param.Value = manualLeft;
                                            break;
                                        case "manualTop":
                                            param.Value = manualTop;
                                            break;
                                        case "manualRight":
                                            param.Value = manualRight;
                                            break;
                                        case "manualBottom":
                                            param.Value = manualBottom;
                                            break;
                                    }
                                }
                            }
                        }
                    
                    }
            }
            ResetSelection();
        }

        private (bool success, int x, int y, int width, int height) GetWorkingSelectionPixelRect()
        {
            if (_selectedRect.IsEmpty || _selectedRect.Width <= 0 || _selectedRect.Height <= 0)
                return (false, 0, 0, 0, 0);

            if (PreviewViewbox == null || PreviewImgBox == null)
                return (false, 0, 0, 0, 0);

            if (PreviewImgBox.Source is not BitmapSource bmp)
                return (false, 0, 0, 0, 0);

            // 1) Viewbox -> Image (DIPs в системе координат PreviewImgBox)
            Rect viewboxRect = _selectedRect;

            GeneralTransform transform = PreviewViewbox.TransformToVisual(PreviewImgBox);
            Rect imageRectDip = transform.TransformBounds(viewboxRect);

            if (PreviewImgBox.ActualWidth <= 0 || PreviewImgBox.ActualHeight <= 0)
                return (false, 0, 0, 0, 0);

            // 2) DIPs (Image) -> пиксели BitmapSource
            double scaleX = bmp.PixelWidth / PreviewImgBox.ActualWidth;
            double scaleY = bmp.PixelHeight / PreviewImgBox.ActualHeight;

            double pxLeft = imageRectDip.X * scaleX;
            double pxTop = imageRectDip.Y * scaleY;
            double pxRight = (imageRectDip.X + imageRectDip.Width) * scaleX;
            double pxBottom = (imageRectDip.Y + imageRectDip.Height) * scaleY;

            double leftD = Math.Min(pxLeft, pxRight);
            double topD = Math.Min(pxTop, pxBottom);
            double rightD = Math.Max(pxLeft, pxRight);
            double bottomD = Math.Max(pxTop, pxBottom);

            leftD = Math.Max(0, leftD);
            topD = Math.Max(0, topD);
            rightD = Math.Min(bmp.PixelWidth, rightD);
            bottomD = Math.Min(bmp.PixelHeight, bottomD);

            double wD = rightD - leftD;
            double hD = bottomD - topD;

            if (wD <= 0 || hD <= 0)
                return (false, 0, 0, 0, 0);

            int x = (int)Math.Round(leftD);
            int y = (int)Math.Round(topD);
            int width = (int)Math.Round(wD);
            int height = (int)Math.Round(hD);

#if DEBUG
            Debug.WriteLine($"Selection DIP in Image: {imageRectDip}");
            Debug.WriteLine($"Selection pixels: x={x}, y={y}, w={width}, h={height}, imgW={bmp.PixelWidth}, imgH={bmp.PixelHeight}");
#endif

            return (true, x, y, width, height);
        }


        private async void PipelineRunButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is PipelineOperation operation)
            {
                //var commmand = operation.Command;
                //var p = operation.CreateParameterDictionary();
                await _manager.ResetWorkingImagePreview();
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
                rawPos.X < _eraseOffset ||
                rawPos.X > width - _eraseOffset;

            _eraseModeActive = erase;
            _draggedItemAdorner?.SetEraseMode(erase);

            // КЛЭМПИМ X для визуального призрака:
            // не даём ему уйти дальше EraseOffset от краёв
            double clampedX = Math.Max(_eraseOffset, Math.Min(rawPos.X, width - _eraseOffset));
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

            //PipelineListBox.UpdateLayout();
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

            int insertionIndex = Math.Max(0, Math.Min(Pipeline.Count, _currentInsertionIndex));
            Pipeline.Insert(insertionIndex, _draggedOperation);
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

                //var rawPos = Mouse.GetPosition(PipelineListBox);
                //double width = PipelineListBox.ActualWidth;

                //double clampedX = Math.Max(EraseOffset, Math.Min(rawPos.X, width - EraseOffset));
                //var clampedPos = new Point(clampedX, rawPos.Y);

                //_draggedItemAdorner.Update(clampedPos);

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
            _originalPipelineIndex = Pipeline.IndexOf(_activeOperation);
            _currentInsertionIndex = _originalPipelineIndex;

            PipelineListBox.UpdateLayout();


            var bitmap = CaptureElementBitmap(_activeContainer);
            var layer = AdornerLayer.GetAdornerLayer(PipelineListBox);
            if (bitmap != null && layer != null)
            {
                _draggedItemAdorner = new DraggedItemAdorner(PipelineListBox, layer, bitmap);
                _draggedItemAdorner.Update(Mouse.GetPosition(PipelineListBox));
            }

            Pipeline.Remove(_activeOperation);

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
                    index = Math.Max(0, Math.Min(Pipeline.Count, index));
                    Pipeline.Insert(index, _draggedOperation);
                }
            }
            if (_draggedOperation != null && (_operationErased || eraseOnCancel))
            {
                _manager.CancelImageProcessing();
                ScheduleLivePipelineRun();
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


        //private async void ExecuteManagerCommand(ProcessorCommand command, Dictionary<string, object> parameters)
        //{
        //    if (_manager == null)
        //        return;

        //    // Опционально: если хочешь, чтобы новый запуск отменял предыдущий Despeckle:
        //    _manager.CancelImageProcessing();

        //    try
        //    {
        //        await _manager.ApplyCommandToProcessingImage(command, parameters);
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        // тихо игнорируем
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"ExecuteManagerCommand error: {ex}");
        //    }
        //}

        //private Dictionary<string, object> GetParametersFromSender(object sender)
        //{
        //    if (sender is FrameworkElement element && element.DataContext is PipelineOperation operation)
        //    {
        //        return operation.CreateParameterDictionary();
        //    }

        //    return new Dictionary<string, object>();
        //}

        //public void UpdatePreview(Stream stream)
        //{
        //    var bitmap = streamToBitmapSource(stream);
        //    Dispatcher.InvokeAsync(() => PreviewImgBox.Source = bitmap);
        //}



        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            _manager.Shutdown();
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
                    _liveDebounceCts?.Cancel();
                    _liveDebounceCts = null;
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

        private void DocumentationMenu_Click(object sender, RoutedEventArgs e)
        {
            OpenDocumentationWindow(sectionId: null);
        }

        private void OperationHelpButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is PipelineOperation operation)
            {
                OpenDocumentationWindow(operation.DocumentationSectionId);
            }
        }

        private void OpenDocumentationWindow(string? sectionId)
        {
            if (_documentationWindow == null || !_documentationWindow.IsLoaded)
            {
                _documentationWindow = new DocumentationWindow
                {
                    Owner = this
                };
                _documentationWindow.Closed += (_, __) => _documentationWindow = null;
                _documentationWindow.Show();
            }
            else
            {
                if (_documentationWindow.WindowState == WindowState.Minimized)
                    _documentationWindow.WindowState = WindowState.Normal;
                _documentationWindow.Activate();
            }

            _documentationWindow?.ShowSection(sectionId);
        }

        private async void LoadPipelinePreset_Click(object sender, RoutedEventArgs e)
        {
            var res = System.Windows.MessageBox.Show($"WARNING! All unsaved parameters will be lost! Are you sure?",
                                                         "Confirm",
                                                         MessageBoxButton.OKCancel,
                                                         MessageBoxImage.Warning);
            if (res == MessageBoxResult.Cancel) return;
            var dlg = new Microsoft.Win32.OpenFileDialog();
            dlg.Filter = "IGpreset files|*.igpreset";
            if (dlg.ShowDialog() == true)
            {
                var fileNamePath = dlg.FileName;
                await _manager.LoadPipelineFromFile(fileNamePath);
            }
                
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
            var op = Pipeline.CreatePipelineOperation(type);  // см. шаг 4

            // вставляем в начало pipeline (индекс 0)
            Pipeline.Insert(0, op);

            // опционально: сразу пересчитать live-pipeline
            ScheduleLivePipelineRun();
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

                var json = Pipeline.BuildPipelineForSave();
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

            Pipeline.ResetToDefault();
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

                _manager.CancelImageProcessing();
                _liveDebounceCts?.Cancel();
                _liveDebounceCts = null;
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

        

        //private (ProcessorCommand Value, Dictionary<string, object>)[]? GetPipelineParameters()
        //{
        //    var pl = Pipeline.Operations
        //            .Where(op => op.InPipeline)
        //            .Select(op => (op.Command, op.CreateParameterDictionary()))
        //            .ToArray();

        //    return pl;
        //}

        private async void ApplyCurrentPipelineToSelectedRootFolder_Click(object sender, RoutedEventArgs e)
        {
            if (Pipeline == null) return;
            if (Pipeline.Operations.Count == 0)
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
                await _manager.ProcessRootFolder(rootFolder, Pipeline, true);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to process Root folder {rootFolder}: {ex.Message}",
                                               "Error",
                                               MessageBoxButton.OK,
                                               MessageBoxImage.Error);
            }

        }

        private async void ApplyCurrentPipelineToCurrentFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // попытка взять папку  из пути текущего файла
                //string? folder = _viewModel?.LastOpenedFolder;


                //var pipeline = GetPipelineParameters();

                if (Pipeline.Operations.Count == 0)
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
                var res = System.Windows.MessageBox.Show($"Apply current pipeline to all images in:\n\n{folder} ?",
                                                         "Confirm",
                                                         MessageBoxButton.OKCancel,
                                                         MessageBoxImage.Question);
                if (res != MessageBoxResult.OK) return;






                // вызываем менеджер, передавая команды
                await _manager.ProcessFolder(folder, Pipeline);


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
            _manager.ResetWorkingImagePreview();
        }

        //private async Task ResetPreview()
        //{
        //    _manager.CancelImageProcessing();
        //    if (_viewModel.OriginalImage == null) return;
        //    await _manager.SetImageForProcessing(_viewModel.OriginalImage);
        //}

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
                //_manager.SaveProcessedImage(path,
                //    ext switch
                //    {
                //        ".tif" or ".tiff" => ImageFormat.Tiff,
                //        ".png" => ImageFormat.Png,
                //        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                //        ".bmp" => ImageFormat.Bmp,
                //        _ => ImageFormat.Png
                //    },
                //    compression);
                _manager.SaveProcessedImageToTiff(path,
                    ext switch
                    {
                        ".tif" or ".tiff" => ImageFormat.Tiff,
                        ".png" => ImageFormat.Png,
                        ".jpg" or ".jpeg" => ImageFormat.Jpeg,
                        ".bmp" => ImageFormat.Bmp,
                        _ => ImageFormat.Png
                    });
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
                    double radius = 4;
                    drawingContext.DrawRoundedRectangle(redBrush, null, rect, radius, radius);
                    
                }
                else
                {
                    drawingContext.PushOpacity(0.7);
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

        private void EnableMagnifier()
        {
            if (_magnifierEnabled || PreviewViewbox == null)
                return;

            var layer = AdornerLayer.GetAdornerLayer(PreviewViewbox);
            if (layer == null)
                return;

            if (_magnifierZoom < MagnifierMinZoom)
                _magnifierZoom = MagnifierMinZoom;

            // сначала клампим глобальный размер
            //ClampMagnifierSize();

            Debug.WriteLine($"Working magn size: {_magnifierSize}");
            _magnifierAdorner = new MagnifierAdorner(
                PreviewViewbox,
                layer,
                _magnifierZoom,
                _magnifierSize
            );
            _magnifierEnabled = true;

            // и синхронизируем размер со второй лупой, если она уже включена
            ApplyMagnifierSizeToAdorners();

            // стартуем из центра превью
            var center = new Point(PreviewViewbox.ActualWidth / 2.0,
                                   PreviewViewbox.ActualHeight / 2.0);
            _magnifierAdorner.UpdatePosition(center);
        }

        private void DisableMagnifier()
        {
            _magnifierEnabled = false;
            _magnifierAdorner?.Remove();
            _magnifierAdorner = null;
        }

        private void EnableOriginalMagnifier()
        {
            if (_originalMagnifierEnabled || OrigViewbox == null)
                return;

            var layer = AdornerLayer.GetAdornerLayer(OrigViewbox);
            if (layer == null)
                return;

            // клампим глобальный размер по обоим изображениям
            //ClampMagnifierSize();

            Debug.WriteLine($"original magn size: {_magnifierSize}");
            _originalMagnifierAdorner = new MagnifierAdorner(
                OrigViewbox,
                layer,
                _magnifierZoom,
                _magnifierSize
            );
            _originalMagnifierEnabled = true;

            // позиционируем по нормализованным координатам
            var sizeOrig = OrigViewbox.RenderSize;
            Point center;
            if (sizeOrig.Width > 0 && sizeOrig.Height > 0)
            {
                center = new Point(
                    _magnifierNormalizedPos.X * sizeOrig.Width,
                    _magnifierNormalizedPos.Y * sizeOrig.Height
                );
            }
            else
            {
                center = new Point(OrigViewbox.ActualWidth / 2.0,
                                   OrigViewbox.ActualHeight / 2.0);
            }

            center = new Point(OrigViewbox.ActualWidth / 2.0,
                                   OrigViewbox.ActualHeight / 2.0);

            _originalMagnifierAdorner.UpdatePosition(center);

            // СИНХРОНИЗАЦИЯ: вдруг уже есть лупа на превью → подгоняем её тоже
            ApplyMagnifierSizeToAdorners();
        }


        private void DisableOriginalMagnifier()
        {
            _originalMagnifierEnabled = false;
            _originalMagnifierAdorner?.Remove();
            _originalMagnifierAdorner = null;
        }

        private double GetMaxLensSize()
        {
            double max = MagnifierMaxSize;

            // ограничиваем по превью
            if (PreviewViewbox != null)
            {
                var s = PreviewViewbox.RenderSize;
                if (s.Width > 0 && s.Height > 0)
                {
                    var localMax = Math.Min(s.Width, s.Height);
                    max = Math.Min(max, localMax);
                }
            }

            // ограничиваем по оригиналу
            if (OrigViewbox != null)
            {
                var s = OrigViewbox.RenderSize;
                if (s.Width > 0 && s.Height > 0)
                {
                    var localMax = Math.Min(s.Width, s.Height);
                    max = Math.Min(max, localMax);
                }
            }

            // БЕЗ Max(MagnifierMinSize, max) — только максимум
            return max;
        }


        private void ClampMagnifierSize()
        {
            double maxAllowed = GetMaxLensSize();
            _magnifierSize = Math.Max(MagnifierMinSize, Math.Min(_magnifierSize, maxAllowed));
        }

        private void ApplyMagnifierSizeToAdorners()
        {
            _magnifierAdorner?.UpdateSize(_magnifierSize);
            _originalMagnifierAdorner?.UpdateSize(_magnifierSize);
        }





        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            // ESC — turn off both magnifiers
            if (e.Key == Key.Escape)
            {
                if (_magnifierEnabled || _originalMagnifierEnabled)
                {
                    DisableMagnifier();           // твой метод, который выключает Preview
                    DisableOriginalMagnifier();   // новый метод, см. ниже
                    ResetSelection();
                    e.Handled = true;
                    return;
                }
            }

            // M — on/off magnifier on PreviewImgBox (working image)
            if (e.Key == Key.M)
            {
                if (_magnifierEnabled)
                    DisableMagnifier();
                else
                    EnableMagnifier();

                e.Handled = true;
            }

            // S — on/off synced magnifier on OrigImgBox (original Image)
            if (e.Key == Key.S)
            {
                if (_originalMagnifierEnabled)
                    DisableOriginalMagnifier();
                else
                    EnableOriginalMagnifier();

                e.Handled = true;
            }
        }

        //private void PreviewImgBox_MouseMove(object sender, MouseEventArgs e)
        //{
        //    if (!_magnifierEnabled || _magnifierAdorner == null)
        //        return;

        //    var pos = e.GetPosition(PreviewViewbox);
        //    _magnifierAdorner.UpdatePosition(pos);

        //    // --- считаем нормализованные координаты центра лупы ---
        //    var size = PreviewViewbox.RenderSize;
        //    if (size.Width > 0 && size.Height > 0)
        //    {
        //        _magnifierNormalizedPos = new Point(
        //            pos.X / size.Width,
        //            pos.Y / size.Height
        //        );

        //        // если включена лупа на оригинале — двигаем её в то же относительное место
        //        if (_originalMagnifierEnabled && _originalMagnifierAdorner != null && OrigViewbox != null)
        //        {
        //            var sizeOrig = OrigViewbox.RenderSize;
        //            if (sizeOrig.Width > 0 && sizeOrig.Height > 0)
        //            {
        //                var origPos = new Point(
        //                    _magnifierNormalizedPos.X * sizeOrig.Width,
        //                    _magnifierNormalizedPos.Y * sizeOrig.Height
        //                );

        //                _originalMagnifierAdorner.UpdatePosition(origPos);
        //            }
        //        }
        //    }
        //}

        private void PreviewImgBox_MouseMove(object sender, MouseEventArgs e)
        {
            if (PreviewViewbox == null)
                return;

            var pos = e.GetPosition(PreviewViewbox);

            // --- 1) Обновление selection (если идёт drag) ---
            if (_selectionMode != SelectionMode.None &&
                e.LeftButton == MouseButtonState.Pressed)
            {
                var size = PreviewViewbox.RenderSize;
                if (size.Width > 0 && size.Height > 0)
                {
                    Rect newRect = _selectedRect;

                    switch (_selectionMode)
                    {
                        case SelectionMode.Creating:
                            {
                                double x1 = Math.Min(_selectionDragStart.X, pos.X);
                                double y1 = Math.Min(_selectionDragStart.Y, pos.Y);
                                double x2 = Math.Max(_selectionDragStart.X, pos.X);
                                double y2 = Math.Max(_selectionDragStart.Y, pos.Y);
                                newRect = new Rect(new Point(x1, y1), new Point(x2, y2));
                                break;
                            }

                        case SelectionMode.Moving:
                            {
                                double dx = pos.X - _selectionDragStart.X;
                                double dy = pos.Y - _selectionDragStart.Y;
                                newRect = new Rect(
                                    _selectionStartRect.X + dx,
                                    _selectionStartRect.Y + dy,
                                    _selectionStartRect.Width,
                                    _selectionStartRect.Height);
                                break;
                            }

                        case SelectionMode.ResizeLeft:
                            {
                                double newLeft = pos.X;
                                newRect = new Rect(
                                    new Point(newLeft, _selectionStartRect.Top),
                                    new Point(_selectionStartRect.Right, _selectionStartRect.Bottom));
                                break;
                            }
                        case SelectionMode.ResizeRight:
                            {
                                double newRight = pos.X;
                                newRect = new Rect(
                                    new Point(_selectionStartRect.Left, _selectionStartRect.Top),
                                    new Point(newRight, _selectionStartRect.Bottom));
                                break;
                            }
                        case SelectionMode.ResizeTop:
                            {
                                double newTop = pos.Y;
                                newRect = new Rect(
                                    new Point(_selectionStartRect.Left, newTop),
                                    new Point(_selectionStartRect.Right, _selectionStartRect.Bottom));
                                break;
                            }
                        case SelectionMode.ResizeBottom:
                            {
                                double newBottom = pos.Y;
                                newRect = new Rect(
                                    new Point(_selectionStartRect.Left, _selectionStartRect.Top),
                                    new Point(_selectionStartRect.Right, newBottom));
                                break;
                            }

                        case SelectionMode.ResizeTopLeft:
                            {
                                double newLeft = pos.X;
                                double newTop = pos.Y;
                                newRect = new Rect(
                                    new Point(newLeft, newTop),
                                    new Point(_selectionStartRect.Right, _selectionStartRect.Bottom));
                                break;
                            }
                        case SelectionMode.ResizeTopRight:
                            {
                                double newRight = pos.X;
                                double newTop = pos.Y;
                                newRect = new Rect(
                                    new Point(_selectionStartRect.Left, newTop),
                                    new Point(newRight, _selectionStartRect.Bottom));
                                break;
                            }
                        case SelectionMode.ResizeBottomLeft:
                            {
                                double newLeft = pos.X;
                                double newBottom = pos.Y;
                                newRect = new Rect(
                                    new Point(newLeft, _selectionStartRect.Top),
                                    new Point(_selectionStartRect.Right, newBottom));
                                break;
                            }
                        case SelectionMode.ResizeBottomRight:
                            {
                                double newRight = pos.X;
                                double newBottom = pos.Y;
                                newRect = new Rect(
                                    new Point(_selectionStartRect.Left, _selectionStartRect.Top),
                                    new Point(newRight, newBottom));
                                break;
                            }
                    }

                    UpdateSelectedRect(newRect);
                }
            }

            // --- 2) Лупа (не трогаем твою логику синхронизации) ---
            if (_magnifierEnabled && _magnifierAdorner != null)
            {
                _magnifierAdorner.UpdatePosition(pos);

                var size = PreviewViewbox.RenderSize;
                if (size.Width > 0 && size.Height > 0)
                {
                    _magnifierNormalizedPos = new Point(
                        pos.X / size.Width,
                        pos.Y / size.Height
                    );

                    if (_originalMagnifierEnabled && _originalMagnifierAdorner != null && OrigViewbox != null)
                    {
                        var sizeOrig = OrigViewbox.RenderSize;
                        if (sizeOrig.Width > 0 && sizeOrig.Height > 0)
                        {
                            var origPos = new Point(
                                _magnifierNormalizedPos.X * sizeOrig.Width,
                                _magnifierNormalizedPos.Y * sizeOrig.Height
                            );

                            _originalMagnifierAdorner.UpdatePosition(origPos);
                        }
                    }
                }
            }
        }


        private void PreviewImgBox_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (!_magnifierEnabled || _magnifierAdorner == null || PreviewViewbox == null)
                return;

            //var pos = e.GetPosition(PreviewImgBox);

            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                // меняем размер
                double deltaSize = e.Delta > 0 ? MagnifierSizeStep : -MagnifierSizeStep;

                // сначала двигаем
                _magnifierSize += deltaSize;
                ClampMagnifierSize();
                ApplyMagnifierSizeToAdorners();

                var pos = e.GetPosition(PreviewViewbox);
                _magnifierAdorner?.UpdatePosition(pos);

                e.Handled = true;
                return;
            }
            else
            {
                // меняем zoom
                double deltaZoom = e.Delta > 0 ? MagnifierZoomStep : -MagnifierZoomStep;
                _magnifierZoom = Math.Max(MagnifierMinZoom,
                                  Math.Min(MagnifierMaxZoom, _magnifierZoom + deltaZoom));

                _magnifierAdorner?.UpdateZoom(_magnifierZoom);
                _originalMagnifierAdorner?.UpdateZoom(_magnifierZoom);

                var pos = e.GetPosition(PreviewViewbox);
                _magnifierAdorner?.UpdatePosition(pos);
            }

            e.Handled = true;
        }

        private sealed class MagnifierAdorner : Adorner
        {
            private readonly AdornerLayer _layer;
            private Point _position;
            private double _zoom;
            private double _lensSize;

            public MagnifierAdorner(UIElement adornedElement,
                                    AdornerLayer layer,
                                    double initialZoom,
                                    double lensSize)
                : base(adornedElement)
            {
                _layer = layer;
                _zoom = initialZoom;
                _lensSize = lensSize;

                IsHitTestVisible = false;
                _layer.Add(this);
            }

            public void UpdatePosition(Point position)
            {
                _position = position;
                InvalidateVisual();
            }

            public void UpdateSize(double lensSize)
            {
                _lensSize = lensSize;
                InvalidateVisual();
            }


            public void UpdateZoom(double zoom)
            {
                _zoom = zoom;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);

                if (AdornedElement is not FrameworkElement element || !element.IsVisible)
                    return;

                var size = element.RenderSize;
                if (size.Width <= 0 || size.Height <= 0)
                    return;

                var lensSize = _lensSize;
                if (lensSize <= 0)
                    return;

                double half = lensSize / 2.0;

                // центр так, чтобы ВЕСЬ квадрат был внутри элемента
                double cx = _position.X;
                double cy = _position.Y;

                cx = Math.Max(half, Math.Min(cx, size.Width - half));
                cy = Math.Max(half, Math.Min(cy, size.Height - half));

                var center = new Point(cx, cy);
                var lensRect = new Rect(center.X - half, center.Y - half, lensSize, lensSize);

                // размер окна в источнике под zoom
                double viewW = lensSize / _zoom;
                double viewH = lensSize / _zoom;

                if (viewW > size.Width)
                    viewW = size.Width;
                if (viewH > size.Height)
                    viewH = size.Height;

                // PARALLAX:
                //  положение лупы (0..1) → положение viewbox (0..maxOffset)
                double travelX = Math.Max(1.0, size.Width - lensSize);
                double travelY = Math.Max(1.0, size.Height - lensSize);

                double relX = (center.X - half) / travelX; // 0..1
                double relY = (center.Y - half) / travelY; // 0..1

                relX = Math.Max(0.0, Math.Min(1.0, relX));
                relY = Math.Max(0.0, Math.Min(1.0, relY));

                double maxOffsetX = Math.Max(0.0, size.Width - viewW);
                double maxOffsetY = Math.Max(0.0, size.Height - viewH);

                double vx = maxOffsetX * relX;
                double vy = maxOffsetY * relY;

                var viewbox = new Rect(vx, vy, viewW, viewH);

                var brush = new VisualBrush(AdornedElement)
                {
                    Viewbox = viewbox,
                    ViewboxUnits = BrushMappingMode.Absolute,
                    Stretch = Stretch.Fill
                };

                // содержимое лупы
                drawingContext.PushClip(new RectangleGeometry(lensRect));
                drawingContext.DrawRectangle(brush, null, lensRect);
                drawingContext.Pop();

                // рамка
                var borderBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
                borderBrush.Freeze();
                var borderPen = new Pen(borderBrush, 1.5);
                borderPen.Freeze();

                drawingContext.DrawRectangle(null, borderPen, lensRect);
            }



            public void Remove()
            {
                _layer.Remove(this);
            }
        }

        private sealed class SelectionAdorner : Adorner
        {
            private readonly AdornerLayer _layer;
            private Rect _rect;

            private readonly Pen _borderPen;
            private readonly Brush _fillBrush;
            private readonly Brush _handleBrush;

            private const double HandleSize = 6.0;

            public SelectionAdorner(UIElement adornedElement, AdornerLayer layer)
                : base(adornedElement)
            {
                _layer = layer;
                IsHitTestVisible = false;

                var borderColor = Color.FromArgb(220, 0, 120, 215); // синий
                var borderBrush = new SolidColorBrush(borderColor);
                borderBrush.Freeze();
                _borderPen = new Pen(borderBrush, 1.0);
                _borderPen.Freeze();

                _fillBrush = new SolidColorBrush(Color.FromArgb(40, 0, 120, 215)); // полупрозрачный
                _fillBrush.Freeze();

                _handleBrush = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255));
                _handleBrush.Freeze();

                _layer.Add(this);
            }

            public void UpdateRect(Rect rect)
            {
                _rect = rect;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext drawingContext)
            {
                base.OnRender(drawingContext);

                if (_rect.IsEmpty || _rect.Width <= 0 || _rect.Height <= 0)
                    return;

                // заполнение + рамка
                drawingContext.DrawRectangle(_fillBrush, _borderPen, _rect);

                double hs = HandleSize / 2.0;

                DrawHandle(drawingContext, _rect.TopLeft, hs);
                DrawHandle(drawingContext, _rect.TopRight, hs);
                DrawHandle(drawingContext, _rect.BottomLeft, hs);
                DrawHandle(drawingContext, _rect.BottomRight, hs);
            }

            private void DrawHandle(DrawingContext ctx, Point center, double hs)
            {
                var rect = new Rect(
                    new Point(center.X - hs, center.Y - hs),
                    new Size(HandleSize, HandleSize));

                ctx.DrawRectangle(_handleBrush, null, rect);
            }

            public void Remove()
            {
                _layer.Remove(this);
            }
        }

        private void PreviewViewbox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (PreviewViewbox == null || !_viewModel.IsSelectionAvaliable)
                return;

            var pos = e.GetPosition(PreviewViewbox);

            EnsureSelectionAdorner();

            // если уже есть selection — проверим, попали ли в него
            if (HasSelection)
            {
                var mode = HitTestSelection(pos);
                if (mode != SelectionMode.None)
                {
                    _selectionMode = mode;
                    _selectionDragStart = pos;
                    _selectionStartRect = _selectedRect;
                    PreviewViewbox.CaptureMouse();
                    e.Handled = true;
                    return;
                }
            }

            // иначе начинаем новый selection
            _selectionMode = SelectionMode.Creating;
            _selectionDragStart = pos;
            _selectionStartRect = Rect.Empty;

            // начальный прямоугольник нулевого размера
            UpdateSelectedRect(new Rect(pos, new Size(0, 0)));
            PreviewViewbox.CaptureMouse();
            e.Handled = true;
        }

        private void PreviewViewbox_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (PreviewViewbox == null)
                return;

            if (_selectionMode != SelectionMode.None)
            {
                PreviewViewbox.ReleaseMouseCapture();
                _selectionMode = SelectionMode.None;
                e.Handled = true;
            }


        }

        private void ResetSelection()
        {
            // сбрасываем геометрию
            _selectedRect = Rect.Empty;

            _leftSelected = 0;
            _topSelected = 0;
            _rightSelected = 0;
            _bottomSelected = 0;

            _selectionMode = SelectionMode.None;

            // отпускаем мышь, если держим
            PreviewViewbox?.ReleaseMouseCapture();

            // убираем адорнер, если хочешь полностью его снять
            if (_selectionAdorner != null)
            {
                _selectionAdorner.UpdateRect(Rect.Empty); // чтобы он ничего не рисовал
                                                          // или, если у тебя есть метод Remove():
                                                          // _selectionAdorner.Remove();
                                                          // _selectionAdorner = null;
            }

#if DEBUG
            Debug.WriteLine("Selection reset");
#endif
        }


        private void EnsureSelectionAdorner()
        {
            if (_selectionAdorner != null || PreviewViewbox == null)
                return;

            var layer = AdornerLayer.GetAdornerLayer(PreviewViewbox);
            if (layer != null)
            {
                _selectionAdorner = new SelectionAdorner(PreviewViewbox, layer);
            }
        }

        private void UpdateSelectedRect(Rect rect)
        {
            if (PreviewViewbox == null)
                return;

            var size = PreviewViewbox.RenderSize;
            if (size.Width <= 0 || size.Height <= 0)
                return;

            // клампим внутрь PreviewViewbox
            double left = Math.Max(0, Math.Min(rect.Left, size.Width));
            double top = Math.Max(0, Math.Min(rect.Top, size.Height));
            double right = Math.Max(0, Math.Min(rect.Right, size.Width));
            double bottom = Math.Max(0, Math.Min(rect.Bottom, size.Height));

            if (right < left) (left, right) = (right, left);
            if (bottom < top) (top, bottom) = (bottom, top);

            Rect clamped = new Rect(new Point(left, top), new Point(right, bottom));

            if (clamped.Width < SelectionMinSize || clamped.Height < SelectionMinSize)
            {
                // даём сделать совсем маленький, но не отрицательный
                clamped = new Rect(clamped.X, clamped.Y,
                                   Math.Max(clamped.Width, SelectionMinSize),
                                   Math.Max(clamped.Height, SelectionMinSize));
            }

            _selectedRect = clamped;

            _leftSelected = _selectedRect.Left;
            _topSelected = _selectedRect.Top;
            _rightSelected = _selectedRect.Right;
            _bottomSelected = _selectedRect.Bottom;

            _selectionAdorner?.UpdateRect(_selectedRect);

#if DEBUG
            Debug.WriteLine($"Selection: L={_leftSelected:0.##}, T={_topSelected:0.##}, R={_rightSelected:0.##}, B={_bottomSelected:0.##}");
#endif
        }

        // простой хелпер, чтобы узнать — есть ли уже выбор
        private bool HasSelection =>
            !_selectedRect.IsEmpty &&
            _selectedRect.Width > 0 &&
            _selectedRect.Height > 0;

        private SelectionMode HitTestSelection(Point pos)
        {
            if (!HasSelection)
                return SelectionMode.None;

            var r = _selectedRect;

            // углы
            if (Distance(pos, r.TopLeft) <= SelectionHandleHit)
                return SelectionMode.ResizeTopLeft;
            if (Distance(pos, r.TopRight) <= SelectionHandleHit)
                return SelectionMode.ResizeTopRight;
            if (Distance(pos, r.BottomLeft) <= SelectionHandleHit)
                return SelectionMode.ResizeBottomLeft;
            if (Distance(pos, r.BottomRight) <= SelectionHandleHit)
                return SelectionMode.ResizeBottomRight;

            // грани
            if (Math.Abs(pos.X - r.Left) <= SelectionHandleHit &&
                pos.Y >= r.Top && pos.Y <= r.Bottom)
                return SelectionMode.ResizeLeft;

            if (Math.Abs(pos.X - r.Right) <= SelectionHandleHit &&
                pos.Y >= r.Top && pos.Y <= r.Bottom)
                return SelectionMode.ResizeRight;

            if (Math.Abs(pos.Y - r.Top) <= SelectionHandleHit &&
                pos.X >= r.Left && pos.X <= r.Right)
                return SelectionMode.ResizeTop;

            if (Math.Abs(pos.Y - r.Bottom) <= SelectionHandleHit &&
                pos.X >= r.Left && pos.X <= r.Right)
                return SelectionMode.ResizeBottom;

            // внутри — move
            if (r.Contains(pos))
                return SelectionMode.Moving;

            return SelectionMode.None;
        }

        private static double Distance(Point a, Point b)
        {
            double dx = a.X - b.X;
            double dy = a.Y - b.Y;
            return Math.Sqrt(dx * dx + dy * dy);
        }


    }


}
