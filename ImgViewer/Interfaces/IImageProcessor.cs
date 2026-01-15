using ImgViewer.Models;
using OpenCvSharp;
using System.IO;

namespace ImgViewer.Interfaces
{
    public interface IImageProcessor
    {

        public object CurrentImage { set; }
        public void UpdateCancellationToken(CancellationToken token);
        public bool TryGetStreamForSave(ImageFormat format, out MemoryStream? ms, out string error);
        public TiffInfo GetTiffInfo(TiffCompression compression, int dpi);
        //Mat ProcessSingle(Mat src, ProcessorCommand command, Dictionary<string, object> parameters, CancellationToken token, bool batchProcessing);

        bool TryApplyCommand(ProcessorCommand commandName,
            Dictionary<string, object> parameters,
            bool batchProcessing = false,
            string currentFilePath = null,
            Action<string> log = null);

        event Action<Stream> ImageUpdated;
        event Action<string> ErrorOccured;
    }

    public enum ImageProcessorType
    {
        OpenCV,
        Leadtools,
        ImageMagick
    }

    public interface IImageProcessorFactory
    {
        IImageProcessor CreateProcessor(ImageProcessorType procType, string licPath = null, string licKey = null);
    }

    public enum ImageFormat
    {
        Bmp,
        Jpeg,
        Png,
        Tiff,
        Pdf
    }


    public enum ProcessorCommand
    {
        Deskew,
        Binarize,
        BordersRemove,
        Despeckle,
        SmartCrop,
        LinesRemove,
        DotsRemove,
        PunchHolesRemove,
        ChannelsCorrection,
        PageSplit,
        Enhance
    }

}
