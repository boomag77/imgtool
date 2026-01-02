using BitMiracle.LibTiff.Classic;
using ImageMagick;
using ImgViewer.Interfaces;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class FileProcessor : IFileProcessor, IDisposable
    {
        private CancellationToken _token;

        private static int _magickConfigured = 0;
        private static readonly SemaphoreSlim _magickGate = new(1, 1);


        public event Action<string> ErrorOccured;

        public FileProcessor(CancellationToken token)
        {
            _token = token;
        }

        public void Dispose()
        {
            // Dispose of unmanaged resources here if needed
        }

        private void ConfigureMagickOnce()
        {
            if (Interlocked.Exchange(ref _magickConfigured, 1) != 0)
                return;

            ResourceLimits.Thread = 1; // снижает шанс race/AV в batch
        }

        private (ImageSource?, byte[]?) LoadWithMagickFallback(bool isBatch, string path, uint? decodePixelWidth)
        {
            ConfigureMagickOnce();

            _magickGate.Wait(_token); // сериализуем fallback

            try
            {
                _token.ThrowIfCancellationRequested();

                using var fs = OpenReadShared(path);
                using var image = new MagickImage(fs);



                image.AutoOrient();



                // decodePixelWidth: делаем resize после чтения (самый надёжный путь)
                if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0 && image.Width > decodePixelWidth.Value)
                {
                    image.Resize((uint)decodePixelWidth.Value, 0); // aspect preserved
                }

                uint width = image.Width;
                uint height = image.Height;
                uint bpp = image.ChannelCount; // 3 или 4
                uint stride = width * bpp;

                var map = (bpp == 4) ? PixelMapping.BGRA : PixelMapping.BGR;

                using var pixels = image.GetPixels();
                byte[] pixelData = pixels.ToByteArray(0, 0, width, height, map);

                if (isBatch)
                {
                    return (null, pixelData);
                }


                var bmpSource = BitmapSource.Create(
                    (int)width, (int)height, 96, 96,
                    bpp == 4 ? PixelFormats.Bgra32 : PixelFormats.Bgr24,
                    null, pixelData, (int)stride);

                bmpSource.Freeze();

                // для совместимости оставим BMP bytes
                byte[] bytes = Array.Empty<byte>();
                if (isBatch)
                {
                    bytes = EncodeToBmpBytes(bmpSource);
                    bmpSource = null; // освобождаем память, если не нужно
                }
                    

                return isBatch ? (null, bytes) : (bmpSource, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Magick.NET failed to load {path}: {ex.Message}");
                throw;
            }
            finally
            {
                _magickGate.Release();
            }
        }


        private FileStream OpenReadShared(string path) =>
                                    new FileStream(
                                                    path,
                                                    FileMode.Open,
                                                    FileAccess.Read,
                                                    FileShare.ReadWrite | FileShare.Delete,   // важно для batch
                                                    4096,
                                                    FileOptions.SequentialScan);

        private bool TryLoadWithWic(bool isBatch, string path, uint? decodePixelWidth,
                                  out BitmapSource? bmp, out byte[] bytes, out string? fail)
        {
            bmp = null;
            bytes = Array.Empty<byte>();
            fail = null;

            try
            {
                _token.ThrowIfCancellationRequested();
                using var fs = OpenReadShared(path);

                if (isBatch)
                {
                    // return only byte[] in batch mode
                    bmp = null;
                    using var ms = new MemoryStream();
                    fs.CopyTo(ms);

                    bytes = ms.ToArray();
                    return true; // raw file bytes

                }

                // Важно: OnLoad, чтобы не держать файл
                var decoder = BitmapDecoder.Create(
                    fs,
                    BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.OnLoad);

                var frame = decoder.Frames[0];
                BitmapSource src = frame;

                // 1) AutoOrient через EXIF Orientation
                int orientation = ReadExifOrientation(frame.Metadata as BitmapMetadata);
                src = ApplyExifOrientation(src, orientation);

                // 2) Downscale по ширине (если нужно)
                src = ApplyDecodeWidth(src, decodePixelWidth);

                // 3) Приводим к удобному формату для WPF (и чтобы PNG alpha не потерять)
                src = EnsureBgra32(src);

                src.Freeze();
                bmp = src;

                // Если тебе реально нужен byte[] (как сейчас): оставим BMP-энкодинг для совместимости
                if (isBatch)
                {
                    bytes = EncodeToBmpBytes(src);
                }
                    

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static int ReadExifOrientation(BitmapMetadata? meta)
        {
            if (meta == null) return 1;

            object? v = null;

            // JPEG query
            try { v = meta.GetQuery("/app1/ifd/{ushort=274}"); } catch { }

            // TIFF query (на всякий случай)
            if (v == null)
                try { v = meta.GetQuery("/ifd/{ushort=274}"); } catch { }

            // Обычно ushort
            if (v is ushort u) return u;
            if (v is short s) return s;

            return 1;
        }

        private BitmapSource ApplyExifOrientation(BitmapSource src, int orientation)
        {
            // Нормальный кейс
            if (orientation <= 1 || orientation > 8) return src;

            BitmapSource r = src;

            // Операции (простые и читаемые): цепочкой
            BitmapSource Rot90(BitmapSource s) => new TransformedBitmap(s, new RotateTransform(90));
            BitmapSource Rot180(BitmapSource s) => new TransformedBitmap(s, new RotateTransform(180));
            BitmapSource Rot270(BitmapSource s) => new TransformedBitmap(s, new RotateTransform(270));
            BitmapSource FlipH(BitmapSource s) => new TransformedBitmap(s, new ScaleTransform(-1, 1));
            BitmapSource FlipV(BitmapSource s) => new TransformedBitmap(s, new ScaleTransform(1, -1));

            // Маппинг на основе стандартных операций:
            // 2 flip horizontally, 3 rotate 180, 4 flip vertically,
            // 5 rotate 90 CW + flip horizontally,
            // 6 rotate 90 CW,
            // 7 rotate 90 CW + flip vertically,
            // 8 rotate 270 CW
            // (такая таблица часто используется как “de-facto” для auto-orient)
            // :contentReference[oaicite:4]{index=4}
            switch (orientation)
            {
                case 2: r = FlipH(r); break;
                case 3: r = Rot180(r); break;
                case 4: r = FlipV(r); break;
                case 5: r = FlipH(Rot90(r)); break;
                case 6: r = Rot90(r); break;
                case 7: r = FlipV(Rot90(r)); break;
                case 8: r = Rot270(r); break;
            }

            return r;
        }

        private BitmapSource ApplyDecodeWidth(BitmapSource src, uint? decodePixelWidth)
        {
            if (!decodePixelWidth.HasValue || decodePixelWidth.Value == 0)
                return src;

            int w = src.PixelWidth;
            if (w <= 0) return src;

            double target = decodePixelWidth.Value;
            double scale = target / w;

            // не апскейлим (обычно это бессмысленно)
            if (scale <= 0 || scale >= 1.0) return src;

            return new TransformedBitmap(src, new ScaleTransform(scale, scale));
        }

        private BitmapSource EnsureBgra32(BitmapSource src)
        {
            if (src.Format == PixelFormats.Bgra32)
                return src;

            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            conv.Freeze();
            return conv;
        }

        private byte[] EncodeToBmpBytes(BitmapSource src)
        {
            using var ms = new MemoryStream();
            var enc = new BmpBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(src));
            enc.Save(ms);
            return ms.ToArray();
        }





        public (ImageSource?, byte[]?) LoadImageSource(string path, bool isBatch, uint? decodePixelWidth = null)
        {


            _token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension == ".tif" || extension == ".tiff")
            {
                if (isBatch)
                {
                    // In batch mode, return raw bytes
                    var tiffBytes = TiffReader.LoadPixelsFromTiff(path).GetAwaiter().GetResult();
                    if (tiffBytes != null)
                    {
                        return (null, tiffBytes);
                    }
                }
                // Try to load TIFF via LibTiff
                var bmp = TiffReader.LoadImageSourceFromTiff(path).GetAwaiter().GetResult();
                if (bmp != null)
                {
                    if (bmp is BitmapSource bs && !bs.IsFrozen)
                        bs.Freeze();
                    return (bmp, null);
                }
                // If failed, fallback to Magick.NET
            }

            if (TryLoadWithWic(isBatch, path, decodePixelWidth, out var wicBmp, out var wicBytes, out var wicFail))
            {
#if DEBUG
                Debug.WriteLine($"Loaded {path} via WIC.");
#endif
                return isBatch ? (null, wicBytes) : (wicBmp!, null);
            }


            try
            {
                Debug.WriteLine($"WIC failed to load {path}: {wicFail}. Falling back to Magick.NET.");
                var (bmp, bytes) = LoadWithMagickFallback(isBatch, path, decodePixelWidth);
                return isBatch ? (null, bytes) : (bmp, null);

            }
            catch (OperationCanceledException)
            {
                // Operation was cancelled
                throw;
            }
            catch (Exception ex2)
            {
                ErrorOccured?.Invoke($"Completely failed to load {path}: {ex2.Message}");

                //throw new Exception($"Completely failed to load {path}: {ex2.Message}", ex2);
                return (null, null);
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

        public void SaveTiff(TiffInfo tiffInfo, string path, bool overwrite = true, string? metadataJson = null)
        {
            if (!IsValidPath(path))
            {
                //return;
                throw new ArgumentException("Invalid file path.", nameof(path));
            }
            try
            {
                //using var tiffSaver = new TiffWriter();
                TiffWriter.SaveBinaryBytesAsCcitt(
                    tiffInfo.Pixels,
                    tiffInfo.StrideBytes,
                    tiffInfo.BitsPerPixel,
                    tiffInfo.Width,
                    tiffInfo.Height,
                    path,
                    tiffInfo.Dpi,
                    tiffInfo.Compression == TiffCompression.CCITTG3 ? Compression.CCITTFAX3 : Compression.CCITTFAX4,
                    false,
                    metadataJson);
            }
            catch (OperationCanceledException)
            {
                // forward cancellation
                throw;
            }
            catch (Exception ex)
            {
                // forward exception
                throw;
            }
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
                TiffWriter.SaveTiff(stream, path, compression, dpi, overwrite, metadataJson);
            }
            catch (OperationCanceledException)
            {
                // forward cancellation
                throw;
            }
            catch (Exception ex)
            {
                // forward exception

                throw;

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

        public SourceImageFolder[] GetSubFoldersWithImagesPaths(string rootFolderPath, CancellationToken token)
        {
            if (token.IsCancellationRequested) return Array.Empty<SourceImageFolder>();
            if (!Directory.Exists(rootFolderPath))
            {
                ErrorOccured?.Invoke($"Directory does not exist: {rootFolderPath}");
                return Array.Empty<SourceImageFolder>();
            }

            IEnumerable<string> subFolderPaths;
            // ignore Inaccessible folders

            var subFolders = new List<SourceImageFolder>();
            try
            {
                subFolderPaths = Directory.EnumerateDirectories(rootFolderPath);
                foreach (string subFolder in subFolderPaths)
                {
                    if (token.IsCancellationRequested) return Array.Empty<SourceImageFolder>();
                    try
                    {
                        var sub = GetImageFilesPaths(subFolder, token);
                        if (sub == null || sub.Files.Length == 0) continue;
                        subFolders.Add(sub);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        var msg = $"Cannot process sub-folder '{subFolder}'.{Environment.NewLine}{ex.Message}";
                        ErrorOccured?.Invoke(msg);
                    }
                }

            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                var msg = $"Cannot enumerate sub-folders in '{rootFolderPath}'.{Environment.NewLine}{ex.Message}";
                ErrorOccured?.Invoke(msg);
                return Array.Empty<SourceImageFolder>();
            }



            return subFolders.ToArray();
        }

        public IEnumerable<string> EnumerateSubFolderPaths(string rootFolderPath, bool fullTree, CancellationToken token)
        {
            if (token.IsCancellationRequested) yield break;

            if (string.IsNullOrWhiteSpace(rootFolderPath) || !Directory.Exists(rootFolderPath))
            {
                ErrorOccured?.Invoke($"Directory does not exist: {rootFolderPath}");
                yield break;
            }

            // fullTree=false: только 1 уровень
            if (!fullTree)
            {
                IEnumerable<string> top;
                try
                {
                    top = Directory.EnumerateDirectories(rootFolderPath);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsFsEnumerationException(ex))
                {
                    ErrorOccured?.Invoke($"Cannot enumerate directories in '{rootFolderPath}': {ex.Message}");
                    yield break;
                }

                foreach (var dir in top)
                {
                    if (token.IsCancellationRequested) yield break;
                    if (IsSkippableDir(dir)) continue;
                    yield return dir;
                }

                yield break;
            }

            // fullTree=true: ручной DFS, best-effort
            var stack = new Stack<string>();

            // Сначала кладём первый уровень
            try
            {
                foreach (var d in Directory.EnumerateDirectories(rootFolderPath))
                {
                    if (token.IsCancellationRequested) yield break;
                    if (IsSkippableDir(d)) continue;
                    stack.Push(d);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsFsEnumerationException(ex))
            {
                ErrorOccured?.Invoke($"Cannot enumerate directories in '{rootFolderPath}': {ex.Message}");
                yield break;
            }

            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested) yield break;

                var dir = stack.Pop();
                yield return dir;

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        if (token.IsCancellationRequested) yield break;
                        if (IsSkippableDir(child)) continue;
                        stack.Push(child);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (IsFsEnumerationException(ex))
                {
                    // best-effort: пропускаем ветку, но продолжаем
                    //ErrorOccured?.Invoke($"Cannot enumerate sub-folders in '{dir}': {ex.Message}");
                    ErrorOccured?.Invoke($"SKIP_ENUM: '{dir}' ({ex.GetType().Name}) {ex.Message}");
                    continue;
                }
            }
        }

        private static bool IsFsEnumerationException(Exception ex) =>
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is PathTooLongException ||
            ex is DirectoryNotFoundException ||
            ex is ArgumentException ||
            ex is NotSupportedException ||
            ex is SecurityException;

        private static bool IsSkippableDir(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);

                // junction/symlink (ReparsePoint) -> предотвращает циклы
                if ((attr & FileAttributes.ReparsePoint) != 0) return true;

                // system dirs часто inaccessible / мусорные
                if ((attr & FileAttributes.System) != 0) return true;
            }
            catch
            {
                // если даже атрибуты не читаются — считаем inaccessible
                return true;
            }

            return false;
        }




        public SourceImageFolder? GetImageFilesPaths(string folderPath, CancellationToken token)
        {
            if (token.IsCancellationRequested) return null;
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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                var msg = $"Cannot enumerate files in '{folderPath}'.\n{ex.Message}";
                ErrorOccured?.Invoke(msg);
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


                                        }).ToArray()
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

        public SourceImageFolder[] GetSubFoldersWithImagesPaths_FullTree(string rootFolderPath, CancellationToken token)
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
                {
                    if (token.IsCancellationRequested)
                        return Array.Empty<SourceImageFolder>();
                    stack.Push(d);
                }

            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
            {
                ErrorOccured?.Invoke($"Cannot enumerate root: {ex.Message}");
                return Array.Empty<SourceImageFolder>();
            }

            while (stack.Count > 0)
            {
                if (token.IsCancellationRequested)
                    return Array.Empty<SourceImageFolder>();
                var dir = stack.Pop();

                // skip names that contain "processed" (case-insensitive)
                var name = Path.GetFileName(dir) ?? dir;
                if (name.IndexOf("processed", StringComparison.OrdinalIgnoreCase) >= 0)
                    continue;

                // get files (your method), handle nulls
                var sf = GetImageFilesPaths(dir, token);
                if (sf != null && sf.Files?.Length > 0) result.Add(sf);

                // push children, ignoring inaccessible ones
                try
                {
                    foreach (var child in Directory.EnumerateDirectories(dir))
                    {
                        if (token.IsCancellationRequested)
                            return Array.Empty<SourceImageFolder>();
                        stack.Push(child);
                    }

                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is PathTooLongException)
                {
                    ErrorOccured?.Invoke($"Cannot enumerate sub-folders in '{dir}': {ex.Message}");
                    //Debug.WriteLine($"Can't enumerate children of {dir}: {ex.Message}");
                }
            }

            return result.ToArray();
        }


    }
}
