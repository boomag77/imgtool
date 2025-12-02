using ImgViewer.Interfaces;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace ImgViewer.Models
{
    internal class AppManager : IAppManager, IDisposable
    {

        private readonly IViewModel _mainViewModel;
        private readonly IFileProcessor _fileProcessor;
        private readonly IImageProcessor _imageProcessor;
        private readonly AppSettings _appSettings;
        private readonly Pipeline _pipeline;


        private readonly CancellationTokenSource _cts;
        private CancellationTokenSource? _poolCts;
        private CancellationTokenSource? _rootFolderCts;
        private CancellationTokenSource _imgProcCts;

        public AppManager(IMainView mainView, CancellationTokenSource cts)
        {
            _cts = cts;
            _appSettings = new AppSettings();
            _mainViewModel = new MainViewModel(this);
            mainView.ViewModel = _mainViewModel;
            _fileProcessor = new FileProcessor(_cts.Token);

            _imgProcCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            _imageProcessor = new OpenCVImageProcessor(this, _imgProcCts.Token);

        }

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
            Dispose();
        }

        public void UpdateStatus(string status)
        {
            _mainViewModel.Status = status;
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
            await Task.Run(() => _imageProcessor.CurrentImage = bmp);
           
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
            _mainViewModel.Status = "Loading image...";
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.OriginalImage = bmp;
            }, System.Windows.Threading.DispatcherPriority.Render);
            _mainViewModel.Status = "Standby";
        }

        public async Task SetImageOnPreview(string imagePath)
        {
            _mainViewModel.CurrentImagePath = imagePath;
            var (bmpImage, bytes) = await Task.Run(() => _fileProcessor.Load<ImageSource>(imagePath));
            await SetBmpImageAsOriginal(bmpImage);
            await SetBmpImageOnPreview(bmpImage);
            await SetImageForProcessing(bmpImage);



        }




        public async Task ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            if (_mainViewModel.OriginalImage == null) return;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _mainViewModel.Status = $"Processing image ({command})...";
            });

            await Task.Run(() => _imageProcessor.ApplyCommand(command, parameters));

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _mainViewModel.Status = "Standby";
            });
        }

        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression, string imageDescription = null)
        {
            var stream = _imageProcessor.GetStreamForSaving(ImageFormat.Tiff, compression);
            Debug.WriteLine($"Stream length: {stream.Length}");

            // test JSON
            string json = "{\"pipeline\":\"Deskew+Binarize\",\"version\":1}";

            _fileProcessor.SaveTiff(stream, outputPath, compression, 300, true, json);
        }

        public void SavePipelineToJSON(string path, string json)
        {
            // TODO async

            var folder = System.IO.Path.GetDirectoryName(path);
            string pipeLineForSave = json;
            string fileName = System.IO.Path.GetFileName(path);
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(folder, fileName), pipeLineForSave);
#if DEBUG
                Debug.WriteLine("Pipeline saved to " + fileName);
#endif
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(ex.Message, "Error!", MessageBoxButton.OK, MessageBoxImage.Error);
            }

        }

        public async Task ProcessRootFolder(string rootFolder, Pipeline pipeline, bool fullTree = true)
        {
            var debug = false;
            if (pipeline == null) return;
            _mainViewModel.Status = $"Processing folders in " + rootFolder;

            SourceImageFolder[] sourceFolders = [];

            if (fullTree)
            {
                sourceFolders = _fileProcessor.GetSubFoldersWithImagesPaths_FullTree(rootFolder);
                if (debug)
                {
                    Debug.WriteLine("Folders to process (FULL):");
                    foreach (var folder in sourceFolders)
                    {
                        Debug.WriteLine(folder.Path);
                    }
                    return;
                }

                if (sourceFolders == null) return;
            }
            else
            {
                sourceFolders = _fileProcessor.GetSubFoldersWithImagesPaths(rootFolder);
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

            if (sourceFolders == null || sourceFolders.Length == 0) return;

            if (_rootFolderCts != null)
            {
                try { _rootFolderCts.Cancel(); } catch { }
                try { _rootFolderCts.Dispose(); } catch { }
                _rootFolderCts = null;
            }
            _rootFolderCts = new CancellationTokenSource();
            try
            {
                foreach (var sourceFolder in sourceFolders)
                {
                    _rootFolderCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        await ProcessFolder(sourceFolder.Path, pipeline);
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("Processing Root Folder was canceled.");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Error while processing sub-Folder in root Folder {rootFolder}: {ex.Message}");
                    }
                }
            }
            finally
            {
                _mainViewModel.Status = "Standby";
                try { _rootFolderCts?.Dispose(); } catch { }
                _rootFolderCts = null;
            }
        }

        public async Task ProcessFolder(string srcFolder, Pipeline pipeline)
        {
            bool debug = false;
            if (pipeline == null) return;

            _mainViewModel.Status = $"Processing folder " + srcFolder;
            var sourceFolder = _fileProcessor.GetImageFilesPaths(srcFolder);
            //if (debug)
            //{
            //    string pipeLineForSave = BuildPipelineForSave(pipelineToUse);
            //    Debug.WriteLine("Pipeline JSON for save:");
            //    Debug.WriteLine(pipeLineForSave);
            //    return;
            //}
            if (debug)
            {
                foreach (var imagePath in sourceFolder.Files)
                {
                    Debug.WriteLine(imagePath);
                }
            }

            if (_poolCts != null)
            {
                try { _poolCts.Cancel(); } catch { }
                try { _poolCts.Dispose(); } catch { }
                _poolCts = null;
            }
            _poolCts = new CancellationTokenSource();

            try
            {
                using (var workerPool = new ImgWorkerPool(_poolCts, pipeline, 0, sourceFolder, 0, IsSavePipelineToMd))
                {
                    try
                    {
                        await workerPool.RunAsync().ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception)
                    {
                        // TODO Error handling
                    }

                }
            }
            finally
            {
                _mainViewModel.Status = $"Standby";
                try
                {
                    _poolCts?.Dispose();
                }
                catch { }
                _poolCts = null;
            }
        }

        public void CancelBatchProcessing()
        {
            try
            {
                _rootFolderCts?.Cancel();
            }
            catch (Exception e)
            {

            }

            try
            {
                _poolCts?.Cancel();
            }
            catch (Exception e)
            {

            }
        }



        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }



    }
}