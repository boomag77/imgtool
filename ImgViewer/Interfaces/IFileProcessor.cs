using System.IO;

namespace ImgViewer.Interfaces
{
    public interface IFileProcessor
    {
        (T, byte[]?) Load<T>(string path, uint? decodePixelWidth = null) where T : class;
        byte[] LoadBmpBytes(string path, uint? decodePixelWidth = null);
        void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite);
        void Save(Stream stream, string path);

        event Action<string> ErrorOccured;
    }
}
