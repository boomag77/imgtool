using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;


namespace ImgViewer.Models
{
    internal class ImgWorkerPool : IDisposable
    {

        private sealed class SaveTaskInfo
        {
            public Stream? ImageStream { get; set; }
            public string OutputFilePath { get; set; } = string.Empty;
            public bool DisposeStream { get; set; } = false;

            public TiffInfo? TiffInfo { get; set; }
        }

        private bool _disposed;

        private readonly ConcurrentBag<string> _fileErrors = new();

        private readonly BlockingCollection<SourceImageFile> _filesQueue;
        private readonly BlockingCollection<SaveTaskInfo> _saveQueue;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _tokenRegistration;
        private readonly SourceImageFolder _sourceFolder;
        private string _outputFolder = string.Empty;
        private int _workersCount;

        private readonly int _maxSavingWorkers;
        private int _currentSavingWorkers = 0;

        private readonly object _savingLock = new();
        private readonly List<Task> _savingTasks = new();
        private readonly HashSet<string> _existingOutputNames;

        private bool overWriteExistingOutputs = true;


        private int _processedCount;
        private readonly int _totalCount;
        private (ProcessorCommand Command, Dictionary<string, object> Params)[] _plOperations;
        private readonly string? _plJson;
        private List<string> _opsLog = new List<string>();

        public event Action<string>? ErrorOccured;
        public event Action<int, int>? ProgressChanged;

        private string _batchErrorsPath = string.Empty;
        private readonly object _errorFileLock = new();

        public ImgWorkerPool(CancellationTokenSource cts,
                             Pipeline pipeline,
                             int maxWorkersCount,
                             SourceImageFolder sourceFolder,
                             int maxFilesQueue,
                             bool savePipelineToMd)
        {
            _cts = cts;
            _token = _cts.Token;
            _sourceFolder = sourceFolder;

            string sourceFolderName = Path.GetFileName(_sourceFolder.Path);
            _outputFolder = Path.Combine(_sourceFolder.ParentPath, sourceFolderName + "_processed");
            Directory.CreateDirectory(_outputFolder);
            _batchErrorsPath = Path.Combine(_outputFolder, "_batch_errors.txt");

            _existingOutputNames = LoadExistingOutputNamesAndCleanupTmp(_outputFolder);

            if (_existingOutputNames.Count > 0)
            {
                // inform user that there is existing files and ask for overwrite or skip existing files
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                string title = "Existing Processed Files Detected";
                string message =
                    $"The output folder '{_outputFolder}' already contains {_existingOutputNames.Count} processed TIFF files. Do you want to overwrite them (Yes) or Proceed (No). Press Cancel to cancel processing?";
                MessageBoxResult result = MessageBoxResult.Cancel;
                if (dispatcher == null || dispatcher.CheckAccess())
                {

                    result = System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                }
                else
                {
                    dispatcher.Invoke(() =>
                        result = System.Windows.MessageBox.Show(message, title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question),
                        System.Windows.Threading.DispatcherPriority.Background);
                }
                if (result == MessageBoxResult.Yes)
                {
                    overWriteExistingOutputs = true;
                }
                else if (result == MessageBoxResult.No)
                {
                    overWriteExistingOutputs = false;
                }
                else
                {
                    throw new OperationCanceledException("User cancelled processing due to existing files.");
                }
            }


            //_pipelineTemplate = pipelineTemplate ?? Array.Empty<(ProcessorCommand, Dictionary<string, object>)>();
            var opsSnapshot = pipeline.Operations
                .Where(op => op.InPipeline)
                .Select(op => (op.Command, Params: op.CreateParameterDictionary()))
                .ToArray();
            _plOperations = opsSnapshot;
            _plJson = savePipelineToMd ? pipeline.BuildPipelineForSave() : null;

            int cpuCount = Environment.ProcessorCount;
            _workersCount = maxWorkersCount == 0 ? Math.Max(1, cpuCount - 1) : maxWorkersCount;

            _filesQueue = new BlockingCollection<SourceImageFile>(
                maxFilesQueue == 0 ? _workersCount : maxFilesQueue
            );
            int saveQueueCapacity = Math.Max(2, _workersCount);
            _saveQueue = new BlockingCollection<SaveTaskInfo>(saveQueueCapacity);
            //_maxSavingWorkers = Math.Max(1, cpuCount/2);
            _maxSavingWorkers = 1;
            _tokenRegistration = _token.Register(() =>
            {
                try { _filesQueue.CompleteAdding(); } catch { }
                //try { _saveQueue.CompleteAdding(); } catch { } gracefull cancel
            });
            _processedCount = 0;
            _totalCount = sourceFolder.Files.Length;
            foreach (var op in opsSnapshot)
            {
                _opsLog.Add($"- {op.Command}");
            }
        }

        private void RegisterFileError(string filePath, string message, Exception ex = null)
        {
            var msg = $"[{filePath}] {message}" + (ex != null ? $" :: {ex.Message}" : "");
            _fileErrors.Add(msg);

            if (string.IsNullOrEmpty(_batchErrorsPath))
                return;

            try
            {
                lock (_errorFileLock)
                {
                    File.AppendAllText(_batchErrorsPath, msg + Environment.NewLine);
                }
            }
            catch (IOException ioEx)
            {
                Debug.WriteLine($"Failed to append batch error log: {ioEx.Message}");
                _fileErrors.Add($"[REGISTERING ERRORS] Failed to append batch error log: {ioEx.Message}");
            }
            catch (UnauthorizedAccessException uaEx)
            {
                Debug.WriteLine($"Cannot write batch error log: {uaEx.Message}");
                _fileErrors.Add($"[REGISTERING ERRORS] Cannot write batch error log: {uaEx.Message}");
            }

            //if (!string.IsNullOrEmpty(_batchErrorsPath))
            //{
            //    File.AppendAllText(_batchErrorsPath, msg + Environment.NewLine);
            //}
        }

        private static HashSet<string> LoadExistingOutputNamesAndCleanupTmp(string outputFolder)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(outputFolder))
                return set;

            foreach (var file in Directory.EnumerateFiles(outputFolder, "*.*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(file);

                // 1) Чистим незавершённые временные файлы
                if (fileName.EndsWith(".tif.tmp", StringComparison.OrdinalIgnoreCase) ||
                    fileName.EndsWith(".tiff.tmp", StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(file); } catch { /* можно залогировать, если нужно */ }
                    continue;
                }

                // 2) Запоминаем уже готовые TIFF'ы
                var ext = Path.GetExtension(file);
                if (ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                {
                    var baseName = Path.GetFileNameWithoutExtension(file);
                    set.Add(baseName);
                }
            }

            return set;
        }



        public void Dispose()
        {

            if (_disposed) return;
            _disposed = true;

            try { _tokenRegistration.Dispose(); } catch { }
            try { _filesQueue?.Dispose(); } catch { }
            try { _saveQueue?.Dispose(); } catch { }
        }

        private async Task EnqueueFiles()
        {
            try
            {
                if (overWriteExistingOutputs)
                {
                    // clear output folder from all files
                    foreach (var file in Directory.EnumerateFiles(_outputFolder, "*.*", SearchOption.TopDirectoryOnly))
                    {
                        _token.ThrowIfCancellationRequested();
                        try { File.Delete(file); } catch { /* log if needed */ }
                    }


                }
                foreach (var file in _sourceFolder.Files)
                {
                    _token.ThrowIfCancellationRequested();
                    var baseName = Path.GetFileNameWithoutExtension(file.Path);
                    if (!overWriteExistingOutputs && _existingOutputNames.Contains(baseName))
                    {
                        // skip existing output
                        _processedCount++;
                        continue;
                    }
                    _filesQueue.Add(file, _token);
                    await Task.Yield();
                }
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("EnqueueFiles cancelled by token");
#endif
            }
            catch (Exception ex)
            {
                RegisterFileError("<enqueueing>", "Error enqueuing files.", ex);
            }
            finally
            {
                try { _filesQueue.CompleteAdding(); } catch { }
            }

        }

        private void StartSavingWorkerIfNeeded()
        {
            lock (_savingLock)
            {
                if (_currentSavingWorkers >= _maxSavingWorkers)
                    return;

                _currentSavingWorkers++;



                var workerTask = Task.Run(async () =>
                {
                    try
                    {
                        await ImageSavingWorkerAsync().ConfigureAwait(false);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _currentSavingWorkers);
                    }
                }, CancellationToken.None);

                _savingTasks.Add(workerTask);

                workerTask.ContinueWith(t =>
                {
                    RegisterFileError("<ImageSavingWorker>", "Saving worker faulted.", t.Exception!);
                }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        private async Task ImageSavingWorkerAsync()
        {
            var token = _token;
            using var fileProc = new FileProcessor(CancellationToken.None);
            string currentOutputFile = null;
            try
            {
                foreach (var saveTask in _saveQueue.GetConsumingEnumerable())
                {
                    //token.ThrowIfCancellationRequested();
                    //using (var stream = saveTask.ImageStream)
                    //{
                    //    if (stream.CanSeek) stream.Position = 0;
                    //    var finalPath = saveTask.OutputFilePath;
                    //    var tempPath = finalPath + ".tmp";

                    //    await Task.Run(() => fileProc.SaveTiff(stream, tempPath, TiffCompression.CCITTG4, 300, true, _plJson), token);
                    //    if (File.Exists(finalPath))
                    //        File.Delete(finalPath);
                    //    File.Move(tempPath, finalPath);
                    //}
                    var tiffInfo = saveTask.TiffInfo;
                    if (tiffInfo == null)
                        throw new InvalidOperationException("SaveTaskInfo contains neither TiffInfo nor ImageStream.");


                    var finalPath = saveTask.OutputFilePath;
                    var tempPath = finalPath + ".tmp";
                    currentOutputFile = finalPath;
                    fileProc.SaveTiff(tiffInfo, tempPath, true, _plJson);
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    File.Move(tempPath, finalPath);
                }
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("ImageSaverWorker cancelled!");
#endif
                throw;

            }
            catch (Exception ex)
            {
                RegisterFileError(currentOutputFile ?? "<unknown>", "Error in ImageSavingWorker.", ex);
            }

        }

        private void ImageProcessingWorker()
        {
            var token = _token;

            using var imgProc = new OpenCvImageProcessor(null, token);
            using var fileProc = new FileProcessor(token);
            try
            {
                foreach (var file in _filesQueue.GetConsumingEnumerable(token))
                {
                    token.ThrowIfCancellationRequested();
                    (ImageSource, byte[]) loaded;
                    try
                    {
                        loaded = fileProc.LoadImageSource(file.Path);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        RegisterFileError(file.Path, $"Error loading image: {file.Path}", ex);
                        continue;
                    }

                    if (loaded.Item1 == null)
                    {
                        // если здесь токен отменён — просто выходим/продолжаем без ошибки
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        RegisterFileError(file.Path, "Load failed: FileProcessor.Load returned null.");
                        continue;
                    }

                    imgProc.CurrentImage = loaded.Item1;
                    //imgProc.CurrentImage = fileProc.Load<ImageSource>(filePath).Item1;

                    foreach (var op in _plOperations)
                    {
                        //if (!op.InPipeline)
                        //    continue; // пользователь снял галочку — пропускаем

                        token.ThrowIfCancellationRequested();

                        try
                        {
                            if (op.Command == null)
                            {
                                continue;
                            }
                            //var parameters = op.CreateParameterDictionary();
                            // if command is Border remove and border removal algo is Manual, check if the file layout left or right
                            if (op.Command == ProcessorCommand.BordersRemove &&
                                op.Params.ContainsKey("borderRemovalAlgorithm") &&
                                op.Params["borderRemovalAlgorithm"] is string algo &&
                                algo == "Manual")
                            {

                                bool applyToLeftPage = (op.Params.ContainsKey("applyToLeftPage") &&
                                                        op.Params["applyToLeftPage"] is bool applyLeft &&
                                                        applyLeft);
                                bool applyToRightPage = (op.Params.ContainsKey("applyToRightPage") &&
                                                        op.Params["applyToRightPage"] is bool applyRight &&
                                                        applyRight);
                                if (file.Layout == SourceFileLayout.Left && !applyToLeftPage)
                                {
                                    continue;
                                }
                                if (file.Layout == SourceFileLayout.Right && !applyToRightPage)
                                {
                                    continue;
                                }
                                if (file.Layout == null)
                                {
                                    continue;
                                }

                            }

                            imgProc.ApplyCommand
                                (op.Command,
                                op.Params,
                                batchProcessing: true,
                                currentFilePath: file.Path,
                                log: msg => RegisterFileError(file.Path, msg)
                                );

                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exOp)
                        {
                            RegisterFileError(file.Path, $"Error applying op {op.Command}", exOp);
                            break;
                            //ErrorOccured?.Invoke($"Error applying op {op.command} to {filePath}: {exOp.Message}");
                        }
                    }

                    token.ThrowIfCancellationRequested();



                    var fileName = Path.ChangeExtension(Path.GetFileName(file.Path), ".tif");
                    var outputFilePath = Path.Combine(_outputFolder, fileName);
                    //proc.SaveCurrentImage(outputFilePath);
                    try
                    {
                        //using (var outStream = imgProc.GetStreamForSaving(ImageFormat.Tiff, TiffCompression.CCITTG4))
                        //{

                        //    if (outStream.CanSeek) outStream.Position = 0;
                        //    outStream.CopyTo(saveTask.ImageStream);
                        //    _saveQueue.Add(saveTask, token);
                        //    if (_saveQueue.Count >= 2)
                        //    {
                        //        StartSavingWorkerIfNeeded();
                        //    }

                        //}
                        //var outStream = imgProc.GetStreamForSaving(ImageFormat.Tiff, TiffCompression.CCITTG4);
                        var tiffInfo = imgProc.GetTiffInfo(TiffCompression.CCITTG4, 300);
                        //if (outStream.CanSeek) outStream.Position = 0;
                        var saveTask = new SaveTaskInfo
                        {
                            //ImageStream = outStream,
                            OutputFilePath = outputFilePath,
                            TiffInfo = tiffInfo,
                            //DisposeStream = true
                        };
                        _saveQueue.Add(saveTask);
                        if (_saveQueue.Count >= 2)
                        {
                            StartSavingWorkerIfNeeded();
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception exSave)
                    {
                        RegisterFileError(file.Path, "Error saving processed image.", exSave);
                        continue;
                    }
                    Interlocked.Increment(ref _processedCount);

                }
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                string logMsg = "Worker cancelled by token.";
                Debug.WriteLine(logMsg);
#endif
                
            }
            catch (Exception ex)
            {
                string logMsg = $"Error in ImageProcessingWorker: {ex.Message}";
                RegisterFileError("<Image processing worker>", logMsg, ex);
#if DEBUG
                Debug.WriteLine(logMsg);
#endif
            }

        }

        private void SaveProcessingLog(string logMsg, string timeMsg, string plOps, string errors, bool cancelled)
        {
            if (cancelled)
                logMsg = "[Processing cancelled]. " + logMsg;
            {
                File.WriteAllLines(
                    Path.Combine(_outputFolder, "_processing_log.txt"),
                    new string[] { logMsg, timeMsg, "Operations performed:", plOps, "\n", "Errors:", "\n", errors, "\n", _plJson ?? "Error get PL json" }
                );
            }
        }

        public async Task RunAsync()
        {
            var startTime = DateTime.Now;
            var processingTasks = new List<Task>();
            bool cancelled = false;
            try
            {
                //if _plOperations contains SmartCrop _workersCount--
                if (_plOperations.Any(op => op.Command == ProcessorCommand.SmartCrop))
                {
                    _workersCount = Math.Max(1, _workersCount - 1);
                }
                for (int i = 0; i < _workersCount; i++)
                {
                    if (_cts.IsCancellationRequested) throw new OperationCanceledException();
                    processingTasks.Add(Task.Run(() => ImageProcessingWorker(), _token));
                }

                // in parallel enqueing que
                var enqueueTask = Task.Run(() => EnqueueFiles(), _token);
                processingTasks.Add(enqueueTask);
                StartSavingWorkerIfNeeded();
                await Task.WhenAll(processingTasks);
                if (_token.IsCancellationRequested)
                {
                    cancelled = true;
                }

                var duration = DateTime.Now - startTime;
                var durationHours = (int)duration.TotalHours;
                var durationMinutes = duration.Minutes;
                var durationSeconds = duration.Seconds;
                var logMsg = $"Processed {_processedCount} of {_totalCount} files from ** {_sourceFolder.Path} **.";
                var timeMsg = $"Completed in {durationHours} hours, {durationMinutes} minutes, {durationSeconds} seconds.";

                var plOps = _opsLog.Count > 0 ? string.Join(Environment.NewLine, _opsLog) : "No operations were performed.";
                var errors = _fileErrors.Count > 0
                    ? string.Join(Environment.NewLine, _fileErrors)
                    : "No errors.";
                var plJsonMsg = _plJson;
                SaveProcessingLog(logMsg, timeMsg, plOps, errors, cancelled);
                if (_fileErrors.IsEmpty && File.Exists(_batchErrorsPath))
                {
                    File.Delete(_batchErrorsPath);
                }
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Batch processing cancelled!");

#endif
            }
            catch (Exception ex)
            {
                var duration = DateTime.Now - startTime;
                var durationHours = (int)duration.TotalHours;
                var durationMinutes = duration.Minutes;
                var durationSeconds = duration.Seconds;
                var logMsg = $"[ERROR OCCURED: {ex.Message}]. Processed {_processedCount} of {_totalCount} files from ** {_sourceFolder.Path} **.";
                var timeMsg = $"Completed in {durationHours} hours, {durationMinutes} minutes, {durationSeconds} seconds.";

                var plOps = _opsLog.Count > 0 ? string.Join(Environment.NewLine, _opsLog) : "No operations were performed.";
                var errors = _fileErrors.Count > 0
                    ? string.Join(Environment.NewLine, _fileErrors)
                    : "No errors.";
                var plJsonMsg = _plJson;
                SaveProcessingLog(logMsg, timeMsg, plOps, errors, false);
                if (_fileErrors.IsEmpty && File.Exists(_batchErrorsPath))
                {
                    File.Delete(_batchErrorsPath);
                }
                RegisterFileError("<ImgWorkerPool>", "Error in Batch processing.", ex);
            }
            finally
            {
                _saveQueue.CompleteAdding();
                await Task.WhenAll(_savingTasks);
                try { _tokenRegistration.Dispose(); } catch { }
                try { _filesQueue.Dispose(); } catch { }

            }



        }
    }
}
