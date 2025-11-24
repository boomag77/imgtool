using System.IO;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IImageProcessor
    {
        
        public ImageSource CurrentImage { set; }
        



        void Load(string path);

        public void UpdateCancellationToken(CancellationToken token);

        Stream? LoadAsPNGStream(string path, int targetBPP);
        //void SaveCurrentImage(string path);
        Stream? GetStreamForSaving(ImageFormat format, TiffCompression compression);
        void ApplyCommand(ProcessorCommand commandName, Dictionary<string, object> parameters);

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
        ChannelsCorrection
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

