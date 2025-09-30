using ImageMagick;
using System.IO;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class FileExplorer : IFileProcessor
    {
        private CancellationToken _token;

        public event Action<string> ErrorOccured;

        public FileExplorer(CancellationToken token)
        {
            _token = token;
        }

        //public void Load(string path, Stream stream)
        //{
        //    try
        //    {
        //        using (var image = new MagickImage(path))
        //        {
        //            image.Format = MagickFormat.Png;
        //            image.Write(stream);
        //        }
        //        if (stream.CanSeek)
        //        {
        //            stream.Position = 0;
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
        //    }
        //}

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

                using (var image = new MagickImage(path, settings))
                {
                    switch (typeof(T))
                    {
                        case Type type when type == typeof(BitmapImage):

                            using (var ms = new MemoryStream())
                            {
                                if (decodePixelWidth.HasValue)
                                {
                                    //image.Quality = 50;
                                    image.Write(ms, MagickFormat.Png);
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
                                    //bitmap.DecodePixelWidth = (int)decodePixelWidth.Value;
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

        private bool IsValidPath(string? path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            if (path.EndsWith(Path.DirectorySeparatorChar.ToString()) ||
                path.EndsWith(Path.AltDirectorySeparatorChar.ToString())) return false;
            var fullPath = Path.GetFullPath(path);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName)) return false;
            if (!Path.HasExtension(fileName)) return false;
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(ext) || ext.Length < 2) return false;
            return true;
        }




        public void SaveTiff(Stream stream, string path, TiffCompression compression, bool overwrite = true)
        {
            if (stream == null)
            {
                ErrorOccured?.Invoke("Empty data stream for saving TIFF.");
                return;
            }
            if (!IsValidPath(path))
            {
                ErrorOccured?.Invoke($"Invalid file path for saving: {path}");
                return;
            }
            MemoryStream ms = stream as MemoryStream;
            bool createdCopy = false;
            if (ms == null || !ms.CanSeek)
            {
                ms = new MemoryStream();
                try
                {
                    stream.CopyTo(ms);
                    createdCopy = true;
                }
                catch (Exception ex)
                {
                    ErrorOccured?.Invoke($"Failed to read input stream: {ex.Message}");
                    ms.Dispose();
                    return;
                }
            }
            ms.Position = 0;

            var compdefine = compression switch
            {
                TiffCompression.None => "none",
                TiffCompression.CCITTG3 => "group3",
                TiffCompression.CCITTG4 => "group4",
                TiffCompression.LZW => "lzw",
                TiffCompression.Deflate => "zip",
                TiffCompression.JPEG => "jpeg",
                TiffCompression.PackBits => "packbits",
                _ => null
            };
            try
            {
                using var image = new MagickImage(stream);

                bool requestedCcitt = (compression == TiffCompression.CCITTG3 || compression == TiffCompression.CCITTG4);
                if (requestedCcitt)
                {
                    bool isBilevel = image.Depth == 1 || image.ColorType == ColorType.Bilevel;
                    if (!isBilevel)
                    {
                        ErrorOccured?.Invoke($"Warning: CCITT requested but input image is not bilevel (Depth={image.Depth}, ColorType={image.ColorType}). " +
                                             "Prefer to pass a 1-bit (bilevel) image when requesting CCITT compression.");
                        return;
                    }
                }

                if (!string.IsNullOrEmpty(compdefine))
                {
                    image.Settings.SetDefine("tiff:compression", compdefine);
                }
                image.Format = MagickFormat.Tiff;

                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (Exception ex)
                    {
                        ErrorOccured?.Invoke($"Failed to overwrite existing file {path}: {ex.Message}");
                        if (createdCopy) ms.Dispose();
                        return;
                    }
                }

                image.Write(path);
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Failed to save TIFF to {path}: {ex.Message}");
            }
            finally
            {
                if (createdCopy) ms.Dispose();
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
            if (_token.IsCancellationRequested)
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
