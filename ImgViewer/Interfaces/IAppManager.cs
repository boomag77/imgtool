using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IAppManager
    {
        public Task SetImageOnPreview(string imagePath);
        public Task SetBmpImageOnPreview(ImageSource bmp);

        public Task SetImageForProcessing(ImageSource bmp);
        public void ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters);
        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression);
        public Task ProcessFolder(string srcFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline);

        public string BuildPipelineForSave((ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline);

        public void StopProcessingFolder();

        public void Shutdown();
    }

}
