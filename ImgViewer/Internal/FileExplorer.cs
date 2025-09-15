using System.IO;
using System.Windows.Media.Imaging;

namespace ImgViewer.Internal
{
    internal class FileExplorer
    {
        public FileExplorer()
        {
        }

        public BitmapImage LoadImage(string path, int? decodePixelWidth = null)
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(path);
            if (decodePixelWidth.HasValue)
                bitmap.DecodePixelWidth = decodePixelWidth.Value;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }

        public Stream LoadImage(string path)
        {
            var stream = new MemoryStream();
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
    }
}
