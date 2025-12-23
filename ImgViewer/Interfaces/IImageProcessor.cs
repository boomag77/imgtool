using OpenCvSharp;
using System.IO;
using System.Security.Policy;
using System.Windows.Media;
using static ImgViewer.Models.OpenCvImageProcessor;
using ImgViewer.Models;

namespace ImgViewer.Interfaces
{
    public interface IImageProcessor
    {
        
        public ImageSource CurrentImage { set; }

        



        public void UpdateCancellationToken(CancellationToken token);

        Stream? LoadAsPNGStream(string path, int targetBPP);
        //void SaveCurrentImage(string path);
        Stream? GetStreamForSaving(ImageFormat format, TiffCompression compression);

        //public (byte[] binPixels, int width, int height) GetBinPixelsFromMat(bool photometricMinIsWhite = false,
        //                                                                     bool useOtsu = true,
        //                                                                     double manualThreshold = 128);
        public TiffInfo GetTiffInfo(TiffCompression compression, int dpi);

        


        Mat ProcessSingle(Mat src, ProcessorCommand command, Dictionary<string, object> parameters, CancellationToken token, bool batchProcessing);

        bool ApplyCommand(ProcessorCommand commandName,
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

    //public enum TiffCompression
    //{
    //    None = 0,
    //    CCITTG4,  // G4
    //    CCITTG3,  // G3
    //    LZW,
    //    Deflate,
    //    JPEG,
    //    PackBits
    //}

}
