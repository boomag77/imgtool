using ImgViewer.Interfaces;
using OpenCvSharp;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Buffers;


namespace ImgViewer.Models
{
    internal class ImgWorkerPool : IDisposable
    {

        private sealed class SaveTaskInfo
        {
            public TiffInfo? TiffInfo { get; set; }
            public string OutputFilePath { get; set; } = string.Empty;

            
        }

        private bool _disposed;

        private readonly ConcurrentBag<string> _fileErrors = new();

        private readonly BlockingCollection<SourceImageFile> _filesQueue;
        private readonly BlockingCollection<SaveTaskInfo> _saveQueue;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _tokenRegistration;
        //private readonly SourceImageFolder _sourceFolder;
        private string _outputFolder = string.Empty;
        private int _workersCount;

        private readonly int _maxSavingWorkers;
        private int _currentSavingWorkers = 0;

        private readonly object _savingLock = new();
        private readonly List<Task> _savingTasks = new();
        private readonly HashSet<string> _existingOutputNames;

        private bool overWriteExistingOutputs = true;

        private string _sourceFolderPath = string.Empty;

        private int _processedCount;
        private int _totalCount;
        private (ProcessorCommand Command, Dictionary<string, object> Params)[] _plOperations;
        private readonly string? _plJson;
        private List<string> _opsLog = new List<string>();

        public event Action<string>? ErrorOccured;
        public event Action<int, int>? ProgressChanged;

        private string _batchErrorsPath = string.Empty;
        private readonly object _errorFileLock = new();

        private readonly bool _boundaryModelRequested = false;
        private readonly bool _isSplitPipeline;

        private void ReportProgress(int processed)
        {
            ProgressChanged?.Invoke(processed, _totalCount);
        }

        public ImgWorkerPool(CancellationTokenSource cts,
                             Pipeline pipeline,
                             int maxWorkersCount,
                             string sourceFolderPath,
                             int maxFilesQueue,
                             bool savePipelineToMd)
        {
            _cts = cts;
            _token = _cts.Token;
            //_sourceFolder = sourceFolder;
            _sourceFolderPath = sourceFolderPath;
            string sourceFolderName = Path.GetFileName(_sourceFolderPath);
            string parentPath = Path.GetDirectoryName(_sourceFolderPath);
            _totalCount = Task.Run(() => CountImages(sourceFolderPath), _token).Result;

            if (pipeline.Operations.Any(op => op.InPipeline && op.Command == ProcessorCommand.SmartCrop))
            {
                _boundaryModelRequested = true;
            }

            _isSplitPipeline = pipeline.Operations.Any(op => op.InPipeline && op.Command == ProcessorCommand.PageSplit);

            if (_isSplitPipeline)
            {
                _outputFolder = Path.Combine(parentPath, sourceFolderName + "_splitted");
            }
            else
            {
                if (pipeline.Name == "Full pipeline")
                {
                    _outputFolder = Path.Combine(parentPath, sourceFolderName + "_processed");
                }
                else
                {
                    _outputFolder = Path.Combine(parentPath, sourceFolderName + "_" + pipeline.Name);
                }
                //_outputFolder = Path.Combine(parentPath, sourceFolderName + "_processed");
                //_outputFolder = Path.Combine(parentPath, sourceFolderName + "_" + pipeline.Name);
            }

                Directory.CreateDirectory(_outputFolder);
            _batchErrorsPath = Path.Combine(_outputFolder, "_batch_errors.txt");

            _existingOutputNames = LoadExistingOutputNamesAndCleanupTmp(_outputFolder);

            if (_existingOutputNames.Count > 0)
            {
                // inform user that there are existing files and ask for overwrite or skip existing files
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
            int saveQueueCapacity = Math.Max(1, _workersCount/2);
            _saveQueue = new BlockingCollection<SaveTaskInfo>(saveQueueCapacity);
            
            _maxSavingWorkers = 3;
            _tokenRegistration = _token.Register(() =>
            {
                try { _filesQueue.CompleteAdding(); } catch { }
                try { _saveQueue.CompleteAdding(); } catch { }
            });
            _processedCount = 0;
            //_totalCount = sourceFolder.Files.Length;
            foreach (var op in opsSnapshot)
            {
                _opsLog.Add($"- {op.Command}");
            }
        }

        private int CountImages(string folder)
        {
            int count = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(path);
                if (ext.Length == 0) continue;

                // сравниваем расширения
                if (ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                    ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
                {
                    count++;
                }
            }
            return count;
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

        private bool TrySaveSplitOutputs(OpenCvImageProcessor imgProc, SourceImageFile file)
        {
            var splitResults = imgProc.GetSplitResults();
            if (splitResults == null || splitResults.Length == 0)
                return false;

            try
            {
                SaveSplitResults(splitResults, file);
                return true;
            }
            finally
            {
                foreach (var mat in splitResults)
                {
                    mat?.Dispose();
                }
                imgProc.ClearSplitResults();
            }
        }

        private void SaveSplitResults(Mat[] splitResults, SourceImageFile file)
        {
            if (splitResults == null || splitResults.Length == 0)
                return;

            var originalExtension = Path.GetExtension(file.Path);
            if (string.IsNullOrWhiteSpace(originalExtension))
                originalExtension = ".tif";

            var encodeExt = NormalizeEncodeExtension(originalExtension);
            var baseName = Path.GetFileNameWithoutExtension(file.Path);

            for (int i = 0; i < splitResults.Length; i++)
            {
                var mat = splitResults[i];
                if (mat == null || mat.IsDisposed)
                    continue;
                if (mat.Empty())
                    throw new InvalidOperationException("Split image is empty.");

                var finalPath = Path.Combine(_outputFolder, $"{baseName}_{i + 1}{originalExtension}");
                SaveMatToFile(mat, encodeExt, finalPath);
            }
        }

        private void RenumberSplitOutputs()
        {
            if (!_isSplitPipeline)
                return;
            if (!Directory.Exists(_outputFolder))
                return;

            var files = Directory.EnumerateFiles(_outputFolder, "*.*", SearchOption.TopDirectoryOnly)
                                 .Where(path =>
                                 {
                                     var name = Path.GetFileName(path);
                                     return !string.IsNullOrWhiteSpace(name) && !name.StartsWith("_", StringComparison.OrdinalIgnoreCase);
                                 })
                                 .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                                 .ToList();

            if (files.Count == 0)
                return;

            var pendingMoves = new List<(string Temp, string Final)>(files.Count);
            int index = 1;
            foreach (var file in files)
            {
                var extension = Path.GetExtension(file);
                var tempPath = file + ".renaming";
                File.Move(file, tempPath);
                var finalPath = Path.Combine(_outputFolder, index.ToString("D3") + extension);
                pendingMoves.Add((tempPath, finalPath));
                index++;
            }

            foreach (var move in pendingMoves)
            {
                if (File.Exists(move.Final))
                    File.Delete(move.Final);
                File.Move(move.Temp, move.Final);
            }
        }

        private static void SaveMatToFile(Mat mat, string encodeExt, string finalPath)
        {
            if (mat == null)
                throw new ArgumentNullException(nameof(mat));
            if (mat.IsDisposed)
                throw new ObjectDisposedException(nameof(mat));
            if (mat.Empty())
                throw new InvalidOperationException("Split image is empty.");

            Cv2.ImEncode(encodeExt, mat, out var buffer);
            var tempPath = finalPath + ".tmp";
            File.WriteAllBytes(tempPath, buffer);
            if (File.Exists(finalPath))
                File.Delete(finalPath);
            File.Move(tempPath, finalPath);
        }

        private static string NormalizeEncodeExtension(string originalExtension)
        {
            if (string.IsNullOrWhiteSpace(originalExtension))
                return ".tif";

            var lower = originalExtension.ToLowerInvariant();
            return lower switch
            {
                ".jpg" => ".jpg",
                ".jpeg" => ".jpg",
                ".png" => ".png",
                ".bmp" => ".bmp",
                ".tif" => ".tif",
                ".tiff" => ".tif",
                ".webp" => ".webp",
                _ => ".tif"
            };
        }

        private static readonly HashSet<string> ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".tif", ".tiff"
        };

        public void Dispose()
        {

            if (_disposed) return;
            _disposed = true;

            try { _tokenRegistration.Dispose(); } catch { }
            try { _filesQueue?.Dispose(); } catch { }
            try { _saveQueue?.Dispose(); } catch { }
        }

        IEnumerable<string> EnumerateImages(string folder)
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var ext = Path.GetExtension(file);
                if (ImageExts.Contains(ext))
                    yield return file;
            }
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

                IEnumerable<string> files;
                try
                {
                    //files = Directory.EnumerateFiles(_sourceFolderPath)
                    //             .Where(file =>
                    //                        file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                    //                        file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    //                        file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                    //                        file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                    //                        file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase));
                    files = EnumerateImages(_sourceFolderPath);
                }
                catch (Exception ex)
                {
                    RegisterFileError(_sourceFolderPath, "Error enumerating files in source folder.", ex);
                    throw;
                }
                //_totalCount = files.Count();
                foreach (var file in files)
                {
                    _token.ThrowIfCancellationRequested();

                    var baseName = Path.GetFileNameWithoutExtension(file);
                    if (!overWriteExistingOutputs && _existingOutputNames.Contains(baseName))
                    {
                        // skip existing output
                        //_processedCount++;
                        int processedCount = Interlocked.Increment(ref _processedCount);
                        ReportProgress(processedCount);
                        continue;
                    }
                    var sourceFile = new SourceImageFile
                    {
                        Path = file,
                        Layout = GetLayoutFromFileName(file)
                    };
                    _filesQueue.Add(sourceFile, _token);


                    //await Task.Yield();
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
        private static SourceFileLayout? GetLayoutFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path); // e.g. "page001"

            if (string.IsNullOrEmpty(name))
                return SourceFileLayout.Right;

            int i = name.Length - 1;

            // идём с конца, пока цифры
            while (i >= 0 && char.IsDigit(name[i]))
                i--;

            int start = i + 1; // первая цифра в хвостовом числе
            string digits = (start < name.Length)
                ? name.Substring(start)    // всё от первой цифры до конца
                : string.Empty;

            if (digits.Length > 0 && int.TryParse(digits, out int num))
                return (num % 2 == 1) ? SourceFileLayout.Left : SourceFileLayout.Right;

            // нет числового суффикса → Right по умолчанию
            return null;
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
                }, _token);

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
            //var tiffInfo = new TiffInfo();
            try
            {
                foreach (var saveTask in _saveQueue.GetConsumingEnumerable())
                {
                    token.ThrowIfCancellationRequested();
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
                    var tempPath = string.Concat(finalPath, ".tmp");
                    currentOutputFile = finalPath;
                    fileProc.SaveTiff(tiffInfo, tempPath, true, _plJson);
                    if (File.Exists(finalPath))
                        File.Delete(finalPath);
                    File.Move(tempPath, finalPath);
                    tiffInfo = null;
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

            using var imgProc = new OpenCvImageProcessor(null, token, 1, _boundaryModelRequested);
            using var fileProc = new FileProcessor(token);
            try
            {
                foreach (var file in _filesQueue.GetConsumingEnumerable(token))
                {
                    token.ThrowIfCancellationRequested();
                    int len;
                    ArrayPool<byte> localPool;
                    (ImageSource?, byte[]?) loaded;
                    try
                    {
                        loaded = fileProc.LoadImageSource(file.Path, isBatch: true);
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

                    if (loaded.Item2 == null)
                    {
                        // если здесь токен отменён — просто выходим/продолжаем без ошибки
                        if (token.IsCancellationRequested) throw new OperationCanceledException(token);
                        RegisterFileError(file.Path, "Load failed: FileProcessor.Load returned null.");
                        continue;
                    }

                    ReadOnlyMemory<byte> imageBytes = loaded.Item2;
                    imgProc.CurrentImage = imageBytes;

                    //imgProc.CurrentImage = loaded.Item2;
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

                    bool handledSplit = false;
                    try
                    {
                        handledSplit = TrySaveSplitOutputs(imgProc, file);
                    }
                    catch (Exception splitEx)
                    {
                        RegisterFileError(file.Path, "Error saving split images.", splitEx);
                        continue;
                    }

                    if (handledSplit)
                    {
                        int processedCount = Interlocked.Increment(ref _processedCount);
                        ReportProgress(processedCount);
                        continue;
                    }

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
                        _saveQueue.Add(saveTask, _token);
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
                    int processedAfterSave = Interlocked.Increment(ref _processedCount);
                    ReportProgress(processedAfterSave);

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
                    _token.ThrowIfCancellationRequested();
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
                var logMsg = $"Processed {_processedCount} of {_totalCount} files from ** {_sourceFolderPath} **.";
                var timeMsg = $"Completed in {durationHours} hours, {durationMinutes} minutes, {durationSeconds} seconds.";

                var plOps = _opsLog.Count > 0 ? string.Join(Environment.NewLine, _opsLog) : "No operations were performed.";
                var errors = _fileErrors.Count > 0
                    ? string.Join(Environment.NewLine, _fileErrors)
                    : "No errors.";
                var plJsonMsg = _plJson;
                SaveProcessingLog(logMsg, timeMsg, plOps, errors, cancelled);
                if (_isSplitPipeline)
                {
                    try
                    {
                        RenumberSplitOutputs();
                    }
                    catch (Exception renameEx)
                    {
                        RegisterFileError("<Split rename>", "Failed to rename split outputs.", renameEx);
                    }
                }
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
                var logMsg = $"[ERROR OCCURED: {ex.Message}]. Processed {_processedCount} of {_totalCount} files from ** {_sourceFolderPath} **.";
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
                if (_isSplitPipeline)
                {
                    try
                    {
                        RenumberSplitOutputs();
                    }
                    catch (Exception renameEx)
                    {
                        RegisterFileError("<Split rename>", "Failed to rename split outputs.", renameEx);
                    }
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
