using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Models
{
    internal class ImgWorkerPool
    {
        private readonly BlockingCollection<string> _filesQueue;
        private readonly CancellationTokenSource _cts;
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
                             SourceImageFolder sourceFolder,
                             int maxFilesQueue = 0)
        {
            _cts = cts;
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

        private void Worker()
        {
            using var imgProc = new OpenCVImageProcessor(null, _cts.Token);
            using var fileProc = new FileProcessor(_cts.Token);
            try
            {
                foreach (var filePath in _filesQueue.GetConsumingEnumerable(_cts.Token))
                {
                    try
                    {
                        if (_cts.IsCancellationRequested)
                            break;

                        imgProc.CurrentImage = fileProc.Load<ImageSource>(filePath).Item1;




                        // Применяем команды
                        foreach (var command in _commandsQueue)
                        {

                            imgProc.ApplyCommandToCurrent(command, new Dictionary<string, object>());
                        }


                        var fileName = Path.ChangeExtension(Path.GetFileName(filePath), ".tif");
                        var outputFilePath = Path.Combine(_outputFolder, fileName);
                        //proc.SaveCurrentImage(outputFilePath);
                        using (var outStream = imgProc.GetStreamForSaving(ImageFormat.Tiff, TiffCompression.CCITTG4))
                        {
                            using (var ms = new MemoryStream())
                            {
                                if (outStream.CanSeek) outStream.Position = 0;
                                outStream.CopyTo(ms);
                                ms.Position = 0;
                                fileProc.SaveTiff(ms, outputFilePath, TiffCompression.CCITTG4, 300, true);
                            }
                        }



                        Interlocked.Increment(ref _processedCount);
                        ProgressChanged?.Invoke(_processedCount, _totalCount);


                    }
                    catch (Exception ex)
                    {
                        ErrorOccured?.Invoke($"Error processing (Worker) {filePath}: {ex.Message}");
                    }

                }
            }
            catch (OperationCanceledException)
            {
                //Debug.WriteLine("Worker cancelled");
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error in worker: {ex.Message}");
            }

        }

        public async Task RunAsync()
        {
            var tasks = new List<Task>();

            for (int i = 0; i < _workersCount; i++)
            {

                tasks.Add(Task.Run(() => Worker(), _cts.Token));
            }

            // Параллельно наполняем очередь
            var enqueueTask = Task.Run(() => EnqueueFiles(), _cts.Token);
            tasks.Add(enqueueTask);
            await Task.WhenAll(tasks);

        }
    }
}
