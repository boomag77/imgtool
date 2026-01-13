using ImgViewer.Interfaces;
using ImgViewer.Views;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class AppManager : IAppManager, IDisposable
    {

        private readonly IViewModel _mainViewModel;
        private readonly BatchViewModel _batchViewModel = new();
        private readonly IFileProcessor _fileProcessor;
        private readonly IImageProcessor _imageProcessor;
        private readonly AppSettings _appSettings;
        private readonly Pipeline _pipeline;

        private readonly List<string> _uiErrors = new();
        private readonly object _uiErrorsLock = new();

        private readonly SemaphoreSlim _currentImageLock = new(1, 1);
        private readonly SemaphoreSlim _imageLoadLock = new(1, 1);
        private readonly object _batchChoiceLock = new();
        private ExistingFilesChoice? _batchExistingFilesChoice;
        private readonly object _batchCancelLock = new();
        private readonly HashSet<string> _canceledFolders = new(StringComparer.OrdinalIgnoreCase);
        private string? _currentBatchFolder;

        public event Action? BatchProgressDismissRequested;

        private readonly CancellationTokenSource _cts;
        private CancellationTokenSource? _poolCts;
        private CancellationTokenSource? _rootFolderCts;
        private CancellationTokenSource _imgProcCts;

        //private DocBoundaryModel _docBoundaryModel;

        private volatile bool _inRootFolderBatch = false;
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _rootBatchIssues
            = new System.Collections.Concurrent.ConcurrentQueue<string>();

        public Pipeline CurrentPipeline => _pipeline;

        public AppManager(IMainView mainView, CancellationTokenSource cts)
        {
            _cts = cts;
            _appSettings = new AppSettings();
            _pipeline = new Pipeline(this);
            _mainViewModel = new MainViewModel(this);
            mainView.ViewModel = _mainViewModel;
            _fileProcessor = new FileProcessor(_cts.Token);

            //_fileProcessor.ErrorOccured += (msg) => ReportError(msg, null, "File Processor Error");
            _fileProcessor.ErrorOccured += OnFileProcessorError;

            _imgProcCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _imageProcessor = new OpenCvImageProcessor(this, _imgProcCts.Token, Environment.ProcessorCount - 1, true);

            _imageProcessor.ErrorOccured += (msg) => ReportError(msg, null, "Image Processor Error");


        }

        public BatchViewModel BatchViewModel => _batchViewModel;

        private void UpdateBatchViewModel(Action<BatchViewModel> update)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                update(_batchViewModel);
            }
            else
            {
                dispatcher.InvokeAsync(() => update(_batchViewModel),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void RequestBatchProgressDismiss()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                BatchProgressDismissRequested?.Invoke();
            }
            else
            {
                dispatcher.InvokeAsync(() => BatchProgressDismissRequested?.Invoke(),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private static string BuildBatchDisplayName(string folderPath)
        {
            var folder = Path.GetFileName(folderPath);
            var parent = Path.GetFileName(Path.GetDirectoryName(folderPath) ?? string.Empty);
            if (string.IsNullOrWhiteSpace(parent)) return folder;
            return $"{parent}/{folder}";
        }

        private ExistingFilesChoice? GetBatchExistingFilesChoice()
        {
            lock (_batchChoiceLock)
            {
                return _batchExistingFilesChoice;
            }
        }

        private void SetBatchExistingFilesChoice(ExistingFilesChoice choice)
        {
            if (choice != ExistingFilesChoice.YesToAll && choice != ExistingFilesChoice.NoToAll)
                return;

            lock (_batchChoiceLock)
            {
                _batchExistingFilesChoice = choice;
            }
        }

        private bool IsFolderCanceled(string folderPath)
        {
            lock (_batchCancelLock)
            {
                return _canceledFolders.Contains(folderPath);
            }
        }

        private bool IsCurrentFolder(string folderPath)
        {
            lock (_batchCancelLock)
            {
                return _currentBatchFolder != null &&
                       string.Equals(_currentBatchFolder, folderPath, StringComparison.OrdinalIgnoreCase);
            }
        }

        public void CancelFolderProcessing(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                CancelBatchProcessing();
                return;
            }

            lock (_batchCancelLock)
            {
                _canceledFolders.Add(folderPath);
            }

            UpdateBatchViewModel(vm => vm.Remove(folderPath));

            if (IsCurrentFolder(folderPath))
            {
                _poolCts?.Cancel();
                UpdateProgress(0);
            }
        }

        //public DocBoundaryModel DocBoundaryModel => _docBoundaryModel;

        public bool IsSavePipelineToMd
        {
            get { return _appSettings.SavePipeLineToMd; }
            set { _appSettings.SavePipeLineToMd = value; }
        }

        public TimeSpan ParametersChangedDebounceDelay
        {
            get { return _appSettings.ParametersChangedDebounceDelay; }
            set { _appSettings.ParametersChangedDebounceDelay = value; }
        }

        public double EraseOperationOffset
        {
            get { return _appSettings.EraseOperationOffset; }
            set { _appSettings.EraseOperationOffset = value; }
        }



        public TiffCompression CurrentTiffCompression
        {
            get { return _appSettings.TiffCompression; }
            set { _appSettings.TiffCompression = value; }
        }

        public string LastSavedFolder
        {
            get
            {
                return _appSettings.LastSavedFolder;
            }
            set
            {
                _appSettings.LastSavedFolder = value;
            }
        }

        public string LastOpenedFolder
        {
            get
            {
                return _appSettings.LastOpenedFolder;
            }
            set
            {
                _appSettings.LastOpenedFolder = value;
            }
        }

        private void OnFileProcessorError(string msg)
        {
            if (_inRootFolderBatch)
            {
                _rootBatchIssues.Enqueue("[FileProcessor] " + msg);
                return;
            }

            // обычный режим — как было
            ReportError(msg, null, "File Processor Error");
        }


        public void Shutdown()
        {
            _cts.Cancel();
            _cts.Dispose();
            _poolCts?.Dispose();
            _rootFolderCts?.Dispose();
            _imgProcCts?.Dispose();
            (_imageProcessor as IDisposable)?.Dispose();
            (_fileProcessor as IDisposable)?.Dispose();
            Dispose();
        }

        private void ReportError(string message, Exception ex = null, string title = "Error")
        {
            // собираем полный текст (для лога / списка ошибок)
            var fullMessage = ex != null
                ? $"{message}{Environment.NewLine}{ex.Message}"
                : message;

            lock (_uiErrorsLock)
            {
                _uiErrors.Add(fullMessage);
            }

            // показываем MessageBox безопасно с точки зрения UI-потока
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                System.Windows.MessageBox.Show(fullMessage, title, MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                dispatcher.InvokeAsync(
                    () => System.Windows.MessageBox.Show(fullMessage, title, MessageBoxButton.OK, MessageBoxImage.Error),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        public void UpdateStatus(string status)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                _mainViewModel.Status = status;
            else
                dispatcher.InvokeAsync(() =>
                {
                    _mainViewModel.Status = status;
                }, System.Windows.Threading.DispatcherPriority.Background);

            //_mainViewModel.Status = status;
        }

        private void UpdateProgress(int percent)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                _mainViewModel.Progress = percent;
            }
            else
            {
                dispatcher.InvokeAsync(() => _mainViewModel.Progress = percent,
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        public void CancelImageProcessing()
        {
            try
            {
                _imgProcCts.Cancel();
            }
            catch { }

            _imgProcCts.Dispose();

            // новый токен, снова привязанный к global _cts
            _imgProcCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            // сообщаем процессору, что токен сменился
            _imageProcessor.UpdateCancellationToken(_imgProcCts.Token);
        }

        public async Task SetImageForProcessing(ImageSource bmp)
        {
            //await Task.Run(() => _imageProcessor.CurrentImage = bmp);
            await _currentImageLock.WaitAsync();
            try
            {
                await Task.Run(() => _imageProcessor.CurrentImage = bmp);
            }
            finally
            {
                _currentImageLock.Release();
            }
        }

        public async Task SetBmpImageOnPreview(ImageSource bmp)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.ImageOnPreview = bmp;
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        public void SetSplitPreviewImages(ImageSource left, ImageSource right)
        {
            if (left is Freezable lf && !lf.IsFrozen)
                lf.Freeze();
            if (right is Freezable rf && !rf.IsFrozen)
                rf.Freeze();

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                _mainViewModel.SetSplitPreviewImages(left, right);
            }
            else
            {
                dispatcher.InvokeAsync(() => _mainViewModel.SetSplitPreviewImages(left, right),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
        }

        public void ClearSplitPreviewImages()
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                _mainViewModel.ClearSplitPreviewImages();
            }
            else
            {
                dispatcher.InvokeAsync(() => _mainViewModel.ClearSplitPreviewImages(),
                    System.Windows.Threading.DispatcherPriority.Normal);
            }
        }

        private async Task SetBmpImageAsOriginal(ImageSource bmp)
        {

            UpdateStatus("Setting original image on preview...");

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.OriginalImage = bmp;
            }, System.Windows.Threading.DispatcherPriority.Background);
            UpdateStatus("Standby");
        }

        public async Task SetImageOnPreview(string imagePath)
        {
            await _imageLoadLock.WaitAsync();
            try
            {

                var (bmpImage, bytes) = await Task.Run(() => _fileProcessor.LoadImageSource(imagePath, isBatch: false));
                if (bmpImage is BitmapSource bmpSource && !bmpSource.IsFrozen)
                    bmpSource.Freeze();

                _mainViewModel.CurrentImagePath = imagePath;
                var tasks = new List<Task>();
                tasks.Add(Task.Run(() => SetBmpImageAsOriginal(bmpImage)));
                tasks.Add(Task.Run(() => SetImageForProcessing(bmpImage)));
                //tasks.Add(Task.Run(() => SetBmpImageOnPreview(bmpImage)));
                await Task.WhenAll(tasks);
                //await SetBmpImageAsOriginal(bmpImage);
                //await SetBmpImageOnPreview(bmpImage);
                ClearSplitPreviewImages();
                //await SetImageForProcessing(bmpImage);
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Loading image was canceled by user.");
#endif
            }
            catch (Exception ex)
            {
                string msg = $"Error loading image: {ex.Message}.";
                ReportError(msg, ex, "Error");
                _mainViewModel.CurrentImagePath = string.Empty;
            }
            finally
            {
                _imageLoadLock.Release();
            }
        }

        public async Task ResetWorkingImagePreview()
        {
            CancelImageProcessing();
            ClearSplitPreviewImages();
            if (_imageProcessor is OpenCvImageProcessor ocvProcessor)
                ocvProcessor.ClearSplitResults();
            if (_mainViewModel.OriginalImage == null) return;
            await SetImageForProcessing(_mainViewModel.OriginalImage);
        }

        public async Task ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            if (_mainViewModel.OriginalImage == null) return;

            UpdateStatus($"Applying command: {command}...");
            try
            {
                await Task.Run(() =>
                {
                    _imageProcessor.ApplyCommand(
                                            command,
                                            parameters,
                                            batchProcessing: false);
                });
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine($"Command {command} canceled by user.");
#endif
            }
            catch (Exception ex)
            {
                string msg = $"Error applying command: {command}.";
                ReportError(msg, ex, "Error");
                //Debug.WriteLine(msg);
                //System.Windows.MessageBox.Show(
                //        $"Error while applying {command}: {ex.Message}",
                //        "Error",
                //        MessageBoxButton.OK,
                //        MessageBoxImage.Error);
            }
            finally
            {
                UpdateStatus("Standby");
            }



        }

        public async Task SaveProcessedImageToTiff(string outputPath, ImageFormat format)
        {
            var tiffInfo = new TiffInfo();
            try
            {
                tiffInfo = await Task.Run(() => _imageProcessor.GetTiffInfo(TiffCompression.CCITTG4, 300));
                _fileProcessor.SaveTiff(
                    tiffInfo,
                    outputPath,
                    overwrite: true,
                    metadataJson: null);
            }
            catch (Exception ex)
            {

                ReportError($"Error getting Tiff info: {ex.Message}", ex, "Tiff Info Error");
            }
        }


        public async Task SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression, string imageDescription = null)
        {
            try
            {
                using var stream = _imageProcessor.GetStreamForSaving(ImageFormat.Tiff, compression);
                if (stream.CanSeek)
                    stream.Position = 0;

                string json = IsSavePipelineToMd ? _pipeline.BuildPipelineForSave() : null;


                await Task.Run(() => _fileProcessor.SaveTiff(stream, outputPath, compression, 300, true, json));
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Saving image was canceled by user.");
#endif
            }
            catch (Exception ex)
            {
                string msg = $"Error while saving processed image to: {outputPath}";
                ReportError(msg, ex, "Error");
            }
        }

        public async Task SavePipelineToJSON(string path, string json)
        {
            var folder = Path.GetDirectoryName(path);
            string pipeLineForSave = json;
            string fileName = Path.GetFileName(path);
            try
            {
                await Task.Run(() => File.WriteAllText(Path.Combine(folder, fileName), pipeLineForSave));

#if DEBUG
                Debug.WriteLine("Pipeline saved to " + fileName);
#endif
            }
            catch (Exception ex)
            {
                ReportError($"Error while saving Pipeline to JSON: {ex.Message}", ex, "Save pipeline");
            }

        }

        private static bool TryHasAnyImageFast(string folderPath, CancellationToken token, out string? issue)
        {
            issue = null;

            try
            {
                foreach (var f in Directory.EnumerateFiles(folderPath))
                {
                    token.ThrowIfCancellationRequested();

                    if (f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                        return true;
                }

                return false; // просто нет картинок
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (ex is IOException ||
                                      ex is UnauthorizedAccessException ||
                                      ex is PathTooLongException ||
                                      ex is DirectoryNotFoundException)
            {
                issue = $"SKIP_ENUM_FILES: '{folderPath}' ({ex.GetType().Name}) {ex.Message}";
                return false;
            }
        }


        public async Task ProcessRootFolder(string rootFolderPath, Pipeline pipeline, bool fullTree = true)
        {
            if (pipeline == null) return;
            _rootFolderCts?.Cancel();
            _rootFolderCts?.Dispose();

            _inRootFolderBatch = true;
            _batchExistingFilesChoice = null;
            lock (_batchCancelLock)
            {
                _canceledFolders.Clear();
            }
            while (_rootBatchIssues.TryDequeue(out _)) { }
            UpdateBatchViewModel(vm => vm.Clear());

            _rootFolderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var batchToken = _rootFolderCts.Token;

            //var debug = false;


            var startTime = DateTime.Now;

            UpdateStatus($"Processing folders in " + rootFolderPath);
            //_mainViewModel.Status = $"Processing folders in " + rootFolder;

            var processedCount = 0;
            var visitedCount = 0;

            var folderPaths = _fileProcessor.EnumerateSubFolderPaths(rootFolderPath, fullTree, batchToken);



            try
            {
                var pendingFolders = new List<string>();
                foreach (var folderPath in folderPaths)
                {
                    if (batchToken.IsCancellationRequested) break;
                    try
                    {
                        visitedCount++;
                
                        if (!TryHasAnyImageFast(folderPath, batchToken, out var issue))
                        {
                            // ???? issue == null  ? ?????? ??? ???????????
                            _rootBatchIssues.Enqueue(issue ?? $"SKIP_NO_IMAGES: '{folderPath}'");
                            continue;
                        }
                
                        pendingFolders.Add(folderPath);
                    }
                    catch (OperationCanceledException)
                    {
                #if DEBUG
                        Debug.WriteLine("Processing Root Folder was canceled.");
                #endif
                        break;
                    }
                    catch (Exception ex)
                    {
                        _rootBatchIssues.Enqueue($"[ProcessFolder] {folderPath}: {ex.Message}");
                        if (!_inRootFolderBatch)
                            ReportError($"Error while processing sub-folder {folderPath} in root folder {rootFolderPath}.", ex, "Processing Error");
                    }
                }
                
                UpdateBatchViewModel(vm =>
                {
                    foreach (var folderPath in pendingFolders)
                    {
                        vm.AddPending(folderPath, BuildBatchDisplayName(folderPath));
                    }
                });
                
                foreach (var folderPath in pendingFolders)
                {
                    if (batchToken.IsCancellationRequested) break;
                    try
                    {
                        if (IsFolderCanceled(folderPath))
                        {
                            UpdateBatchViewModel(vm => vm.Remove(folderPath));
                            continue;
                        }

                        await ProcessFolder(folderPath, pipeline, batchToken).ConfigureAwait(false);
                        processedCount++;
                    }
                    catch (OperationCanceledException)
                    {
                #if DEBUG
                        Debug.WriteLine("Processing Root Folder was canceled.");
                #endif
                        break;
                    }
                    catch (Exception ex)
                    {
                        _rootBatchIssues.Enqueue($"[ProcessFolder] {folderPath}: {ex.Message}");
                        if (!_inRootFolderBatch)
                            ReportError($"Error while processing sub-folder {folderPath} in root folder {rootFolderPath}.", ex, "Processing Error");
                    }
                }
                var duration = DateTime.Now - startTime;
                var durationHours = (int)duration.TotalHours;
                var durationMinutes = duration.Minutes;
                var durationSeconds = duration.Seconds;
                var logMsg = $"Processed {processedCount} folders (visited {visitedCount}) from ** {rootFolderPath} **.";
                var timeMsg = $"Completed in {durationHours} hours, {durationMinutes} minutes, {durationSeconds} seconds.";
                var opsLog = new List<string>();
                foreach (var op in pipeline.Operations)
                {
                    opsLog.Add($"- {op.Command}");
                }
                var plOps = pipeline.Operations.Count > 0 ? string.Join(Environment.NewLine, opsLog) : "No operations were performed.";
                var plJson = pipeline.BuildPipelineForSave();
                try
                {
                    //File.WriteAllLines(
                    //    Path.Combine(rootFolderPath, "_processing_log.txt"),
                    //    new string[] { logMsg, timeMsg, "Operations performed:", plOps, "\n", plJson }
                    //);
                    var logPath = Path.Combine(rootFolderPath, "_processing_log.txt");
                    var logLines = new List<string>
                    {
                        logMsg,
                        timeMsg,
                        "Operations performed:",
                        plOps,
                        "",
                        plJson
                    };

                    if (!_rootBatchIssues.IsEmpty)
                    {
                        logLines.Add("");
                        logLines.Add("Issues / skipped folders:");
                        while (_rootBatchIssues.TryDequeue(out var issue))
                            logLines.Add("- " + issue);
                    }

                    File.WriteAllLines(logPath, logLines);

                }
                catch (Exception ex)
                {
                    ReportError($"Failed to write processing log to {rootFolderPath}", ex, "Logging Error");
                }

            }
            catch (OperationCanceledException)
            {
                // iterator мог бросить из ThrowIfCancellationRequested()
            }
            catch (Exception ex)
            {
                ReportError($"Error while enumerating folders in root folder: {rootFolderPath}", ex, "Processing Error");
            }
            finally
            {
                _inRootFolderBatch = false;

                UpdateStatus("Standby");
                try { _rootFolderCts?.Dispose(); } catch { }
                _rootFolderCts = null;
            }
        }

        public async Task ProcessFolder(string srcFolder)
        {
            _rootFolderCts?.Cancel();
            _rootFolderCts?.Dispose();
            _rootFolderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _batchExistingFilesChoice = null;
            lock (_batchCancelLock)
            {
                _canceledFolders.Clear();
            }

            var token = _rootFolderCts.Token;
            var pipelines = CurrentPipeline.CreatePipelinesForBatchProcessing();

            foreach (var pipeline in pipelines)
            {
                token.ThrowIfCancellationRequested();
                await ProcessFolder(srcFolder, pipeline, token);
            }
        }


        private async Task ProcessFolder(string srcFolder, Pipeline pipeline, CancellationToken batchToken)
        {
            if (pipeline == null) return;

            lock (_batchCancelLock)
            {
                _currentBatchFolder = srcFolder;
            }

            UpdateStatus($"Processing folder " + srcFolder);
            UpdateProgress(0);
            UpdateBatchViewModel(vm =>
            {
                vm.AddPending(srcFolder, BuildBatchDisplayName(srcFolder));
                vm.SetInProgress(srcFolder);
                vm.SetProgress(srcFolder, 0);
            });
            UpdateProgress(0);


            //var sourceFolder = _fileProcessor.GetImageFilesPaths(srcFolder, batchToken);
            //if (sourceFolder == null || sourceFolder.Files == null || sourceFolder.Files.Length == 0) return;


            _poolCts?.Cancel();
            _poolCts?.Dispose();
            _poolCts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);


            //_poolCts = new CancellationTokenSource();

            var startTime = DateTime.Now;

            try
            {
                using (var workerPool = new ImgWorkerPool(
                    _poolCts,
                    pipeline,
                    0,
                    srcFolder,
                    0,
                    IsSavePipelineToMd,
                    GetBatchExistingFilesChoice,
                    SetBatchExistingFilesChoice,
                    () => CancelBatchProcessing()))
                {
                    try
                    {
                        workerPool.ProgressChanged += (processedCount, total) =>
                        {
                            if (_poolCts?.IsCancellationRequested == true || batchToken.IsCancellationRequested)
                            {
                                return;
                            }
                            int percent = total > 0 ? (int)Math.Round(processedCount * 100.0 / total) : 0;
                            UpdateProgress(percent);
                            UpdateBatchViewModel(vm => vm.SetProgress(srcFolder, percent));
                        };

                        await workerPool.RunAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
#if DEBUG
                        Debug.WriteLine("Processing Folder was canceled.");
#endif
                    }
                    catch (Exception ex)
                    {
                        ReportError("Error during processing folder.", ex, "Processing error");
                    }

                }
            }
            finally
            {

                UpdateStatus("Standby");
                UpdateBatchViewModel(vm => vm.Remove(srcFolder));
                if (_poolCts?.IsCancellationRequested == true || batchToken.IsCancellationRequested)
                {
                    UpdateProgress(0);
                }
                lock (_batchCancelLock)
                {
                    if (string.Equals(_currentBatchFolder, srcFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        _currentBatchFolder = null;
                    }
                }


                try
                {
                    _poolCts?.Dispose();
                }
                catch { }
                _poolCts = null;
            }
        }

        public async Task LoadPipelineFromFile(string fileNamePath)
        {

            try
            {
                string json = await Task.Run(() => File.ReadAllText(fileNamePath));
                CurrentPipeline.LoadPipelineFromJson(json);
            }
            catch (OperationCanceledException)
            {
                // Load was cancelled, do nothing
            }
            catch (Exception ex)
            {
                var msg = $"Error loading preset from file: {fileNamePath}";
                ReportError(msg, ex, "Load preset error");
            }

#if DEBUG
            //foreach (var op in pipeline)
            //{
            //    Debug.WriteLine($"Command: {op.Command}");
            //    foreach (var p in op.Parameters)
            //    {
            //        Debug.WriteLine($"  {p.Name} = {p.Value} (type: {p.Value?.GetType().Name ?? "null"})");
            //    }
            //}
#endif
        }

        public void CancelBatchProcessing()
        {
            try
            {
                _rootFolderCts?.Cancel();
                UpdateBatchViewModel(vm => vm.Clear());
                UpdateProgress(0);
                RequestBatchProgressDismiss();
            }
            catch (Exception ex)
            {
                ReportError($"Failed to cancel BatchProcessing. {ex}");
            }
        }



        public void Dispose()
        {
            _poolCts?.Dispose();
            _rootFolderCts?.Dispose();
            _imgProcCts?.Dispose();
            (_imageProcessor as IDisposable)?.Dispose();
            (_fileProcessor as IDisposable)?.Dispose();
            GC.SuppressFinalize(this);
        }



    }
}
