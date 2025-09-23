namespace ImgProcessor.Abstractions
{
    public interface IImageProcessor
    {
        void Load(string path);

        Stream? LoadAsPNGStream(string path, int targetBPP);
        void SaveCurrentImage(string path);
        void ApplyCommandToCurrent(ProcessorCommands commandName, Dictionary<string, object> parameters);

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



    public enum ProcessorCommands
    {
        Deskew,
        Binarize,
        BorderRemove,
        Despeckle,
        AutoCropRectangle,
        LineRemove,
        DotsRemove
    }
}

