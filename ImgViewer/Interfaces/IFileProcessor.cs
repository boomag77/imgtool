using ImgViewer.Models;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IFileProcessor
    {
        (ImageSource?, byte[]?) LoadImageSource(string path, bool isBatch, uint? decodePixelWidth = null);
        //BitmapSource? LoadTemp(string path);
        //byte[] LoadBmpBytes(string path, uint? decodePixelWidth = null);
        SourceImageFolder? GetImageFilesPaths(string folderPath, CancellationToken token);

        SourceImageFolder[] GetSubFoldersWithImagesPaths(string rootFolderPath, CancellationToken token);

        SourceImageFolder[] GetSubFoldersWithImagesPaths_FullTree(string rootFolderPath, CancellationToken token);

        void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite, string? metadataJson = null);
        public void SaveTiff(TiffInfo tiffInfo, string path, bool overwrite = true, string? metadataJson = null);
        void Save(Stream stream, string path);

        public IEnumerable<string> EnumerateSubFolderPaths(string rootFolderPath, bool fullTree, CancellationToken token);


        event Action<string> ErrorOccured;
    }
}
