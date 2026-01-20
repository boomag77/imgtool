using ImgViewer.Models;
using System.IO;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IFileProcessor
    {
        (ImageSource?, byte[]?) LoadImageSource(string path, bool isBatch, uint? decodePixelWidth = null);

        public void SaveTiff(TiffInfo tiffInfo, string path, bool overwrite = true, string? metadataJson = null);
        void SaveStreamToFile(MemoryStream stream, string path);

        public IEnumerable<string> EnumerateSubFolderPaths(string rootFolderPath, bool fullTree, CancellationToken token);

        event Action<string> ErrorOccured;
    }
}
