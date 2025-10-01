using OpenCvSharp.ML;
using System.Windows.Media;
using System.Windows;
using ImgViewer.Interfaces;
using System.Web.UI.WebControls.WebParts;
using System.Diagnostics;

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

        public async Task SetImageForProcessing(byte[] bmpBytes)
        {
            _imageProcessor.BmpBytes = bmpBytes;
        }

        public async Task SetBmpImageOnPreview(ImageSource bmp)
        {
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                _mainViewModel.ImageOnPreview = bmp;
                _mainViewModel.Status = $"Ready";
            }, System.Windows.Threading.DispatcherPriority.Render);
        }

        public async Task SetImageOnPreview(string imagePath)
        {
            _mainViewModel.CurrentImagePath = imagePath;
            _mainViewModel.Status = $"Loading image preview...";
            var (bmpImage, bmpBytes) = await Task.Run(() => _fileProcessor.Load<ImageSource>(imagePath));
            await SetBmpImageOnPreview(bmpImage);
            await SetImageForProcessing(bmpBytes);
            _mainViewModel.Status = $"Standby";
        }

        public void ApplyCommandToProcessingImage(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            _mainViewModel.Status = $"Processing image...";
            _imageProcessor.ApplyCommandToCurrent(command, parameters);
            _mainViewModel.Status = $"Standby";
        }

        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression)
        {
            var stream = _imageProcessor.GetStreamForSaving(ImageFormat.Tiff, compression);
            Debug.WriteLine($"Stream length: {stream.Length}");
            _fileProcessor.SaveTiff(stream, outputPath, compression, true);
        }

        public void Dispose()
        {
            GC.SuppressFinalize(this);
        }

    }
}