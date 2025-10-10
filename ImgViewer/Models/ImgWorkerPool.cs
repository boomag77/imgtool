using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows.Media;

namespace ImgViewer.Models
{
    internal class ImgWorkerPool
    {
        private readonly BlockingCollection<string> _filesQueue;
        private readonly CancellationTokenSource _cts;
        private readonly IImageProcessor _imageProcessor;
        private readonly IFileProcessor _fileExplorer;
        private readonly SourceImageFolder _sourceFolder;
        private string _outputFolder = string.Empty;
        private readonly int _workersCount;
        private readonly ProcessorCommands[] _commandsQueue;

        private int _processedCount;
        private readonly int _totalCount;

        public event Action<string>? ErrorOccured;
        public event Action<int, int>? ProgressChanged;

        public ImgWorkerPool(CancellationTokenSource cts,
                             ProcessorCommands[] commandsQueue,
                             int maxWorkersCount,
                             IFileProcessor fileExplorer,
                             SourceImageFolder sourceFolder,
                             int maxFilesQueue = 0)
        {
            _cts = cts;
            _fileExplorer = fileExplorer;
            _sourceFolder = sourceFolder;

            _outputFolder = Path.Combine(_sourceFolder.Path, "Processed_1");
            Directory.CreateDirectory(_outputFolder);
            _commandsQueue = commandsQueue;

            int cpuCount = Environment.ProcessorCount;
            _workersCount = maxWorkersCount == 0 ? cpuCount : maxWorkersCount;

            _filesQueue = new BlockingCollection<string>(
                maxFilesQueue == 0 ? _workersCount * 2 : maxFilesQueue
            );
            _processedCount = 0;
            _totalCount = sourceFolder.Files.Length;
        }

        /// <summary>
        /// Наполняем очередь файлами
        /// </summary>
        private async Task EnqueueFiles()
        {
            foreach (var file in _sourceFolder.Files)
            {
                if (_cts.IsCancellationRequested)
                    break;

                _filesQueue.Add(file);
                await Task.Yield();
            }

            _filesQueue.CompleteAdding();

        }

        /// <summary>
        /// Один воркер: берёт файлы и обрабатывает
        /// </summary>
        /// 

        private ImageSource? LoadImage(string imagePath)
        {
            //var (bmpImage, _) = _fileExplorer.Load<ImageSource>(imagePath);
            var bmpImage = _fileExplorer.LoadTemp(imagePath);
            return bmpImage;
        }

        private void Worker(IImageProcessor proc)
        {
            try
            {
                foreach (var filePath in _filesQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        if (_cts.IsCancellationRequested)
                            break;

                        // Загружаем картинку
                        //using var inputStream = new MemoryStream();
                        //_fileExplorer.Load(filePath, inputStream);


                        // Устанавливаем картинку в процессор
                        proc.CurrentImage = LoadImage(filePath);




                        // Применяем команды
                        foreach (var command in _commandsQueue)
                        {

                            proc.ApplyCommandToCurrent(command, new Dictionary<string, object>());
                        }

                        // Сохраняем результат
                        //var outputDir = Path.Combine(_sourceFolder.Path, "Processed");
                        //Directory.CreateDirectory(_outputFolder);

                        var fileName = Path.ChangeExtension(Path.GetFileName(filePath), ".tif");
                        var outputFilePath = Path.Combine(_outputFolder, fileName);
                        //proc.SaveCurrentImage(outputFilePath);
                        using (var outStream = proc.GetStreamForSaving(ImageFormat.Tiff, TiffCompression.CCITTG4))
                        {
                            using (var ms = new MemoryStream())
                            {
                                if (outStream.CanSeek) outStream.Position = 0;
                                outStream.CopyTo(ms);
                                ms.Position = 0;
                                _fileExplorer.SaveTiff(ms, outputFilePath, TiffCompression.CCITTG4, 300, true);
                            }
                        }

                        //var ms = new MemoryStream();
                        //outStream.CopyTo(ms);
                        //Debug.WriteLine($"Stream length: {outStream.Length}");




                        Interlocked.Increment(ref _processedCount);
                        ProgressChanged?.Invoke(_processedCount, _totalCount);


                    }
                    catch (Exception ex)
                    {
                        ErrorOccured?.Invoke($"Error processing (Worker) {filePath}: {ex.Message}");
                    }

                }
            }
            finally
            {
                if (proc is IDisposable disposableProc)
                    disposableProc.Dispose();
            }

        }

        /// <summary>
        /// Запускаем пул воркеров
        /// </summary>
        public async Task RunAsync()
        {
            var tasks = new List<Task>();

            for (int i = 0; i < _workersCount; i++)
            {
                var proc = new OpenCVImageProcessor(null, _cts.Token);
                tasks.Add(Task.Run(() => Worker(proc), _cts.Token));
            }

            // Параллельно наполняем очередь
            var enqueueTask = Task.Run(() => EnqueueFiles(), _cts.Token);
            tasks.Add(enqueueTask);
            await Task.WhenAll(tasks);

        }
    }
}
