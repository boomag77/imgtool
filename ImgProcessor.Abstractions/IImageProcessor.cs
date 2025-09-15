using ImgProcessor.Abstractions;

namespace ImgProcessor.Abstractions
{
    public interface IImageProcessor
    {
        void LoadImage(string path);
        void SaveCurrentImage(string path);
        void SaveImage(string path, Stream stream);
        void ApplyCommandToCurrent(ProcessorCommands commandName, Dictionary<string, object> parameters);

        event Action<Stream> ImageUpdated;
        event Action<string> ErrorOccured;
    }

    public interface IImageProcessorFactory
    {
        IImageProcessor CreateProcessor();
    }

    public enum ProcessorCommands
    {
        Deskew,
        Binarize,
        BorderRemove,
        Despeckle,
        AutoCropRectangle,
        LineRemove
    }
}

