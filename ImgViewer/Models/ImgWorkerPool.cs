using ImgViewer.Interfaces;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Models
{
    internal class ImgWorkerPool : IDisposable
    {
        private bool _disposed;

        private readonly BlockingCollection<string> _filesQueue;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _token;
        private CancellationTokenRegistration _tokenRegistration;
        private readonly SourceImageFolder _sourceFolder;
        private string _outputFolder = string.Empty;
        private readonly int _workersCount;

        private readonly (ProcessorCommand command, Dictionary<string, object> parameters)[] _pipelineTemplate;

        //private readonly ProcessorCommands[] _commandsQueue;

        private int _processedCount;
        private readonly int _totalCount;

        public event Action<string>? ErrorOccured;
        public event Action<int, int>? ProgressChanged;

        public ImgWorkerPool(CancellationTokenSource cts,
                             (ProcessorCommand command, Dictionary<string, object> parameters)[] pipelineTemplate,
                             int maxWorkersCount,
                             SourceImageFolder sourceFolder,
                             int maxFilesQueue = 0)
        {
            _cts = cts;
            _token = _cts.Token;
            _sourceFolder = sourceFolder;

            _outputFolder = Path.Combine(_sourceFolder.Path, "Processed");
            Directory.CreateDirectory(_outputFolder);
            _pipelineTemplate = pipelineTemplate ?? Array.Empty<(ProcessorCommand, Dictionary<string, object>)>();

            int cpuCount = Environment.ProcessorCount;
            _workersCount = maxWorkersCount == 0 ? cpuCount : maxWorkersCount;

            _filesQueue = new BlockingCollection<string>(
                maxFilesQueue == 0 ? _workersCount * 2 : maxFilesQueue
            );
            _tokenRegistration = _token.Register(() =>
            {
                try { _filesQueue.CompleteAdding(); } catch { }
            });
            _processedCount = 0;
            _totalCount = sourceFolder.Files.Length;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _tokenRegistration.Dispose(); } catch { }
            try { _filesQueue?.Dispose(); } catch { }
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
                Debug.WriteLine("EnqueueFiles cancelled by token");
            }
            finally
            {
                try { _filesQueue.CompleteAdding(); } catch { }
            }

        }

        private void Worker()
        {
            var token = _token;

            using var imgProc = new OpenCVImageProcessor(null, token);
            using var fileProc = new FileProcessor(token);
            try
            {
                foreach (var filePath in _filesQueue.GetConsumingEnumerable(token))
                {
                    token.ThrowIfCancellationRequested();

                    var loaded = fileProc.Load<ImageSource>(filePath);
                    imgProc.CurrentImage = loaded.Item1;
                    //imgProc.CurrentImage = fileProc.Load<ImageSource>(filePath).Item1;

                    foreach (var op in _pipelineTemplate)
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            imgProc.ApplyCommandToCurrent(op.command, op.parameters ?? new Dictionary<string, object>());
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception exOp)
                        {
                            ErrorOccured?.Invoke($"Error applying op {op.command} to {filePath}: {exOp.Message}");
                        }
                    }

                    token.ThrowIfCancellationRequested();


                    //TODO make saving worker(s) and dissfetent queue for the saving

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
            }
            catch (OperationCanceledException)
            {

                Debug.WriteLine("Worker cancelled!");
                return;

            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error in worker: {ex.Message}");
            }

        }

        public async Task RunAsync()
        {
            var tasks = new List<Task>();
            try
            {
                for (int i = 0; i < _workersCount; i++)
                {
                    if (_cts.IsCancellationRequested) throw new OperationCanceledException();
                    tasks.Add(Task.Run(() => Worker(), _token));
                }

                // in parallel enqueing que
                var enqueueTask = Task.Run(() => EnqueueFiles(), _token);
                tasks.Add(enqueueTask);
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Enqueing cancelled!");
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error in Enqueing (Worker Pool): {ex.Message}");
            }
            finally
            {
                try { _tokenRegistration.Dispose(); } catch { }
                try { _filesQueue.Dispose(); } catch { }

            }
            


        }
    }
}
