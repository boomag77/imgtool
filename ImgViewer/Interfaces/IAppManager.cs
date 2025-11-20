using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IAppManager
    {
        public TiffCompression CurrentTiffCompression { get; set;  }
        public string LastOpenedFolder { get; set; }
        public Task SetImageOnPreview(string imagePath);
        public Task SetBmpImageOnPreview(ImageSource bmp);

        public void SavePipelineToJSON(string path, string json);

        public Task SetImageForProcessing(ImageSource bmp);
        public void ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters);
        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression, string imageDesvription = null);
        public Task ProcessFolder(string srcFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline);

        public Task ProcessRootFolder(string rootFolder, (ProcessorCommand command, Dictionary<string, object> parameters)[] pipeline, bool fullTree);


        public void CancelBatchProcessing();

        public void Shutdown();
    }

}
