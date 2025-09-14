using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;


namespace ImgProcessor.Abstractions
{
    public interface IImageProcessor
    {
        void LoadImage(string path);
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
        Binarize
    }
}
