using ImageMagick;
using ImgViewer.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Windows;
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

        //public BitmapSource? LoadTemp(string path)
        //{
        //    try
        //    {
        //        if (_token.IsCancellationRequested)
        //        {
        //            return null;
        //        }
        //        var bitmap = new BitmapImage();
        //        bitmap.BeginInit();
        //        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        //        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;

        //        bitmap.CacheOption = BitmapCacheOption.OnLoad;

        //        bitmap.EndInit();
        //        bitmap.Freeze();


        //        return bitmap;
        //    }
        //    catch (Exception ex2)
        //    {
        //        ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");
        //        return null;
        //    }
        //}


        public (ImageSource, byte[]) LoadImageSource(string path, uint? decodePixelWidth = null)
        {

            //_magickSemaphore.Wait(_token);
            //return ((T)(object)TiffReader.LoadImageSourceFromTiff(path), null);
            

            try
            {
                _token.ThrowIfCancellationRequested();
                string extension = Path.GetExtension(path).ToLowerInvariant();
                if (extension == ".tif" || extension == ".tiff")
                {
                    // Try to load TIFF via LibTiff
                    var bmp = TiffReader.LoadImageSourceFromTiff(path).GetAwaiter().GetResult();
                    if (bmp != null)
                    {
                        return (bmp, Array.Empty<byte>());
                    }
                    // If failed, fallback to Magick.NET
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

                        return (bmpSource, bmpBytes);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled
                throw;
            }
            catch (Exception ex)
            {
                // === Fallback через WIC ===

                try
                {
                    _token.ThrowIfCancellationRequested();
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


                    return (bitmap, []);
                }
                catch (OperationCanceledException)
                {
                    // Operation was cancelled
                    throw;
                }
                catch (Exception ex2)
                {
                    //ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");
                    throw new Exception($"Completely failed to load {path}: {ex2.Message}", ex2);
                    //return (null, Array.Empty<byte>());
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



        public void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi, bool overwrite = true, string? metadataJson = null)
        {
            if (!IsValidPath(path))
            {
                //return;
                throw new ArgumentException("Invalid file path.", nameof(path));
            }
            try
            {
                using var tiffSaver = new TiffWriter();
                tiffSaver.SaveTiff(stream, path, compression, dpi, overwrite, metadataJson);
            }
            
            catch (Exception ex)
            {
                // forward exception
                throw new Exception($"Error saving TIFF to {path}: {ex.Message}", ex);
            }
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

            IEnumerable<string> subFolderPaths;
            try
            {
                subFolderPaths = Directory.EnumerateDirectories(rootFolderPath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                var msg = $"Cannot enumerate sub-folders in '{rootFolderPath}'.{Environment.NewLine}{ex.Message}";
                System.Windows.MessageBox.Show(msg, "Folder Access Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var subFolders = new List<SourceImageFolder>();
            foreach (string subFolder in subFolderPaths)
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
            string[] files;
            try
            {
                files = Directory.EnumerateFiles(folderPath)
                                 .Where(file =>
                                            file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                            file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                            file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                            file.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) ||
                                            file.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase))
                                 .ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                var msg = $"Cannot enumerate files in '{folderPath}'.\n{ex.Message}";
                System.Windows.MessageBox.Show(msg, "Folder Access Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var sourceFolder = new SourceImageFolder
            {
                Path = folderPath,
                ParentPath = parentPath,
                Files = files
                                        .Select(f => new SourceImageFile
                                        {
                                            Path = f,
                                            // layout left if filename is ending with number and number is odd, otherwise right
                                            Layout = GetLayoutFromFileName(f)


                                        })
                                        .ToArray()
            };



            return sourceFolder;
        }

        private static SourceFileLayout? GetLayoutFromFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path); // e.g. "page001"

            if (string.IsNullOrEmpty(name))
                return SourceFileLayout.Right;

            int i = name.Length - 1;

            // идём с конца, пока цифры
            while (i >= 0 && char.IsDigit(name[i]))
                i--;

            int start = i + 1; // первая цифра в хвостовом числе
            string digits = (start < name.Length)
                ? name.Substring(start)    // всё от первой цифры до конца
                : string.Empty;

            if (digits.Length > 0 && int.TryParse(digits, out int num))
                return (num % 2 == 1) ? SourceFileLayout.Left : SourceFileLayout.Right;

            // нет числового суффикса → Right по умолчанию
            return null;
        }

        public SourceImageFolder[]? GetSubFoldersWithImagesPaths_FullTree(string rootFolderPath)
        {
            if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            {
                ErrorOccured?.Invoke($"Directory does not exist: {rootFolderPath}");
                return Array.Empty<SourceImageFolder>();
            }

            var result = new List<SourceImageFolder>();
            var stack = new Stack<string>();
            try
            {
                foreach (var d in Directory.EnumerateDirectories(rootFolderPath))
                    stack.Push(d);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                ErrorOccured?.Invoke($"Cannot enumerate root: {ex.Message}");
                return Array.Empty<SourceImageFolder>();
            }

            while (stack.Count > 0)
            {
                var dir = stack.Pop();

                // skip names that contain "processed" (case-insensitive)
                var name = Path.GetFileName(dir) ?? dir;
                if (name.IndexOf("processed", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // get files (your method), handle nulls
                var sf = GetImageFilesPaths(dir);
                if (sf != null && sf.Files?.Length > 0) result.Add(sf);

                // push children, ignoring inaccessible ones
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                        stack.Push(child);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
                {
                    Debug.WriteLine($"Can't enumerate children of {dir}: {ex.Message}");
                }
            }

            return result.ToArray();
        }


    }
}
