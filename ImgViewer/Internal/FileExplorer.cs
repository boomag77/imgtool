using System.IO;
using System.Windows.Media.Imaging;
using ImgProcessor.Abstractions;
using ImgViewer.Internal.Abstractions;
using ImageMagick;
using System.CodeDom;

namespace ImgViewer.Internal
{
    internal class FileExplorer : IFileProcessor
    {
        private CancellationToken _token;
        private IImageProcessor _processor;

        public event Action<string> ErrorOccured;

        public FileExplorer(CancellationToken token)
        {
            _token = token;
        }

        public void Load(string path, Stream stream)
        {
            try
            {
                using (var image = new MagickImage(path))
                {
                    image.Format = MagickFormat.Png;
                    image.Write(stream);
                }
                if (stream.CanSeek)
                {
                    stream.Position = 0;
                }
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
            }
        }

        public T? Load<T>(string path, uint? decodePixelWidth = null) where T : class
        {
            try
            {
                if (_token.IsCancellationRequested)
                {
                    return null;
                }
                MagickReadSettings settings = new MagickReadSettings();
                if (decodePixelWidth.HasValue)
                    settings.Width = decodePixelWidth.Value;

                using (var image = new MagickImage(path))
                {   
                    switch (typeof(T))
                    {
                        case Type type when type == typeof(MagickImage):
                            return (T)(object)image.Clone();
                        case Type type when type == typeof(BitmapImage):

                            using (var ms = new MemoryStream())
                            {
                                if (decodePixelWidth.HasValue)
                                {
                                    image.Quality = 50;
                                    image.Write(ms, MagickFormat.Jpeg);
                                }
                                else
                                {
                                    image.Write(ms, MagickFormat.Png);
                                }

                                ms.Position = 0;
                                var bitmap = new BitmapImage();
                                bitmap.BeginInit();
                                if (decodePixelWidth.HasValue)
                                {
                                    bitmap.DecodePixelWidth = (int)decodePixelWidth.Value;
                                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                                }

                                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                                bitmap.StreamSource = ms;
                                bitmap.EndInit();
                                bitmap.Freeze(); // чтобы можно было безопасно шарить BitmapImage между потоками
                                return (T)(object)bitmap;
                            }

                        case Type type when type == typeof(byte[]):
                            return (T)(object)image.ToByteArray(MagickFormat.Png);
                        default:
                            ErrorOccured?.Invoke($"Unsupported type requested while loading image {path}: {typeof(T).FullName}");
                            return null;
                    }
                }
            }
            catch (Exception _)
            {
                // === Fallback через WIC ===
                try
                {
                    if (_token.IsCancellationRequested)
                    {
                        return null;
                    }
                    if (typeof(T) == typeof(BitmapImage))
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(path, UriKind.Absolute);
                        if (decodePixelWidth.HasValue)
                        {
                            bitmap.DecodePixelWidth = (int)decodePixelWidth.Value;
                            bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                        }
                            
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                           
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return (T)(object)bitmap;
                    }
                    else
                    {
                        ErrorOccured?.Invoke($"Fallback WIC supports only BitmapImage, requested: {typeof(T).FullName}");
                        return null;
                    }   
                }
                catch (Exception ex2)
                {
                    ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");
                    return null;
                }
            }

        }




        //public void Load(string path, Stream stream)
        //{
        //    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        //    {
        //        fs.CopyTo(stream);
        //    }
        //    if (stream.CanSeek)
        //    {
        //        stream.Position = 0;
        //    }
        //}

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
            if(_token.IsCancellationRequested)
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
                                        .Where(file =>
                                                    file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                                file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                                file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||   // если хочешь PNG тоже
                                                file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                                                file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                                        .Take(100)
                                        .ToArray()
            };


            return sourceFolder;    
        }
    }
}
