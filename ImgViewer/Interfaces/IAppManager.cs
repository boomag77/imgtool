using System.Windows.Media;
using ImgViewer.Models;
using ImgViewer.Models.Onnx;

namespace ImgViewer.Interfaces
{
    public interface IAppManager
    {
        public TiffCompression CurrentTiffCompression { get; set;  }
        public string LastOpenedFolder { get; set; }

        public Pipeline CurrentPipeline { get; }

        public double EraseOperationOffset { get; set; }

        public TimeSpan ParametersChangedDebounceDelay { get; set; }

        public bool IsSavePipelineToMd { get; set; }

        //public DocBoundaryModel? DocBoundaryModel { get; }

        public Task SetImageOnPreview(string imagePath);
        public Task SetBmpImageOnPreview(ImageSource bmp);

        public void CancelImageProcessing();

        public Task ResetWorkingImagePreview();

        public void UpdateStatus(string status);

        public Task SavePipelineToJSON(string path, string json);

        public Task SetImageForProcessing(ImageSource bmp);
        public Task ApplyCommandToProcessingImage(ProcessorCommand command, Dictionary<string, object> parameters);
        public Task SaveProcessedImage(string outputPath, ImageFormat format, TiffCompression compression, string imageDesvription = null);
        public Task ProcessFolder(string srcFolder, Pipeline pipeline, CancellationToken batchToken);
        public Task ProcessFolder(string srcFolder, Pipeline pipeline);

        public Task ProcessRootFolder(string rootFolder, Pipeline pipeline, bool fullTree);

        public Task LoadPipelineFromFile(string fileNamePath);

        public void CancelBatchProcessing();

        public void Shutdown();
    }

}
