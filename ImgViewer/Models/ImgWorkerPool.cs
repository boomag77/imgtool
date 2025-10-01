using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.IO;

namespace ImgViewer.Models
{
    internal class ImgWorkerPool
    {
        private readonly BlockingCollection<string> _filesQueue;
        private readonly CancellationTokenSource _cts;
        private readonly IImageProcessorFactory _processorFactory;
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
                             IImageProcessorFactory processorFactory,
                             IFileProcessor fileExplorer,
                             SourceImageFolder sourceFolder,
                             int maxFilesQueue = 0)
        {
            _cts = cts;
            _fileExplorer = fileExplorer;
            _processorFactory = processorFactory;
            _sourceFolder = sourceFolder;

            _outputFolder = Path.Combine(_sourceFolder.Path, "Processed");
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
        private void Worker(IImageProcessor proc)
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
                    proc.Load(filePath);


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

                    Interlocked.Increment(ref _processedCount);
                    ProgressChanged?.Invoke(_processedCount, _totalCount);

                }
                catch (Exception ex)
                {
                    ErrorOccured?.Invoke($"Error processing {filePath}: {ex.Message}");
                }

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
                //var proc = _processorFactory.CreateProcessor(ImageProcessorType.Leadtools);
                var proc = _processorFactory.CreateProcessor(ImageProcessorType.OpenCV);
                tasks.Add(Task.Run(() => Worker(proc), _cts.Token));
            }

            // Параллельно наполняем очередь
            var enqueueTask = Task.Run(() => EnqueueFiles(), _cts.Token);
            tasks.Add(enqueueTask);
            await Task.WhenAll(tasks);

        }
    }
}
