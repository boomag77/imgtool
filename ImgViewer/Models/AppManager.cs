using ImgViewer.Interfaces;
using System.Diagnostics;
using System.Windows.Media;
using System.Text.Json;
using System.Text;

namespace ImgViewer.Models
{
    internal class AppManager : IAppManager, IDisposable
    {

        private readonly IViewModel _mainViewModel;
        private readonly IFileProcessor _fileProcessor;
        private readonly IImageProcessor _imageProcessor;

        private readonly CancellationTokenSource _cts;
        private CancellationTokenSource? _poolCts;

        public AppManager(IMainView mainView, CancellationTokenSource cts)
        {
            _cts = cts;
            _mainViewModel = new MainViewModel();
            mainView.ViewModel = _mainViewModel;
            _fileProcessor = new FileProcessor(_cts.Token);
            _imageProcessor = new OpenCVImageProcessor(this, _cts.Token);

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

        public async Task SetBmpImageAsOriginal(ImageSource bmp)
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
            _mainViewModel.Status = $"Processing image...";
            
            _imageProcessor.ApplyCommandToCurrent(command, parameters);
            _mainViewModel.Status = $"Standby";
        }

        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression)
        {
            var stream = _imageProcessor.GetStreamForSaving(ImageFormat.Tiff, compression);
            Debug.WriteLine($"Stream length: {stream.Length}");
            _fileProcessor.SaveTiff(stream, outputPath, compression, 300, true);
        }

        public async Task ProcessFolder(string srcFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline = null)
        {
            bool debug = false;

            _mainViewModel.Status = $"Processing folder " + srcFolder;
            var sourceFolder = _fileProcessor.GetImageFilesPaths(srcFolder);
            var pipelineToUse = pipeline ?? new (ProcessorCommand, Dictionary<string, object>)[]
               {
                    (ProcessorCommand.Deskew, new Dictionary<string, object>()),
                    (ProcessorCommand.BordersRemove, new Dictionary<string, object>()),
                    //(ProcessorCommands.AutoCropRectangle, new Dictionary<string, object>()),
                    (ProcessorCommand.Binarize, new Dictionary<string, object>()),
               };
            if (debug)
            {
                string pipeLineForSave = BuildPipelineForSave(pipelineToUse);
                Debug.WriteLine("Pipeline JSON for save:");
                Debug.WriteLine(pipeLineForSave);
                return;
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
                    catch (Exception ex)
                    {
                        
                    }

                }
            }
            finally
            {
                _mainViewModel.Status = $"Standby";
            }
        }

        public void StopProcessingFolder()
        {
            if (_poolCts == null) return;
            try
            {
                _poolCts.Cancel();
            }
            finally
            {
                _poolCts.Dispose();
                _poolCts = null;
            }
        }

        public string BuildPipelineForSave((ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline)
        {
            if (pipeline == null) return "[]";

            // Проектируем каждый шаг в объект с параметрами string->string
            var items = pipeline.Select(step => new
            {
                command = step.command.ToString(), // или (int)step.command если хочешь числовой код
                parameters = (step.parameters == null || step.parameters.Count == 0)
                    ? new Dictionary<string, string>()
                    : step.parameters.ToDictionary(
                        kv => kv.Key,
                        kv => FormatParamValue(kv.Value) ?? "null"  // FormatParamValue уже у тебя есть
                    )
            }).ToList();

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(items, options);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

        static string FormatParamValue(object? v)
        {
            if (v == null) return "null";

            // primitives and strings
            var t = v.GetType();
            if (t.IsPrimitive || v is decimal || v is string || v is DateTime || v is Guid)
                return v.ToString() ?? "<empty>";

            // IDictionary
            if (v is System.Collections.IDictionary dict)
            {
                var items = new List<string>();
                foreach (var key in dict.Keys)
                {
                    var val = dict[key];
                    items.Add($"{key}={FormatParamValue(val)}");
                    if (items.Count >= 10) { items.Add("..."); break; } // limit length
                }
                return "{" + string.Join(", ", items) + "}";
            }

            // IEnumerable (but not string)
            if (v is System.Collections.IEnumerable ie && !(v is string))
            {
                var items = new List<string>();
                int i = 0;
                foreach (var it in ie)
                {
                    items.Add(FormatParamValue(it));
                    if (++i >= 8) { items.Add("..."); break; } // limit items
                }
                return "[" + string.Join(", ", items) + "]";
            }

            // Common heavy/complex types: show type name and some hint instead of trying to serialize them
            var typeName = t.Name;
            if (typeName.Contains("Mat") || typeName.Contains("Image") || typeName.Contains("Bitmap") || typeName.Contains("ImageSource"))
            {
                return $"<{typeName}>";
            }

            // Last resort: ToString (may be type name)
            try { return v.ToString() ?? $"<{typeName}>"; } catch { return $"<{typeName}>"; }
        }

    }
}