using System.IO;
using System.Windows.Media.Imaging;
using ImgViewer.Internal.Abstractions;

namespace ImgViewer.Internal
{
    internal class FileExplorer : IFileProcessor
    {
        private CancellationTokenSource _cts;

        public event Action<string> ErrorOccured;

        public FileExplorer(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public BitmapImage Load(string path, int? decodePixelWidth = null)
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

        public void Load(string path, Stream stream)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.CopyTo(stream);
            }
            if (stream.CanSeek)
            {
                stream.Position = 0;
            }
        }

        public void Save(Stream stream, string path)
        {
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
                stream.CopyTo(fileStream);
            }
        }

        public byte[] LoadImageBytes(string path)
        {
            return File.ReadAllBytes(path);
        }

        public SourceImageFolder? GetImageFilesPaths(string folderPath)
        {
            if(_cts.IsCancellationRequested)
            {
                return null;
            }
            if (!Directory.Exists(folderPath))
            {
                ErrorOccured?.Invoke($"Directory does not exist: {folderPath}");
                return null;
            }
            var parentPath = Directory.GetParent(folderPath)?.FullName;
            if (parentPath == null)
            {
                ErrorOccured?.Invoke($"Cannot determine parent directory for: {folderPath}");
                return null;
            }
            var sourceFolder = new SourceImageFolder
            {
                Path = folderPath,
                ParentPath = parentPath,
                Files = Directory.EnumerateFiles(folderPath)
                                        .Where(file => file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                       file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                                        .Take(100)
                                        .ToArray()
            };


            return sourceFolder;    
        }
    }
}
