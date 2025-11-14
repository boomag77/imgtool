using ImgViewer.Models;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImgViewer.Interfaces
{
    public interface IFileProcessor
    {
        (T?, byte[]) Load<T>(string path, uint? decodePixelWidth = null) where T : class;
        BitmapSource? LoadTemp(string path);
        byte[] LoadBmpBytes(string path, uint? decodePixelWidth = null);
        SourceImageFolder? GetImageFilesPaths(string folderPath);

        SourceImageFolder[]? GetSubFoldersWithImagesPaths(string rootFolderPath);

        SourceImageFolder[]? GetSubFoldersWithImagesPaths_FullTree(string rootFolderPath);

        void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite);
        void Save(Stream stream, string path);


        event Action<string> ErrorOccured;
    }
}
