using ImageMagick;
using ImgViewer.Interfaces;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class FileProcessor : IFileProcessor, IDisposable
    {
        private CancellationToken _token;


        public event Action<string> ErrorOccured;

        public FileProcessor(CancellationToken token)
        {
            _token = token;
        }

        public void Dispose()
        {
            // Dispose of unmanaged resources here if needed
        }

        public byte[] LoadBmpBytes(string path, uint? decodePixelWidth = null)
        {
            try
            {
                if (_token.IsCancellationRequested)
                {
                    return Array.Empty<byte>();
                }
                MagickReadSettings settings = new MagickReadSettings();
                if (decodePixelWidth.HasValue)
                    settings.Width = decodePixelWidth.Value;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan))
                using (var image = new MagickImage(fs, settings))
                {
                    image.AutoOrient();
                    return image.ToByteArray(MagickFormat.Bmp);
                }
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
                return Array.Empty<byte>();
            }
        }

        public BitmapSource? LoadTemp(string path)
        {
            try
            {
                if (_token.IsCancellationRequested)
                {
                    return null;
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

                bitmap.CacheOption = BitmapCacheOption.OnLoad;

                bitmap.EndInit();
                bitmap.Freeze();


                return bitmap;
            }
            catch (Exception ex2)
            {
                ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");
                return null;
            }
        }


        public (T?, byte[]) Load<T>(string path, uint? decodePixelWidth = null) where T : class
        {

            //_magickSemaphore.Wait(_token);
            //return ((T)(object)TiffReader.LoadImageSourceFromTiff(path), null);
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".tif" || extension == ".tiff")
            {
                // Try to load TIFF via LibTiff
                var bmp = TiffReader.LoadImageSourceFromTiff(path).GetAwaiter().GetResult();
                if (bmp != null)
                {
                    return ((T)(object)bmp, []);
                }
                // If failed, fallback to Magick.NET
            }

            try
            {
                if (_token.IsCancellationRequested)
                {
                    return (null, null);
                }
                //throw new Exception();
                //return (null, null);
                MagickReadSettings settings = new MagickReadSettings();

                if (decodePixelWidth.HasValue)
                    settings.Width = decodePixelWidth.Value;

                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                using (var image = new MagickImage(fs, settings))
                {
                    image.AutoOrient();
                    switch (typeof(T))
                    {
                        case Type type when type == typeof(ImageSource):
                            //byte[] bmpBytes = image.ToByteArray(MagickFormat.Bmp);
                            {
                                uint width = image.Width;
                                uint height = image.Height;
                                uint bytesPerPixel = image.ChannelCount; // 3 for RGB, 4 for RGBA
                               uint stride = width * bytesPerPixel;

                                PixelMapping pixelMapping = bytesPerPixel == 4 ? PixelMapping.BGRA : PixelMapping.BGR;
                                using (var pixels = image.GetPixels())
                                {
                                    byte[]? pixelData = pixels.ToByteArray(0, 0, width, height, pixelMapping);



                                    var bmpSource = BitmapSource.Create((int)width, (int)height, 96, 96,
                                        bytesPerPixel == 4 ? System.Windows.Media.PixelFormats.Bgra32 : System.Windows.Media.PixelFormats.Bgr24,
                                        null, pixelData, (int)stride);


                                    bmpSource.Freeze();

                                    byte[] bmpBytes;
                                    using (var ms = new MemoryStream())
                                    {
                                        BitmapEncoder encoder = new BmpBitmapEncoder();
                                        encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                                        encoder.Save(ms);
                                        bmpBytes = ms.ToArray();
                                    }

                                    return ((T)(object)bmpSource, bmpBytes);
                                }

                            }

                        default:
                            ErrorOccured?.Invoke($"Unsupported type requested while loading image {path}: {typeof(T).FullName}");
                            return (null, null);
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
                        return (null, null);
                    }
                    if (typeof(T) == typeof(BitmapImage) || typeof(T) == typeof(ImageSource))
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


                        return ((T)(object)bitmap, []);
                    }
                    else
                    {
                        ErrorOccured?.Invoke($"Fallback WIC supports only BitmapImage, requested: {typeof(T).FullName}");
                        return (null, null);
                    }
                }
                catch (Exception ex2)
                {
                    ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");
                    return (null, null);
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



        public void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite = true)
        {
            if (!IsValidPath(path))
            {
                return;
            }
            using var tiffSaver = new TiffWriter();
            tiffSaver.SaveTiff(stream, path, compression, dpi, overwrite);
        }


        private static void InvertBinary(byte[] bin)
        {
            for (int i = 0; i < bin.Length; i++)
                bin[i] = (byte)(bin[i] == 0 ? 255 : 0);
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

        public SourceImageFolder[]? GetSubFoldersWithImagesPaths(string rootFolderPath)
        {
            if (_token.IsCancellationRequested) return null;
            if (!Directory.Exists(rootFolderPath))
            {
                ErrorOccured?.Invoke($"Directory does not exist: {rootFolderPath}");
                return null;
            }
            var subFolders = new List<SourceImageFolder>();
            IEnumerable<string> subFolderPath = Directory.EnumerateDirectories(rootFolderPath);
            foreach (string subFolder in subFolderPath)
            {
                var sub = GetImageFilesPaths(subFolder);
                if (sub == null || sub.Files.Length == 0) continue;
                subFolders.Add(sub);
            }
            return subFolders.ToArray();
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
                                        .ToArray()
            };


            return sourceFolder;
        }

    }
}
