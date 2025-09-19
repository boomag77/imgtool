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
        LineRemove,
        DotsRemove
    }
}

