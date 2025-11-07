using ImgViewer.Interfaces;
using ImgViewer.Models;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
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
        private readonly HashSet<PipeLineOperation> _handlingOps = new();

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

        private bool _livePipelineRunning = false;


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

        private readonly HashSet<PipeLineOperation> _liveRunning = new();

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
            
            SubscribeParameterChangedHandlers();
            HookLiveHandlers();

        }

        private void HookLiveHandlers()
        {
            // attach to existing operations
            foreach (var op in PipeLineOperations)
                op.LiveChanged += OnOperationLiveChanged;

            // attach to future additions/removals if collection changes
            if (PipeLineOperations is INotifyCollectionChanged coll)
            {
                coll.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (PipeLineOperation added in e.NewItems)
                            added.LiveChanged += OnOperationLiveChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (PipeLineOperation removed in e.OldItems)
                            removed.LiveChanged -= OnOperationLiveChanged;
                    }
                };
            }
        }

        private async void OnOperationLiveChanged(PipeLineOperation op)
        {
            // only react when Live turned ON
            if (!op.Live) return;

            // avoid duplicate parallel runs for same op
            if (!_liveRunning.Add(op)) return;

            try
            {
                // 1) reset preview to original on UI thread
                // Prefer a ResetImage() helper if you have it, otherwise call ResetButton_Click safely.
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        // If you have ResetImage() helper, call it instead:
                        
                        ResetPreview();

                        // Otherwise call reset button handler (you earlier had ResetButton_Click)
                        //ResetButton_Click(/*sender*/ ResetButton /*if you have the button reference*/, new RoutedEventArgs());
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Reset failed before live run: {ex}");
                    }
                });

                // 2) execute operation on background thread
                await Task.Run(() =>
                {
                    try
                    {
                        // if Execute modifies UI, you must update UI via Dispatcher inside Execute
                        op.Execute(this);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Live execution failed for {op.DisplayName}: {ex}");
                    }
                });
            }
            finally
            {
                _liveRunning.Remove(op);
            }
        }

        private void SubscribeParameterChangedHandlers()
        {
            foreach (var op in PipeLineOperations)
            {
                op.ParameterChanged += OnOperationParameterChanged;
            }

            // If PipeLineOperations can change at runtime, hook new items as well:
            if (PipeLineOperations is INotifyCollectionChanged coll)
            {
                coll.CollectionChanged += (s, e) =>
                {
                    if (e.NewItems != null)
                    {
                        foreach (PipeLineOperation added in e.NewItems)
                            added.ParameterChanged += OnOperationParameterChanged;
                    }
                    if (e.OldItems != null)
                    {
                        foreach (PipeLineOperation removed in e.OldItems)
                            removed.ParameterChanged -= OnOperationParameterChanged;
                    }
                };
            }
        }

        // --- Replace this existing method with the code below ---
        private async void OnOperationParameterChanged(PipeLineOperation op, PipeLineParameter? param)
        {
            // avoid re-entrancy: if a pipeline-run is already applying, ignore subsequent rapid changes
            if (_livePipelineRunning) return;

            _livePipelineRunning = true;
            try
            {
                // 1) Reset preview to original on UI thread (same behavior as Reset button)
                try
                {
                    Dispatcher.Invoke(() => ResetPreview());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Reset failed in parameter-change handler: {ex}");
                }

                // 2) Execute ALL operations from the start that are marked InPipeline && Live
                //    Execute sequentially in pipeline order to accumulate effects predictably.
                foreach (var pipelineOp in PipeLineOperations)
                {
                    // only run operations that are both InPipeline and Live
                    if (!pipelineOp.InPipeline || !pipelineOp.Live) continue;

                    // avoid duplicate parallel runs for the same operation
                    if (!_liveRunning.Add(pipelineOp)) continue;

                    try
                    {
                        // run heavy work off UI thread; op.Execute(...) should marshal to UI if it updates UI.
                        await Task.Run(() =>
                        {
                            try
                            {
                                pipelineOp.Execute(this);
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


        //private async void OnOperationParameterChanged(PipeLineOperation op, PipeLineParameter? param)
        //{
        //    // simple guard to avoid re-entrancy (if parameter change triggers reset which triggers param change)
        //    if (_handlingOps.Contains(op)) return;

        //    try
        //    {
        //        _handlingOps.Add(op);

        //        // 1) Reset the current image to original using the same logic as Reset button.
        //        //    If you have a dedicated Reset method, call it instead of invoking the click handler.
        //        //    Example uses the ResetButton_Click event handler you mentioned exists.
        //        try
        //        {
        //            // ensure this runs on UI thread
        //            Dispatcher.Invoke(() => ResetPreview());
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"Reset failed in parameter-change handler: {ex}");
        //        }

        //        // 2) If this operation is marked Live, run it
        //        if (op.Live)
        //        {
        //            // Run the operation off the UI thread if it is heavy; otherwise run on UI thread.
        //            // I use Task.Run to avoid blocking UI; if Execute touches UI, marshal inside Execute accordingly.
        //            await Task.Run(() =>
        //            {
        //                try
        //                {
        //                    op.Execute(this); // call the existing Run logic for this operation
        //                }
        //                catch (Exception ex)
        //                {
        //                    Debug.WriteLine($"Live execution failed for {op.DisplayName}: {ex}");
        //                }
        //            });
        //        }
        //    }
        //    finally
        //    {
        //        _handlingOps.Remove(op);
        //    }
        //}

        private void InitializePipeLineOperations()
        {
            _pipeLineOperations.Clear();

            

            _pipeLineOperations.Add(new PipeLineOperation(
                "Deskew",
                "Preview",
                new[]
                {
                    new PipeLineParameter("Algorithm", "deskewAlgorithm", new [] {"Auto", "ByBorders", "Hough", "Projection", "PCA" }, 0),
                    new PipeLineParameter("cannyTresh1", "cannyTresh1", 50, 10, 250, 1),
                    new PipeLineParameter("cannyTresh2", "cannyTresh2", 150, 10, 250, 1),
                    new PipeLineParameter("Morph kernel", "morphKernel", 5, 1, 10, 1),
                    new PipeLineParameter("Hough min line length", "minLineLength", 200, 0, 20000, 1),
                    new PipeLineParameter("Hough threshold", "houghTreshold", 80, 5, 250, 1)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.Deskew, operation.CreateParameterDictionary()))
                );

            _pipeLineOperations.Add(new PipeLineOperation(
                "Border Removal",
                "Preview",
                new[]
                {
                    new PipeLineParameter("Threshold Frac", "threshFrac", 0.40, 0.05, 1.00, 0.05),
                    new PipeLineParameter("Contrast Threshold", "contrastThr", 50, 1, 250, 1),
                    new PipeLineParameter("Central Sample", "centralSample", 0.10, 0.01, 1.00, 0.01),
                    new PipeLineParameter("Max remove frac", "maxRemoveFrac", 0.45, 0.01, 1.00, 0.01)
                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.BorderRemove, operation.CreateParameterDictionary())));

            //_pipeLineOperations.Add(new PipeLineOperation(
            //    "Auto Crop",
            //    "Run",
            //    new[]
            //    {
            //        new PipeLineParameter("Padding", "CropPadding", 8, 0, 100, 1)
            //    },
            //    (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.AutoCropRectangle, operation.CreateParameterDictionary())));

            

            _pipeLineOperations.Add(new PipeLineOperation(
                "Auto Binarize",
                "Run",
                new[]
                {
                    new PipeLineParameter("Algorithm", "binarizeAlgorithm", new [] {"Treshold", "Sauvola", "Adaptive"}, 0),
                    // Treshold alg
                    new PipeLineParameter("Treshold", "BinarizeTreshold", 128, 0, 255, 1),
                    // Adaptive alg
                    new PipeLineParameter("BlockSize", "blockSize", 3, 3, 255, 2),
                    new PipeLineParameter("Constant C", "C", 14, -50.0, 50.0, 1),

                     // new boolean checkbox parameters:
                    new PipeLineParameter("Use Gaussian", "useGaussian", false),
                    new PipeLineParameter("Apply Morphology", "useMorphology", false),

                    // morphology-specific numeric params (visible only if Apply Morphology == true — see ниже how to hide/show)
                    new PipeLineParameter("Morph kernel", "morphKernelBinarize", 3, 1, 21, 2),
                    new PipeLineParameter("Morph iterations", "morphIterationsBinarize", 1, 0, 5, 1),

                },
                (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.Binarize, operation.CreateParameterDictionary())));


            //_pipeLineOperations.Add(new PipeLineOperation(
            //    "Batch Processing",
            //    "Process Folder",
            //    new[]
            //    {
            //        new PipeLineParameter("Parallel", "Parallelism", 1, 1, Environment.ProcessorCount, 1)
            //    },
            //    (window, operation) => window.ProcessFolderClick(window, new RoutedEventArgs())));
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

        private async Task RunLiveOperationsForNewImageAsync()
        {
            try
            {
                // Если оригинального изображения нет — ничего не делать
                if (_viewModel?.OriginalImage == null) return;

                // Сброс к оригиналу (на UI-потоке)
                Dispatcher.Invoke(() => ResetPreview());

                // Выполняем последовательно в порядке PipeLineOperations все операции с Live == true
                foreach (var op in PipeLineOperations)
                {
                    if (!op.Live || !op.InPipeline) continue;

                    // защита от повторного параллельного запуска одной и той же операции
                    if (!_liveRunning.Add(op)) continue;

                    try
                    {
                        await Task.Run(() =>
                        {
                            try
                            {
                                op.Execute(this);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"RunLiveOperations: execution failed for {op.DisplayName}: {ex}");
                            }
                        });
                    }
                    finally
                    {
                        _liveRunning.Remove(op);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RunLiveOperationsForNewImageAsync failed: {ex}");
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

        private async void PipelineListBox_Drop(object sender, DragEventArgs e)
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
                    _viewModel.CurrentImagePath = fileName;
                    _viewModel.LastOpenedFolder = System.IO.Path.GetDirectoryName(fileName);

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
                var folder = _viewModel.LastOpenedFolder;
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
                int newIdx = next ? i+1 : i-1;
                if (newIdx < 0 || newIdx >= files.Length) return;

                var target = files[newIdx];
                await _manager.SetImageOnPreview(target);
                _viewModel.CurrentImagePath = target;
                _viewModel.LastOpenedFolder = folder;

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

            }
            //StatusText.Text = "Ready";
            //MyProgressBar.Value = 0;
        }

        private void ApplyCurrentPipelineToCurrentFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // попытка взять папку из LastOpenedFolder, иначе - из пути текущего файла
                //string? folder = _viewModel?.LastOpenedFolder;
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

                // вызываем менеджер — он уже делает обработку папки
                _manager.ProcessFolder(folder);
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

        private void ResetPreview()
        {
            if (_viewModel.OriginalImage == null) return;
            var originalImage = _viewModel.OriginalImage;

            _viewModel.ImageOnPreview = originalImage;
            _manager.SetImageForProcessing(originalImage);
        }

        private void SaveAs_Click(object sender, RoutedEventArgs e)
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

    public class PipeLineOperation : INotifyPropertyChanged
    {
        private readonly ObservableCollection<PipeLineParameter> _parameters;
        private readonly Action<MainWindow, PipeLineOperation>? _execute;

        public event Action<PipeLineOperation, PipeLineParameter?>? ParameterChanged;

        private bool _inPipeline = false;
        private bool _live = false;

        public PipeLineOperation(string displayName, string actionLabel, IEnumerable<PipeLineParameter> parameters, Action<MainWindow, PipeLineOperation> execute)
        {
            DisplayName = displayName;
            ActionLabel = actionLabel;
            _parameters = new ObservableCollection<PipeLineParameter>(parameters ?? Enumerable.Empty<PipeLineParameter>());
            _execute = execute;
            _inPipeline = false;


            InitializeParameterVisibilityRules();
            HookParameterChanges();
        }

        public event Action<PipeLineOperation>? LiveChanged;

        public string DisplayName { get; }

        public string ActionLabel { get; }

        public ObservableCollection<PipeLineParameter> Parameters => _parameters;

        public bool InPipeline
        {
            get => _inPipeline;
            set
            {
                if (_inPipeline != value)
                {
                    _inPipeline = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool Live
        {
            get => _live;
            set
            {
                if (_live != value)
                {
                    _live = value;
                    OnPropertyChanged();
                    LiveChanged?.Invoke(this); // notify subscribers (optional)
                }
            }
        }

        private void HookParameterChanges()
        {
            foreach (var p in _parameters)
            {
                // attach a lightweight handler to forward notifications
                p.PropertyChanged += (s, e) =>
                {
                    // forward (operation, parameter) to listeners
                    ParameterChanged?.Invoke(this, p);
                    
                };
            }
        }

        public void Execute(MainWindow window)
        {
            _execute?.Invoke(window, this);
        }

        private void InitializeParameterVisibilityRules()
        {
            // Deskew algorithm rules
            var algo = _parameters.FirstOrDefault(p => p.Key == "deskewAlgorithm");
            if (algo != null)
            {
                // apply initial visibility
                ApplyDeskewVisibility(algo.SelectedOption);

                // listen for changes on the combo parameter
                algo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex) ||
                        e.PropertyName == nameof(PipeLineParameter.SelectedOption))
                    {
                        ApplyDeskewVisibility(algo.SelectedOption);
                    }
                };
            }

            // Binarize algorithm rules example
            var binAlgo = _parameters.FirstOrDefault(p => p.Key == "binarizeAlgorithm");
            if (binAlgo != null)
            {
                // initial apply
                ApplyBinarizeVisibility(binAlgo.SelectedOption);

                // ensure morph fields reflect the current state right away
                var morphFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "useMorphology");
                if (morphFlagImmediate != null)
                {
                    // set initial visibility of morph fields based on current checkbox value
                    ApplyMorphVisibility(morphFlagImmediate.BoolValue);
                    // subscribe once so further toggles update visibility
                    morphFlagImmediate.PropertyChanged -= MorphFlag_PropertyChanged;
                    morphFlagImmediate.PropertyChanged += MorphFlag_PropertyChanged;
                }

                // Also listen for changes of the algorithm selection and re-evaluate
                binAlgo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex) ||
                        e.PropertyName == nameof(PipeLineParameter.SelectedOption))
                    {
                        // re-evaluate which controls are visible based on chosen algorithm
                        ApplyBinarizeVisibility(binAlgo.SelectedOption);
                    }
                };
            }
        }

        private void ApplyMorphVisibility(bool enabled)
        {
            foreach (var p in _parameters)
            {
                if (p.Key == "morphKernelBinarize" || p.Key == "morphIterationsBinarize")
                    p.IsVisible = enabled;
            }
        }

        private void ApplyDeskewVisibility(string? selectedOption)
        {
            // default to Auto if null
            var selected = (selectedOption ?? "Auto").Trim();

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "deskewAlgorithm":
                        p.IsVisible = true; // algorithm selector always visible
                        break;

                    // show these only for ByBorders
                    case "cannyTresh1":
                    case "cannyTresh2":
                    case "morphKernel":
                        p.IsVisible = selected.Equals("ByBorders", StringComparison.OrdinalIgnoreCase);
                        break;

                    // show these only for Hough
                    case "minLineLength":
                    case "houghTreshold":
                        p.IsVisible = selected.Equals("Hough", StringComparison.OrdinalIgnoreCase);
                        break;

                    default:
                        // keep other parameters visible by default
                        p.IsVisible = true;
                        break;
                }
            }
        }

        private void ApplyBinarizeVisibility(string? selectedOption)
        {
            // find the binarizeAlgorithm parameter (source of truth)
            var binAlgo = _parameters.FirstOrDefault(x => x.Key == "binarizeAlgorithm");

            // robust "isAdaptive" detection:
            // 1) prefer SelectedOption string if available
            // 2) otherwise fallback to SelectedIndex (index 2 means "Adaptive" in your options order)
            bool isAdaptive = false;
            if (binAlgo != null)
            {
                var opt = (binAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isAdaptive = opt.Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
                else
                    isAdaptive = binAlgo.SelectedIndex == 2; // defensive fallback: index 2 = Adaptive
            }
            else
            {
                // fallback if binAlgo missing — keep previous behaviour
                isAdaptive = (selectedOption ?? "").Trim().Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
            }

            // find useMorphology flag once
            var morphFlag = _parameters.FirstOrDefault(x => x.Key == "useMorphology");
            bool useMorph = morphFlag != null && morphFlag.IsBool && morphFlag.BoolValue;

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "binarizeAlgorithm":
                        p.IsVisible = true;
                        break;

                    case "BinarizeTreshold":
                        p.IsVisible = (binAlgo != null)
                            ? ((binAlgo.SelectedOption ?? "").Equals("Treshold", StringComparison.OrdinalIgnoreCase)
                                || binAlgo.SelectedIndex == 0)
                            : (selectedOption ?? "").Trim().Equals("Treshold", StringComparison.OrdinalIgnoreCase);
                        break;

                    case "blockSize":
                    case "C":
                    case "useGaussian":
                    case "useMorphology":
                        p.IsVisible = isAdaptive;
                        break;

                    case "morphKernelBinarize":
                    case "morphIterationsBinarize":
                        // visible only when algorithm == Adaptive AND ApplyMorphology checked
                        p.IsVisible = isAdaptive && useMorph;
                        break;

                    default:
                        p.IsVisible = true;
                        break;
                }
            }
        }

        private void MorphFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter morphParam) return;

            // compute current algorithm -> isAdaptive (same logic as ApplyBinarizeVisibility)
            var binAlgo = _parameters.FirstOrDefault(x => x.Key == "binarizeAlgorithm");
            bool isAdaptive = false;
            if (binAlgo != null)
            {
                var opt = (binAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isAdaptive = opt.Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
                else
                    isAdaptive = binAlgo.SelectedIndex == 2;
            }

            bool useMorph = morphParam.BoolValue;

            foreach (var q in _parameters)
            {
                if (q.Key == "morphKernelBinarize" || q.Key == "morphIterationsBinarize")
                    q.IsVisible = isAdaptive && useMorph;
            }
        }


        public Dictionary<string, object> CreateParameterDictionary()
        {
            return _parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => (object)(
                    parameter.IsCombo ? (object?)parameter.SelectedOption ?? string.Empty :
                    parameter.IsBool ? (object)parameter.BoolValue :
                                        (object)parameter.Value
                )
            );
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public class PipeLineParameter : INotifyPropertyChanged
    {
        private readonly double _min;
        private readonly double _max;
        private double _value;

        private bool _isVisible = true;
        private bool _isBool = false;
        private bool _boolValue = false;

        private IList<string>? _options;
        private int _selectedIndex;

        public PipeLineParameter(string label, string key, double value, double min, double max, double step)
        {
            Label = label;
            Key = key;
            _min = min;
            _max = max;
            Step = step <= 0 ? 1 : step;
            _value = Clamp(value);
            _options = null;
            _selectedIndex = -1;
        }

        // constructor for ComboBox parameter
        public PipeLineParameter(string label, string key, IEnumerable<string> options, int selectedIndex = 0)
        {
            Label = label;
            Key = key;
            Step = 1;
            _min = double.NaN;
            _max = double.NaN;
            _value = double.NaN;

            _options = options?.ToList() ?? new List<string>();
            SelectedIndex = Math.Max(0, Math.Min(_options.Count - 1, selectedIndex));
        }

        // constructor for CheckBox
        public PipeLineParameter(string label, string key, bool boolValue)
        {
            Label = label;
            Key = key;
            Step = 1;
            _min = double.NaN;
            _max = double.NaN;
            _value = double.NaN;
            _options = null;
            _selectedIndex = -1;

            _isBool = true;
            _boolValue = boolValue;
        }

        public bool IsBool => _isBool;



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

        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (_boolValue != value)
                {
                    _boolValue = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
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

        // --- Combo properties ---
        public IList<string>? Options
        {
            get => _options;
            // rarely changed at runtime; if you set it, update IsCombo
            set
            {
                _options = value;
                OnPropertyChanged(nameof(Options));
                OnPropertyChanged(nameof(IsCombo));
            }
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_options == null || _options.Count == 0)
                {
                    _selectedIndex = -1;
                }
                else
                {
                    int idx = Math.Max(0, Math.Min(_options.Count - 1, value));
                    if (_selectedIndex != idx)
                    {
                        _selectedIndex = idx;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(SelectedOption));
                    }
                }
            }
        }

        public string? SelectedOption => (Options != null && SelectedIndex >= 0 && SelectedIndex < Options.Count) ? Options[SelectedIndex] : null;

        // convenience flag for XAML
        public bool IsCombo => Options != null && Options.Count > 0;

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
