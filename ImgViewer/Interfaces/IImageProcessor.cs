using ImgViewer.Models;
using OpenCvSharp;
using System.Buffers;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImgViewer.Interfaces
{
    public interface IImageProcessor
    {
        public enum RawPixelFormat
        {
            Gray8,
            Bgr24,
            Bgra32
        }

        public readonly record struct RawImageData(
            IMemoryOwner<byte> Owner,
            int Width,
            int Height,
            int Stride,
            RawPixelFormat Format);

        void SetImage(Mat mat);
        void SetImage(byte[] rawPixels);
        void SetImage(ReadOnlyMemory<byte> rom);
        void SetImage(RawImageData raw);

        //public object CurrentImage { set; }
        public void UpdateCancellationToken(CancellationToken token);
        public bool TryGetStreamForSave(ImageFormat format, out MemoryStream? ms, out string error);
        public TiffInfo GetTiffInfo(TiffCompression compression, int dpi);
        //Mat ProcessSingle(Mat src, ProcessorCommand command, Dictionary<string, object> parameters, CancellationToken token, bool batchProcessing);
        public JpegInfo GetJpegInfo(int quality, int dpi, int subSampling, Mat? outSrc = null);
        bool TryApplyCommand(ProcessorCommand commandName,
            Dictionary<string, object> parameters,
            bool batchProcessing = false,
            string currentFilePath = null,
            Action<string> log = null);

        event Action<Stream> ImageUpdated;
        event Action<string> ErrorOccured;
        event Action<BitmapSource, BitmapSource>? SplitPreviewUpdated;
        event Action? SplitPreviewCleared;

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
        Invert,
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
