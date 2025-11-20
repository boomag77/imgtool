using ImgViewer.Interfaces;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text;
using System.Text.Json;
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


        private readonly CancellationTokenSource _cts;
        private CancellationTokenSource? _poolCts;
        private CancellationTokenSource? _rootFolderCts;

        public AppManager(IMainView mainView, CancellationTokenSource cts)
        {
            _cts = cts;
            _appSettings = new AppSettings();
            _mainViewModel = new MainViewModel(_appSettings);
            mainView.ViewModel = _mainViewModel;
            _fileProcessor = new FileProcessor(_cts.Token);
            _imageProcessor = new OpenCVImageProcessor(this, _cts.Token);

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

        public async Task SetImageForProcessing(ImageSource bmp)
        {

            _imageProcessor.CurrentImage = bmp;
        }

        public async Task SetBmpImageOnPreview(ImageSource bmp)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.ImageOnPreview = bmp;
                _mainViewModel.Status = $"Ready";
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        private async Task SetBmpImageAsOriginal(ImageSource bmp)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.OriginalImage = bmp;
                _mainViewModel.Status = $"Ready";
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        public async Task SetImageOnPreview(string imagePath)
        {
            _mainViewModel.CurrentImagePath = imagePath;
            _mainViewModel.Status = $"Loading image preview...";
            var (bmpImage, bytes) = await Task.Run(() => _fileProcessor.Load<ImageSource>(imagePath));
            await SetBmpImageAsOriginal(bmpImage);
            await SetBmpImageOnPreview(bmpImage);

            await SetImageForProcessing(bmpImage);
            _mainViewModel.Status = $"Standby";
        }


        public void ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            if (_mainViewModel.OriginalImage == null) return;
            _mainViewModel.Status = $"Processing image ({command})";
            Debug.WriteLine(command.ToString());

            _imageProcessor.ApplyCommand(command, parameters);
            _mainViewModel.Status = $"Standby";
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

        public async Task ProcessRootFolder(string rootFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline, bool fullTree = true)
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
                    foreach(var folder in sourceFolders)
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

        public async Task ProcessFolder(string srcFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline)
        {
            bool debug = false ;
            if (pipeline == null) return;

            _mainViewModel.Status = $"Processing folder " + srcFolder;
            var sourceFolder = _fileProcessor.GetImageFilesPaths(srcFolder);
            var pipelineToUse = pipeline;
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
                using (var workerPool = new ImgWorkerPool(_poolCts, pipelineToUse, 0, sourceFolder, 0))
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