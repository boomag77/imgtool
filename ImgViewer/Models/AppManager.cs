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

        public AppManager(IMainView mainView)
        {
            _cts = new CancellationTokenSource();
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

        public async void ProcessFolder(string srcFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline = null)
        {
            bool debug = true;

            _mainViewModel.Status = $"Processing folder...";
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
                Debug.WriteLine("!!!!! ------ WARNING! DEBUG IS ON IN FOLDER PROCESSING ----- !!!!!");
                Debug.WriteLine("------- PIPELINE PARAMS------");
                // дебаг вывод
                //foreach (var p in pipelineToUse)
                //{
                //    string paramStr;
                //    if (p.parameters == null || p.parameters.Count == 0)
                //    {
                //        paramStr = "{}";
                //    }
                //    else
                //    {
                //        try
                //        {
                //            // Try to serialize the dictionary to JSON for the most readable output.
                //            // Use limited depth/size by default options; if something isn't serializable we'll hit catch.
                //            paramStr = JsonSerializer.Serialize(p.parameters, new JsonSerializerOptions
                //            {
                //                WriteIndented = false,
                //                // ignore cycles / non-serializable members will throw
                //            });
                //        }
                //        catch
                //        {
                //            // Fallback: print each key=value using safe formatter
                //            var kvParts = p.parameters.Select(kv => $"{kv.Key}={FormatParamValue(kv.Value)}");
                //            paramStr = "{" + string.Join(", ", kvParts) + "}";
                //        }
                //    }

                //    Debug.WriteLine($"Pipeline step: {p.command} params: {paramStr}");
                //}

                string pipeLineForSave = BuildPipelineForSave(pipelineToUse);
                Debug.WriteLine("Pipeline JSON for save:");
                Debug.WriteLine(pipeLineForSave);
                return;
            }



            var workerPool = new ImgWorkerPool(_cts, pipelineToUse, 0, sourceFolder, 0);
            try
            {
                await workerPool.RunAsync();
            }
            catch (OperationCanceledException)
            {
                //StatusText.Text = "Cancelled";

            }
            _mainViewModel.Status = $"Standby";
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