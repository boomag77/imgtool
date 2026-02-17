using BitMiracle.LibTiff.Classic;
using ImageMagick;
using ImgViewer.Interfaces;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Printing.IndexedProperties;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

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
            _magickGate.Dispose();
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
                var readSettings = new MagickReadSettings();
                readSettings.SetDefine("jpeg:ignore-exif-errors", "true");
                using var image = new MagickImage(fs, readSettings);

                try
                {
                    image.AutoOrient();
                }
                catch
                {
                    // Ignore EXIF issues and continue with the raw orientation
                }



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

        public bool TryResizeImagesIn(string[] folderPaths, ResizeParameters parameters, CancellationToken token, int maxWorkers, Action<int, int, string?>? progress = null)
        {
            var decoderTL = new ThreadLocal<WpfPixelDecoder>(() => new WpfPixelDecoder());
            var resizerTL = new ThreadLocal<ImageResizer>(() => new ImageResizer());
            try
            {
                bool IsSupportedImagePath(string path)
                {
                    var ext = Path.GetExtension(path);
                    if (string.IsNullOrWhiteSpace(ext))
                        return false;

                    return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                           ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
                }

                var filesToResize = new List<(string SourcePath, string DestFolder)>();
                foreach (var folderPath in folderPaths ?? Array.Empty<string>())
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(folderPath))
                        continue;
                    if (!Directory.Exists(folderPath))
                    {
                        ErrorOccured?.Invoke($"Directory does not exist: {folderPath}");
                        continue;
                    }

                    var parentDir = Path.GetDirectoryName(folderPath);
                    var resizedDirName = Path.GetFileName(folderPath) + "_resized";
                    var resizedFolder = parentDir != null ? Path.Combine(parentDir, resizedDirName) : folderPath + "_resized";
                    Directory.CreateDirectory(resizedFolder);

                    foreach (var imagePath in Directory.EnumerateFiles(folderPath))
                    {
                        if (!IsSupportedImagePath(imagePath))
                            continue;
                        filesToResize.Add((imagePath, resizedFolder));
                    }
                }

                int total = filesToResize.Count;
                int processed = 0;
                progress?.Invoke(0, total, null);



                Parallel.ForEach(
                    filesToResize,
                    new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = Math.Max(1, maxWorkers)
                    },
                    item =>
                    {
                        var decoder = decoderTL.Value!;
                        var resizer = resizerTL.Value!;
                        string imagePath = item.SourcePath;
                        var imageSource = LoadImageSource(imagePath, isBatch: false);
                        if (!decoder.TryDecodeToBgra32(imageSource.Item1,
                                                        out int w, out int h, out int stride,
                                                        out double dpiX, out double dpiY,
                                                        out var inOwner, out var fail))
                        {
                            ErrorOccured?.Invoke($"Decode fail: {imagePath}. Reason: {fail}");
                            int failProcessed = Interlocked.Increment(ref processed);
                            progress?.Invoke(failProcessed, total, imagePath);
                            return;
                        }

                        using (inOwner!)
                        {
                            if (!resizer.TryResizeImage(inOwner!.Memory, w, h, stride,
                                                        (int)parameters.MaxWidth, (int)parameters.MaxHeight, parameters.KeepAspectRatio, 4,
                                                        parameters.Method,
                                                        out var outOwner, out int outW, out int outH, out int outStride))
                            {
                                ErrorOccured?.Invoke($"Resize fail: {imagePath}. Reason: unknown.");
                                int failProcessed = Interlocked.Increment(ref processed);
                                progress?.Invoke(failProcessed, total, imagePath);
                                return;
                            }
                            using (outOwner!)
                            {
                                string originalFileName = Path.GetFileName(imagePath);
                                string originalExtension = Path.GetExtension(imagePath);
                                string resizedFilePath = Path.Combine(item.DestFolder, originalFileName);

                                var pixelSpan = outOwner!.Memory.Span.Slice(0, outStride * outH);
                                var bmpSource = CreateBitmapSourceFromBgra32(pixelSpan, outW, outH, outStride, dpiX, dpiY);

                                int jpegQuality = Math.Max(1, Math.Min(100, parameters.JpegQuality <= 0 ? 90 : parameters.JpegQuality));

                                BitmapEncoder encoder = originalExtension.ToLowerInvariant() switch
                                {
                                    ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
                                    ".png" => new PngBitmapEncoder(),
                                    ".bmp" => new BmpBitmapEncoder(),
                                    ".tif" or ".tiff" => new TiffBitmapEncoder(),
                                    _ => new PngBitmapEncoder() // default to PNG if somehow unsupported
                                };
                                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                                using var fs = new FileStream(resizedFilePath, FileMode.Create);
                                encoder.Save(fs);
                            }

                        }

                        int done = Interlocked.Increment(ref processed);
                        progress?.Invoke(done, total, imagePath);
                    });

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error resizing folders: {ex.Message}");
                return false;
            }
            finally
            {
                // Здесь можно выполнить очистку ресурсов, если это необходимо
                decoderTL.Dispose();
                resizerTL.Dispose();
            }
        }

        private static unsafe BitmapSource CreateBitmapSourceFromBgra32(
                                                                ReadOnlySpan<byte> pixels, int width, int height, int strideBytes,
                                                                double dpiX, double dpiY)
        {
            if (dpiX <= 0) dpiX = 96;
            if (dpiY <= 0) dpiY = 96;
            var wb = new WriteableBitmap(width, height, dpiX, dpiY, PixelFormats.Bgra32, null);

            fixed (byte* p = pixels)
            {
                wb.WritePixels(
                    new Int32Rect(0, 0, width, height),
                    (IntPtr)p,
                    strideBytes * height,   // bufferSize
                    strideBytes);
            }

            wb.Freeze(); // IMPORTANT for Parallel / cross-thread usage
            return wb;
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
                string ext = Path.GetExtension(path).ToLowerInvariant();
                bool batchDecodeTiff = isBatch && (ext == ".tif" || ext == ".tiff");
                using var fs = OpenReadShared(path);

                if (isBatch)
                {
                    if (!batchDecodeTiff)
                    {
                        // return only byte[] in batch mode
                        bmp = null;
                        bytes = File.ReadAllBytes(path); // raw file bytes for non-TIFF
                        return true; // raw file bytes
                    }
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
                //src = EnsureBgra32(src);

                // If src BGRa32 or Bgr24, we can keep it as is.
                if (src.Format != PixelFormats.Bgr24 && src.Format != PixelFormats.Bgra32)
                {
                    src = EnsureBgr24(src);
                }

                if (!src.IsFrozen) src.Freeze();
                if (isBatch)
                {
                    bmp = null;
                    bytes = EncodeToBmpBytes(src);
                    return true;
                }

                bmp = src;
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

        private bool TryLoadWithWicUri(string path, uint? decodePixelWidth, out BitmapSource? bmp, out string? fail)
        {
            bmp = null;
            fail = null;

            try
            {
                _token.ThrowIfCancellationRequested();

                var bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.CreateOptions = BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile;
                if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                {
                    bi.DecodePixelWidth = (int)decodePixelWidth.Value;
                }
                bi.UriSource = new Uri(path, UriKind.Absolute);
                bi.EndInit();

                BitmapSource src = bi;
                int orientation = ReadExifOrientation(bi.Metadata as BitmapMetadata);
                src = ApplyExifOrientation(src, orientation);

                if (src.Format != PixelFormats.Bgr24 && src.Format != PixelFormats.Bgra32)
                {
                    src = EnsureBgr24(src);
                }

                if (!src.IsFrozen) src.Freeze();
                bmp = src;
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

        private bool TryLoadWithGdi(bool isBatch, string path, uint? decodePixelWidth, out BitmapSource? bmp, out byte[] bytes, out string? fail)
        {
            bmp = null;
            bytes = Array.Empty<byte>();
            fail = null;

            try
            {
                _token.ThrowIfCancellationRequested();

                using var fs = OpenReadShared(path);
                using var img = System.Drawing.Image.FromStream(fs, useEmbeddedColorManagement: false, validateImageData: false);

                int targetWidth = 0;
                if (decodePixelWidth.HasValue && decodePixelWidth.Value > 0)
                {
                    targetWidth = (int)decodePixelWidth.Value;
                }

                using var bmpGdi = (targetWidth > 0 && img.Width > targetWidth)
                    ? new Bitmap(img, new System.Drawing.Size(targetWidth, (int)Math.Round(img.Height * (targetWidth / (double)img.Width))))
                    : new Bitmap(img);

                if (isBatch)
                {
                    using var ms = new MemoryStream();
                    bmpGdi.Save(ms, DrawingImageFormat.Bmp);
                    bytes = ms.ToArray();
                    return true;
                }

                IntPtr hBitmap = bmpGdi.GetHbitmap();
                try
                {
                    var src = Imaging.CreateBitmapSourceFromHBitmap(
                        hBitmap,
                        IntPtr.Zero,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    if (!src.IsFrozen) src.Freeze();
                    bmp = src;
                    return true;
                }
                finally
                {
                    DeleteObject(hBitmap);
                }
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

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
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

        private BitmapSource EnsureBgr24(BitmapSource src)
        {
            if (src.Format == PixelFormats.Bgr24)
                return src;

            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr24, null, 0);
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

        private bool TryLoadBytes(string path, out byte[] bytes, out string? fail)
        {
            bytes = Array.Empty<byte>();
            fail = null;
            try
            {
                _token.ThrowIfCancellationRequested();
                var loaded = LoadImageSource(path, isBatch: true);
                if (loaded.Item2 == null || loaded.Item2.Length == 0)
                {
                    fail = "No bytes returned by batch loader.";
                    return false;
                }

                bytes = loaded.Item2;
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                fail = ex.GetType().Name + ": " + ex.Message;
                ErrorOccured?.Invoke($"Failed to load bytes for {path}: {fail}");
                return false;
            }
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
                //Debug.WriteLine($"Loaded {path} via WIC.");
#endif

                return isBatch ? (null, wicBytes) : (wicBmp!, null);
            }

            string? wicUriFail = null;
            if (!isBatch && TryLoadWithWicUri(path, decodePixelWidth, out var wicUriBmp, out wicUriFail))
            {
                return (wicUriBmp!, null);
            }

            string? gdiFail = null;
            if (TryLoadWithGdi(isBatch, path, decodePixelWidth, out var gdiBmp, out var gdiBytes, out gdiFail))
            {
                return isBatch ? (null, gdiBytes) : (gdiBmp!, null);
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
                var details = $"WIC: {wicFail}; WIC-URI: {wicUriFail}; GDI: {gdiFail}; Magick: {ex2.Message}";
                ErrorOccured?.Invoke($"Completely failed to load {path}: {details}");

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

        // ImgWorkerPool uses this method to save CCITT TIFFs
        public void SaveTiff(TiffInfo tiffInfo, string path, SubSamplingMode subSamplingMode, bool overwrite = true, int jpegQuality = 75, string? metadataJson = null)
        {
            if (!IsValidPath(path))
            {
                //return;
                throw new ArgumentException("Invalid file path.", nameof(path));
            }
            try
            {
                if (tiffInfo.Compression == TiffCompression.CCITTG3 ||
                    tiffInfo.Compression == TiffCompression.CCITTG4)
                {
                    var ok = TiffWriter.SaveBinaryBytesAsCcitt(
                                    tiffInfo.Pixels,
                                    tiffInfo.StrideBytes,
                                    tiffInfo.BitsPerPixel,
                                    tiffInfo.Width,
                                    tiffInfo.Height,
                                    path,
                                    tiffInfo.Dpi,
                                    tiffInfo.Compression == TiffCompression.CCITTG3 ? Compression.CCITTFAX3 : Compression.CCITTFAX4,
                                    false,
                                    metadataJson
                    );
                    if (!ok)
                    {
                        throw new Exception("Failed to save CCITT TIFF, TiffWriter returned error.");
                    }
                }
                else
                {
                    var ok = TiffWriter.SaveNonCcittTiff(
                                    tiffInfo.Pixels,
                                    tiffInfo.Width,
                                    tiffInfo.Height,
                                    tiffInfo.BitsPerPixel,
                                    tiffInfo.Compression,
                                    tiffInfo.Dpi,
                                    path,
                                    overwrite, false,
                                    jpegQuality: Math.Clamp(jpegQuality, 1, 100),
                                    subSamplingMode,
                                    metadataJson
                    );
                    if (!ok)
                    {
                        throw new Exception("Failed to save TIFF, TiffWriter returned error.");
                    }
                }

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

        public void SaveStreamToFile(MemoryStream ms, string path)
        {
            try
            {
                using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
                {
                    if (ms.CanSeek)
                    {
                        ms.Position = 0;
                    }
                    ms.CopyTo(fileStream);
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save stream to file '{path}': {ex.Message}", ex);
            }

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

        public void SaveBytesToFile(ReadOnlySpan<byte> bytes, string path)
        {
            try
            {
                File.WriteAllBytes(path, bytes);
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to save bytes to file '{path}': {ex.Message}", ex);
            }

        }
    }
}
