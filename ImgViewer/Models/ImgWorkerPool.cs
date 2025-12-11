using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Models
{
    internal class ImgWorkerPool : IDisposable
    {

        private sealed class SaveTaskInfo
        {
            public MemoryStream ImageStream { get; set; }
            public string OutputFilePath;
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


        private int _processedCount;
        private readonly int _totalCount;
        private (ProcessorCommand Command, Dictionary<string, object> Params)[] _plOperations;
        private readonly string? _plJson;
        private List<string> _opsLog = new List<string>();

        public event Action<string>? ErrorOccured;
        public event Action<int, int>? ProgressChanged;

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
            //_pipelineTemplate = pipelineTemplate ?? Array.Empty<(ProcessorCommand, Dictionary<string, object>)>();
            var opsSnapshot = pipeline.Operations
                .Where(op => op.InPipeline)
                .Select(op => (op.Command, Params: op.CreateParameterDictionary()))
                .ToArray();
            _plOperations = opsSnapshot;
            _plJson = savePipelineToMd ? pipeline.BuildPipelineForSave() : null;

            int cpuCount = Environment.ProcessorCount;
            _workersCount = maxWorkersCount == 0 ? Math.Max(1, cpuCount-1) : maxWorkersCount;

            _filesQueue = new BlockingCollection<SourceImageFile>(
                maxFilesQueue == 0 ? _workersCount * 2 : maxFilesQueue
            );
            _saveQueue = new BlockingCollection<SaveTaskInfo>( _workersCount );
            _maxSavingWorkers = Math.Max(1, cpuCount/2);

            _tokenRegistration = _token.Register(() =>
            {
                try { _filesQueue.CompleteAdding(); } catch { }
                try { _saveQueue.CompleteAdding(); } catch { }
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
                foreach (var file in _sourceFolder.Files)
                {
                    _token.ThrowIfCancellationRequested();

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

                var task = Task.Run(() =>
                {
                    try
                    {
                        ImageSavingWorker(); 
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _currentSavingWorkers);
                    }
                }, _token);

                _savingTasks.Add(task);
            }
        }

        private void ImageSavingWorker()
        {
            var token = _token;
            using var fileProc = new FileProcessor(token);
            string currentOutputFile = null;
            try
            {
                foreach (var saveTask in _saveQueue.GetConsumingEnumerable(token))
                {
                    token.ThrowIfCancellationRequested();
                    currentOutputFile = saveTask.OutputFilePath;
                    using (var ms = saveTask.ImageStream)
                    {
                        ms.Position = 0;
                        var finalPath = saveTask.OutputFilePath;
                        var tempPath = finalPath + ".tmp";
                        fileProc.SaveTiff(ms, tempPath, TiffCompression.CCITTG4, 300, true, _plJson);

                        // Если старый финальный файл уже есть – удаляем
                        if (File.Exists(finalPath))
                            File.Delete(finalPath);
                        File.Move(tempPath, finalPath);

                    }
                }
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("ImageSaverWorker cancelled!");
#endif
                return;

            }
            catch (Exception ex)
            {
                RegisterFileError(currentOutputFile ?? "<unknown>", "Error in ImageSavingWorker.", ex);
            }

        }

        private void ImageProcessingWorker()
        {
            var token = _token;
               
            using var imgProc = new OpenCVImageProcessor(null, token);
            using var fileProc = new FileProcessor(token);
            try
            {
                foreach (var file in _filesQueue.GetConsumingEnumerable(token))
                {
                    token.ThrowIfCancellationRequested();

                    var loaded = fileProc.Load<ImageSource>(file.Path);

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
                            // if commant is Border remove and border removal algo is Manual, check if the file layout left or right
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
                        using (var outStream = imgProc.GetStreamForSaving(ImageFormat.Tiff, TiffCompression.CCITTG4))
                        {
                            var saveTask = new SaveTaskInfo
                            {
                                ImageStream = new MemoryStream(),
                                OutputFilePath = outputFilePath
                            };
                            if (outStream.CanSeek) outStream.Position = 0;
                            outStream.CopyTo(saveTask.ImageStream);
                            _saveQueue.Add(saveTask, token);
                            if (_saveQueue.Count >= 2)
                            {
                                StartSavingWorkerIfNeeded();
                            }

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
                return;

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

        public async Task RunAsync()
        {
            var startTime = DateTime.Now;
            var processingTasks = new List<Task>();
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
                _saveQueue.CompleteAdding();
                await Task.WhenAll(_savingTasks);

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
                File.WriteAllLines(
                    Path.Combine(_outputFolder, "_processing_log.txt"),
                    new string[] { logMsg, timeMsg, "Operations performed:", plOps, "\n", "Errors:", "\n", errors, "\n", _plJson ?? "Error get PL json" }
                );

            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Enqueing cancelled!");
#endif
            }
            catch (Exception ex)
            {
                RegisterFileError("<ImgWorkerPool>", "Error in Batch processing.", ex);
            }
            finally
            {
                try { _tokenRegistration.Dispose(); } catch { }
                try { _filesQueue.Dispose(); } catch { }

            }
            


        }
    }
}
