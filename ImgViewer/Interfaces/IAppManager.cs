using System.Windows.Media;
using ImgViewer.Models;

namespace ImgViewer.Interfaces
{
    public interface IAppManager
    {
        public TiffCompression CurrentTiffCompression { get; set;  }
        public string LastOpenedFolder { get; set; }

        public double EraseOperationOffset { get; set; }

        public TimeSpan ParametersChangedDebounceDelay { get; set; }

        public bool IsSavePipelineToMd { get; set; }

        public Task SetImageOnPreview(string imagePath);
        public Task SetBmpImageOnPreview(ImageSource bmp);

        public void CancelImageProcessing();

        public void UpdateStatus(string status);

        public void SavePipelineToJSON(string path, string json);

        public Task SetImageForProcessing(ImageSource bmp);
        public Task ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters);
        public void SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression, string imageDesvription = null);
        public Task ProcessFolder(string srcFolder, Pipeline pipeline);

        public Task ProcessRootFolder(string rootFolder, Pipeline pipeline, bool fullTree);


        public void CancelBatchProcessing();

        public void Shutdown();
    }

}
