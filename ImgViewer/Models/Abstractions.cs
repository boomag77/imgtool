
using System.IO;

namespace ImgViewer.Models
{

    public interface IFileProcessor
    {
        //void Load(string path, Stream stream);
        //BitmapImage Load(string path, int? decodePixelWidth = null);
        T Load<T>(string path, uint? decodePixelWidth = null) where T : class;
        void Save(Stream stream, string path);

        event Action<string> ErrorOccured;
    }

    public class SourceImageFolder
    {
        public string Path { get; set; }
        public string ParentPath { get; set; }
        public string[] Files { get; set; }
    }





}


