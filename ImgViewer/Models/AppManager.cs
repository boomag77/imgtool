using BitMiracle.LibTiff.Classic;
using ImgViewer.Interfaces;
using ImgViewer.Models.Onnx;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class AppManager : IAppManager, IDisposable
    {

        private readonly IViewModel _mainViewModel;
        private readonly IFileProcessor _fileProcessor;
        private readonly IImageProcessor _imageProcessor;
        private readonly AppSettings _appSettings;
        private readonly Pipeline _pipeline;

        private readonly List<string> _uiErrors = new();
        private readonly object _uiErrorsLock = new();

        private readonly SemaphoreSlim _currentImageLock = new(1, 1);
        private readonly SemaphoreSlim _imageLoadLock = new(1, 1);

        private readonly CancellationTokenSource _cts;
        private CancellationTokenSource? _poolCts;
        private CancellationTokenSource? _rootFolderCts;
        private CancellationTokenSource _imgProcCts;

        //private DocBoundaryModel _docBoundaryModel;

        public Pipeline CurrentPipeline => _pipeline;

        public AppManager(IMainView mainView, CancellationTokenSource cts)
        {
            _cts = cts;
            _appSettings = new AppSettings();
            _pipeline = new Pipeline(this);
            _mainViewModel = new MainViewModel(this);
            mainView.ViewModel = _mainViewModel;
            _fileProcessor = new FileProcessor(_cts.Token);

            _fileProcessor.ErrorOccured += (msg) => ReportError(msg, null, "File Processor Error");

            _imgProcCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _imageProcessor = new OpenCvImageProcessor(this, _imgProcCts.Token);

            _imageProcessor.ErrorOccured += (msg) => ReportError(msg, null, "Image Processor Error");
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
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private async Task SetBmpImageAsOriginal(ImageSource bmp)
        {

            UpdateStatus("Setting original image on preview...");

            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.OriginalImage = bmp;
            }, System.Windows.Threading.DispatcherPriority.Render);
            UpdateStatus("Standby");
        }

        public async Task SetImageOnPreview(string imagePath)
        {
            await _imageLoadLock.WaitAsync();
            try
            {
                
                var (bmpImage, bytes) = await Task.Run(() => _fileProcessor.LoadImageSource(imagePath));
                if (bmpImage is BitmapSource bmpSource && !bmpSource.IsFrozen)
                    bmpSource.Freeze(); 
                
                _mainViewModel.CurrentImagePath = imagePath;
                await SetBmpImageAsOriginal(bmpImage);
                await SetBmpImageOnPreview(bmpImage);
                await SetImageForProcessing(bmpImage);
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

        public async Task ProcessRootFolder(string rootFolder, Pipeline pipeline, bool fullTree = true)
        {
            _rootFolderCts?.Cancel();
            _rootFolderCts?.Dispose();

            _rootFolderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var batchToken = _rootFolderCts.Token;

            var debug = false;
            if (pipeline == null) return;

            var startTime = DateTime.Now;   

            UpdateStatus($"Processing folders in " + rootFolder);
            //_mainViewModel.Status = $"Processing folders in " + rootFolder;

            SourceImageFolder[] sourceFolders = Array.Empty<SourceImageFolder>();

            if (fullTree)
            {
                sourceFolders = _fileProcessor.GetSubFoldersWithImagesPaths_FullTree(rootFolder, batchToken);
                //if (debug)
                //{
                //    Debug.WriteLine("Folders to process (FULL):");
                //    foreach (var folder in sourceFolders)
                //    {
                //        Debug.WriteLine(folder.Path);
                //    }
                //    return;
                //}

                if (sourceFolders == null) return;
            }
            else
            {
                sourceFolders = _fileProcessor.GetSubFoldersWithImagesPaths(rootFolder, batchToken);
                if (debug)
                {
                    Debug.WriteLine("Folders to process:");
                    foreach (var folder in sourceFolders)
                    {
                        Debug.WriteLine(folder.Path);
                    }
                    return;
                }
                if (sourceFolders == null) return;

            }

            if (sourceFolders.Length == 0) return;

            var processedCount = 0;


            //_rootFolderCts = new CancellationTokenSource();
            try
            {
                foreach (var sourceFolder in sourceFolders)
                {
                    if (batchToken.IsCancellationRequested) break;
                    try
                    {
                        await Task.Run(() => ProcessFolder(sourceFolder.Path, pipeline, batchToken), batchToken);
                        processedCount++;
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("Processing Root Folder was canceled.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        //Debug.WriteLine($"Error while processing sub-Folder in root Folder {rootFolder}: {ex.Message}");
                        ReportError($"Error while processing sub-Folder in root Folder {rootFolder}.", ex, "Processing Error");
                    }
                }


                var duration = DateTime.Now - startTime;
                var durationHours = (int)duration.TotalHours;
                var durationMinutes = duration.Minutes;
                var durationSeconds = duration.Seconds;
                var logMsg = $"Processed {processedCount} of {sourceFolders.Length} folders from ** {rootFolder} **.";
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
                    File.WriteAllLines(
                        Path.Combine(rootFolder, "_processing_log.txt"),
                        new string[] { logMsg, timeMsg, "Operations performed:", plOps, "\n", plJson }
                    );
                }
                catch (Exception ex)
                {
                    ReportError($"Failed to write processing log to {rootFolder}", ex, "Logging Error");
                }

            }
            finally
            {
                UpdateStatus("Standby");
                try { _rootFolderCts?.Dispose(); } catch { }
                _rootFolderCts = null;
            }
        }

        public Task ProcessFolder(string srcFolder, Pipeline pipeline)
        {
            _rootFolderCts?.Cancel();
            _rootFolderCts?.Dispose();
            _rootFolderCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

            return ProcessFolder(srcFolder, pipeline, _rootFolderCts.Token);
        }


        public async Task ProcessFolder(string srcFolder, Pipeline pipeline, CancellationToken batchToken)
        {
            bool debug = false;
            if (pipeline == null) return;

            UpdateStatus($"Processing folder " + srcFolder);

            
            var sourceFolder = _fileProcessor.GetImageFilesPaths(srcFolder, batchToken);
            if (sourceFolder == null || sourceFolder.Files == null || sourceFolder.Files.Length == 0) return;


            _poolCts?.Cancel();
            _poolCts?.Dispose();
            _poolCts = CancellationTokenSource.CreateLinkedTokenSource(batchToken);

            if (debug)
            {
                foreach (var imagePath in sourceFolder.Files)
                {
                    Debug.WriteLine(imagePath);
                }
            }

            //_poolCts = new CancellationTokenSource();

            var startTime = DateTime.Now;

            try
            {
                using (var workerPool = new ImgWorkerPool(_poolCts, pipeline, 0, sourceFolder, 0, IsSavePipelineToMd))
                {
                    try
                    {
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
