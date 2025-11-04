using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IAppManager
    {
        public Task SetImageOnPreview(string imagePath);
        public Task SetBmpImageOnPreview(ImageSource bmp);

        public Task SetImageForProcessing(ImageSource bmp);
        public void ApplyCommandToProcessingImage(ProcessorCommands command, Dictionary<string, object> parameters);
        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression);
        public void ProcessFolder(string srcFolder);
        public void Shutdown();
    }

}
