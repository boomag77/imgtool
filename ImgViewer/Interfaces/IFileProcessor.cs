using ImgViewer.Models;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IFileProcessor
    {
        (ImageSource, byte[]) LoadImageSource(string path, uint? decodePixelWidth = null);
        //BitmapSource? LoadTemp(string path);
        byte[] LoadBmpBytes(string path, uint? decodePixelWidth = null);
        SourceImageFolder? GetImageFilesPaths(string folderPath);

        SourceImageFolder[]? GetSubFoldersWithImagesPaths(string rootFolderPath);

        SourceImageFolder[]? GetSubFoldersWithImagesPaths_FullTree(string rootFolderPath);

        void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite, string? metadataJson = null);
        void Save(Stream stream, string path);


        event Action<string> ErrorOccured;
    }
}
