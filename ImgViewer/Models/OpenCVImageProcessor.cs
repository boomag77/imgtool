using ImgViewer.Interfaces;
using ImgViewer.Models.Onnx;
using OpenCvSharp;
using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static ImgViewer.Models.BordersRemover;

namespace ImgViewer.Models
{
    public class OpenCvImageProcessor : IImageProcessor, IDisposable
    {
        private byte[]? _binaryBuffer;

        private Mat _currentImage;
        //private Scalar _pageColor;
        //private Scalar _borderColor;
        //private Mat _blurred;
        private readonly IAppManager _appManager;

        private CancellationToken _token;
        //private readonly CancellationTokenSource _onnxCts;

        private readonly object _imageLock = new();
        private readonly object _commandLock = new();
        private readonly object _splitLock = new();

        private readonly DocBoundaryModel? _docBoundaryModel;
        private Mat[]? _splitWorkingImages;

        private Mat WorkingImage
        {
            get
            {
                lock (_imageLock)
                    return _currentImage.Clone();
            }
            set
            {
                if (value == null || value.Empty()) return;
                Mat old;
                var previewSnap = new Mat();
                lock (_imageLock)
                {
                    old = _currentImage;
                    _currentImage = value;
                    if (_appManager != null)
                        previewSnap = value.Clone();
                }
                old?.Dispose();
                if (_appManager == null) { previewSnap.Dispose(); return; }
                try
                {
                    _appManager.SetBmpImageOnPreview(MatToBitmapSource(previewSnap));
                }
                finally
                {
                    previewSnap.Dispose();
                }

            }
        }


        //Mat Load(BitmapSource bmp) => BitmapSourceToMat(bmp);

        //public void SetSpanData(ReadOnlySpan<byte> rawPixels)
        //{
        //    try
        //    {
        //        ClearSplitResults();
        //        Mat? mat = Cv2.ImDecode(rawPixels, ImreadModes.Color);
        //        if (mat == null || mat.Empty())
        //        {
        //            mat?.Dispose();
        //            return;
        //        }
        //        WorkingImage = mat;
        //    }
        //    catch (OperationCanceledException)
        //    {
        //    }
        //    catch (Exception ex)
        //    {
        //        ErrorOccured?.Invoke($"Failed to set Current Image from span: {ex.Message}");
        //    }
        //}

        public object CurrentImage
        {
            set
            {
                try
                {
                    ClearSplitResults();
                    // if value is raw pixels
                    Mat? mat = null;
                    if (value is byte[] rawPixels)
                    {
                        mat = Cv2.ImDecode(rawPixels, ImreadModes.Color);
                    }
                    else if (value is BitmapSource bmp)
                    {
                        mat = BitmapSourceToMat(bmp);
                    }
                    else if (value is ReadOnlyMemory<byte> rom)
                    {
                        mat = Cv2.ImDecode(rom.Span, ImreadModes.Color);
                    }
                    else
                    {
                        ErrorOccured?.Invoke($"Unsupported type for CurrentImage: {value.GetType()}");
                        return;
                    }
                    if (mat == null || mat.Empty())
                    {
                        mat?.Dispose();
                        return;
                    }

                    WorkingImage = mat;
                }

                catch (OperationCanceledException)
                {

                }
                catch (Exception ex)
                {
                    ErrorOccured?.Invoke($"Failed to set Current Image: {ex.Message}");
                }
            }
        }




        public OpenCvImageProcessor(IAppManager appManager, CancellationToken token, int cvNumThreads = 0, bool needDocBoundaryModel = true)
        {
            _currentImage = new Mat();
            _appManager = appManager;
            _token = token;
            //_onnxCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (appManager == null)
                Cv2.SetNumThreads(cvNumThreads); // force single-threaded if no app manager (e.g. unit tests)
            //Debug.WriteLine($"[OpenCvImageProcessor] Initialized with Cv2.GetNumThreads()={Cv2.GetNumThreads()}");

            try
            {
                var baseDir = AppDomain.CurrentDomain.BaseDirectory;
                var modelPath = Path.Combine(baseDir, "Models", "ML", "model.onnx");

                if (!File.Exists(modelPath) && needDocBoundaryModel)
                {
                    Debug.WriteLine($"[DocBoundaryModel] Model file not found: {modelPath}");
                    _docBoundaryModel = null;
                    return;
                }

                //var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                //var onnxToken = linkedCts.Token;
                _docBoundaryModel = needDocBoundaryModel ? new DocBoundaryModel(modelPath) : null;
            }
            catch (Exception ex)
            {
                var msg = $"[DocBoundaryModel] Failed to initialize: {ex}";
                ErrorOccured?.Invoke(msg);
                Debug.WriteLine(msg);
                _docBoundaryModel = null;
            }

        }

        //private byte[] RentBinaryBuffer(int size)
        //{
        //    if (_binaryBuffer == null || _binaryBuffer.Length < size)
        //        _binaryBuffer = new byte[size];
        //    return _binaryBuffer;
        //}

        private Mat BitmapSourceToMat(BitmapSource src)
        {
            _token.ThrowIfCancellationRequested();
            if (src == null) throw new ArgumentNullException(nameof(src));

            // Нормализуем формат: убираем premultiplied alpha и приводим к удобному формату
            if (src.Format == PixelFormats.Pbgra32)
                src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);

            PixelFormat copyFormat;
            bool viewIsBGRA = false;

            if (src.Format == PixelFormats.Bgr24)
                copyFormat = PixelFormats.Bgr24;
            else if (src.Format == PixelFormats.Bgra32)
            {
                copyFormat = PixelFormats.Bgra32;
                viewIsBGRA = true;
            }
            else if (src.Format == PixelFormats.Gray8)
                copyFormat = PixelFormats.Gray8;
            else
                copyFormat = PixelFormats.Bgr24; // fallback: WPF сделает конвертацию

            if (src.Format != copyFormat)
                src = new FormatConvertedBitmap(src, copyFormat, null, 0);

            int w = src.PixelWidth;
            int h = src.PixelHeight;
            if (w == 0 || h == 0) return new Mat();

            int stride = (w * copyFormat.BitsPerPixel + 7) / 8;
            long total = (long)stride * h;
            if (total > int.MaxValue) throw new NotSupportedException("Image too large");

            int byteCount = (int)total;
            byte[] buffer = ArrayPool<byte>.Shared.Rent(byteCount);
            //GCHandle? handle = null;

            try
            {
                _token.ThrowIfCancellationRequested();
                src.CopyPixels(buffer, stride, 0);

                //handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                //IntPtr ptr = handle.Value.AddrOfPinnedObject();

                Mat result = CreateMatFromBuffer(buffer, w, h, stride, copyFormat);


                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Failed while converting Bitmap Source to Mat: {ex.Message}");
                return new Mat();
            }
            finally
            {
                //if (handle.HasValue && handle.Value.IsAllocated) handle.Value.Free();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private Mat CreateMatFromBuffer(byte[] buffer, int width, int height, int srcStride, PixelFormat copyFormat)
        {
            _token.ThrowIfCancellationRequested();
            //var bufferCopy = buffer.Clone();
            try
            {
                if (copyFormat == PixelFormats.Bgr24)
                {
                    var mat = new Mat(height, width, MatType.CV_8UC3);
                    IntPtr dstPtr = mat.Data;
                    int dstStride = (int)mat.Step();
                    int copyRowBytes = Math.Min(width * 3, srcStride);
                    for (int r = 0; r < height; r++)
                    {
                        _token.ThrowIfCancellationRequested();
                        Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);
                    }
                    return mat;
                }

                if (copyFormat == PixelFormats.Bgra32)
                {
                    using var mat4 = new Mat(height, width, MatType.CV_8UC4);
                    IntPtr dstPtr = mat4.Data;
                    int dstStride = (int)mat4.Step();
                    int copyRowBytes = Math.Min(width * 4, srcStride);
                    for (int r = 0; r < height; r++)
                    {
                        _token.ThrowIfCancellationRequested();
                        Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);
                    }
                    var result = new Mat();
                    Cv2.CvtColor(mat4, result, ColorConversionCodes.BGRA2BGR);
                    return result;
                }

                if (copyFormat == PixelFormats.Gray8)
                {
                    using var mat1 = new Mat(height, width, MatType.CV_8UC1);
                    IntPtr dstPtr = mat1.Data;
                    int dstStride = (int)mat1.Step();
                    int copyRowBytes = Math.Min(width, srcStride);
                    for (int r = 0; r < height; r++)
                    {
                        _token.ThrowIfCancellationRequested();
                        Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);
                    }
                    var result = new Mat();
                    Cv2.CvtColor(mat1, result, ColorConversionCodes.GRAY2BGR);
                    return result;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error creating Mat from buffer: {ex.Message}");
                return new Mat();
            }
            return new Mat();
        }


        public void Dispose()
        {
            _docBoundaryModel?.Dispose();
            //_onnxCts.Dispose();
            lock (_imageLock)
            {
                _currentImage?.Dispose();
                _currentImage = null;
            }
            ClearSplitResults();


        }

        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;


        public bool TryGetStreamForSave(ImageFormat imageFormat, out MemoryStream? ms, out string error)
        {

            var img = WorkingImage; // clone for thread safety
            if (img == null || img.Empty())
            {
                ms = null;
                error = "Can't create stream for saving: WorkingImage is null or empty";
                return false;
            }
            error = string.Empty;
            switch (imageFormat)
            {
                case ImageFormat.Bmp:
                    {
                        byte[] bmpData = img.ImEncode(".bmp");
                        ms = new MemoryStream(bmpData, 0, bmpData.Length);
                        break;
                    }
                case ImageFormat.Jpeg:
                    {

                        byte[] jpgData = img.ImEncode(".jpg");
                        ms = new MemoryStream(jpgData, 0, jpgData.Length);
                        break;
                    }
                case ImageFormat.Png:
                    {
                        byte[] pngData = img.ImEncode(".png");
                        ms = new MemoryStream(pngData, 0, pngData.Length);
                        break;
                    }
                case ImageFormat.Pdf:
                    {
                        error = "PDF saving is not implemented yet.";
                        ms = null;
                        return false;
                    }
                default:
                    ms = null;
                    error = $"Unsupported image format for saving: {imageFormat}";
                    return false;
            }
            return true;
        }


        public TiffInfo GetTiffInfo(TiffCompression compression, int dpi)
        {
            var tiffInfo = new TiffInfo();
            switch (compression)
            {
                case TiffCompression.CCITTG3:
                case TiffCompression.CCITTG4:
                    // for CCITTG3/G4 we need binary image
                    {
                        var (binPixels, width, height, strideBytes, bitsPerPixel) = GetBinPixelsFromMat(compression, photometricMinIsWhite: false, useOtsu: false);
                        tiffInfo.Pixels = binPixels;
                        tiffInfo.StrideBytes = strideBytes;
                        tiffInfo.Width = width;
                        tiffInfo.Height = height;
                        tiffInfo.Dpi = dpi;
                        tiffInfo.BitsPerPixel = bitsPerPixel; // binary
                        tiffInfo.Compression = compression;
                        tiffInfo.IsMultiPage = false;
                        break;
                    }
                case TiffCompression.JPEG:
                case TiffCompression.LZW:
                case TiffCompression.Deflate:
                case TiffCompression.PackBits:
                case TiffCompression.None:
                    {
                        var (pixels, width, height, bitsPerPixel) = GetPixelsFromMat();
                        tiffInfo.Pixels = pixels;
                        tiffInfo.Width = width;
                        tiffInfo.Height = height;
                        tiffInfo.Dpi = dpi;
                        tiffInfo.BitsPerPixel = bitsPerPixel; // updated to use lzwBitsPerPixel correctly
                        tiffInfo.Compression = compression; // added line to set compression for LZW
                        tiffInfo.IsMultiPage = false;
                        break;
                    }
                default:
                    // for other compressions we can use grayscale or color
                    tiffInfo.Compression = compression; // added line to set compression for default case
                    break;
            }
            return tiffInfo;
        }

        private (byte[] pixels, int width, int height, int bitsPerPixel) GetPixelsFromMat()
        {

            using var src = WorkingImage; // cloned
            if (src == null || src.Empty())
                throw new InvalidOperationException("src Mat is null or empty");
            using var tmpDepth = new Mat();
            using var tmpColor = new Mat();
            Mat work = src;
            if (src.Depth() != MatType.CV_8U)
            {
                work.ConvertTo(tmpDepth, MatType.CV_8U);
                work = tmpDepth;
            }

            if (work.Type() == MatType.CV_8UC3)
            {
                Cv2.CvtColor(work, tmpColor, ColorConversionCodes.BGR2RGB);
                work = tmpColor;
            }
            else if (work.Type() == MatType.CV_8UC4)
            {
                Cv2.CvtColor(work, tmpColor, ColorConversionCodes.BGRA2RGBA);
                work = tmpColor;
            }
            int width = work.Cols;
            int height = work.Rows;
            int channels = work.Channels();
            int bitsPerPixel = channels * 8;
            int rowBytes = checked(width * channels);
            int bufferSize = checked(rowBytes * height);
            var pixels = new byte[bufferSize];
            if (work.Step() == width * channels && work.IsContinuous())
            {
                // можно скопировать сразу весь буфер
                Marshal.Copy(work.Ptr(0), pixels, 0, bufferSize);
                return (pixels, width, height, bitsPerPixel);
            }
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(work.Ptr(y), pixels, y * width * channels, width * channels);
            }
            return (pixels, width, height, bitsPerPixel);
        }

        private (byte[] binPixels, int width, int height, int strideBytes, int bitsPerPixel) GetBinPixelsFromMat(TiffCompression compression,
                                                                             bool photometricMinIsWhite = false,
                                                                             bool useOtsu = true,
                                                                             double manualThreshold = 128)
        {
            using var src = WorkingImage; // cloned
            if (src == null || src.Empty())
                throw new InvalidOperationException("src Mat is null or empty");

            using var gray = ToGray8u(src);

            using var bin = new Mat();
            if (IsBinaryMat(gray))
            {
                gray.CopyTo(bin);
            }
            else
            {
                var thrType = photometricMinIsWhite
                    ? ThresholdTypes.BinaryInv   // white bg -> 0, black ink -> 255
                    : ThresholdTypes.Binary;     // black ink -> 0, white bg -> 255

                if (useOtsu)
                    thrType |= ThresholdTypes.Otsu;

                Cv2.Threshold(gray, bin, useOtsu ? 0 : manualThreshold, 255, thrType);
            }



            int width = bin.Cols;
            int height = bin.Rows;
            int strideBytes = width;

            if (compression == TiffCompression.CCITTG3 || compression == TiffCompression.CCITTG4)
            {
                // для G3/G4 нужно 1-битное изображение
                strideBytes = (width + 7) >> 3;
                var packed = new byte[strideBytes * height];
                unsafe
                {
                    for (int y = 0; y < height; y++)
                    {
                        byte* srcRow = (byte*)bin.Ptr(y).ToPointer();
                        int dstOff = y * strideBytes;

                        for (int x = 0; x < width; x++)
                        {
                            // ставим бит = 1 если пиксель != 0 (обычно 255)
                            if (srcRow[x] != 0)
                                packed[dstOff + (x >> 3)] |= (byte)(0x80 >> (x & 7));
                        }
                    }
                }
                return (packed, width, height, strideBytes, 1);
            }



            int bufferSize = width * height;

            var binPixels = new byte[bufferSize];

            //var binPixels = new byte[width * height];

            // важно: копируем ровно width байт на строку, игнорируя bin.Step()
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(bin.Ptr(y), binPixels, y * width, width);
            }

            return (binPixels, width, height, strideBytes, 8);
        }

        private Mat ToGray8u(Mat src)
        {
            // вернём новый Mat (caller Dispose через using выше)
            if (src.Type() == MatType.CV_8UC1)
                return src.Clone();

            var gray = new Mat();

            if (src.Channels() == 3)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
            else
                throw new NotSupportedException($"Unsupported channels: {src.Channels()}");

            if (gray.Type() != MatType.CV_8UC1)
                gray.ConvertTo(gray, MatType.CV_8UC1);

            return gray;
        }


        private static bool IsBinaryMat(Mat gray)
        {
            if (gray == null || gray.Empty()) return false;
            if (gray.Type() != MatType.CV_8UC1) return false;

            using var nonBinary = new Mat();
            Cv2.InRange(gray, new Scalar(1), new Scalar(254), nonBinary);
            return Cv2.CountNonZero(nonBinary) == 0;
        }


        public (Vec3d pageColorNorm, Vec3d borderColorNorm) AnalyzePageAndBorderColorsSimple(
            Mat src,
            int borderStripPx = 32)   // ширина полосы по краям для оценки бордюра; уменьшите для мелких изображений
        {
            if (src == null || src.Empty())
                throw new ArgumentException("src is null or empty");

            // Приведём к CV_8UC3 (BGR) если нужно
            Mat img = src;
            Mat tmp = null;
            if (img.Type() == MatType.CV_8UC3)
            {
                // ничего не делаем
            }
            else if (img.Type() == MatType.CV_8UC1)
            {
                tmp = new Mat();
                Cv2.CvtColor(img, tmp, ColorConversionCodes.GRAY2BGR);
                img = tmp;
            }
            else if (img.Type() == MatType.CV_8UC4)
            {
                tmp = new Mat();
                Cv2.CvtColor(img, tmp, ColorConversionCodes.BGRA2BGR);
                img = tmp;
            }
            else
            {
                throw new ArgumentException("EstimatePageColor expects 1, 3, or 4 channel Mat", nameof(src));
            }

            try
            {
                int W = img.Cols;
                int H = img.Rows;

                // центральная область ~ 1/3 x 1/3
                int cw = Math.Max(1, W / 3);
                int ch = Math.Max(1, H / 3);
                int cx = Math.Max(0, (W - cw) / 2);
                int cy = Math.Max(0, (H - ch) / 2);
                var centralRect = new Rect(cx, cy, cw, ch);
                using var central = new Mat(img, centralRect);

                // Усреднённый цвет центральной области (Scalar: B,G,R,[A])
                Scalar meanCentral = Cv2.Mean(central);
                Vec3d page = new Vec3d(meanCentral.Val0 / 255.0, meanCentral.Val1 / 255.0, meanCentral.Val2 / 255.0);

                // полосы по краям (clamp размера)
                int strip = Math.Max(1, Math.Min(borderStripPx, Math.Min(W, H) / 4));

                using var top = new Mat(img, new Rect(0, 0, W, strip));
                using var bottom = new Mat(img, new Rect(0, Math.Max(0, H - strip), W, Math.Min(strip, H)));
                using var left = new Mat(img, new Rect(0, 0, strip, H));
                using var right = new Mat(img, new Rect(Math.Max(0, W - strip), 0, Math.Min(strip, W), H));

                // усреднённый цвет бордюров по площадям
                double totalPixels = (double)(top.Rows * top.Cols + bottom.Rows * bottom.Cols + left.Rows * left.Cols + right.Rows * right.Cols);
                double sumB = 0, sumG = 0, sumR = 0;

                Scalar mTop = Cv2.Mean(top);
                sumB += mTop.Val0 * top.Rows * top.Cols;
                sumG += mTop.Val1 * top.Rows * top.Cols;
                sumR += mTop.Val2 * top.Rows * top.Cols;

                Scalar mBottom = Cv2.Mean(bottom);
                sumB += mBottom.Val0 * bottom.Rows * bottom.Cols;
                sumG += mBottom.Val1 * bottom.Rows * bottom.Cols;
                sumR += mBottom.Val2 * bottom.Rows * bottom.Cols;

                Scalar mLeft = Cv2.Mean(left);
                sumB += mLeft.Val0 * left.Rows * left.Cols;
                sumG += mLeft.Val1 * left.Rows * left.Cols;
                sumR += mLeft.Val2 * left.Rows * left.Cols;

                Scalar mRight = Cv2.Mean(right);
                sumB += mRight.Val0 * right.Rows * right.Cols;
                sumG += mRight.Val1 * right.Rows * right.Cols;
                sumR += mRight.Val2 * right.Rows * right.Cols;

                if (totalPixels <= 0) totalPixels = 1; // safety

                var border = new Vec3d((sumB / totalPixels) / 255.0, (sumG / totalPixels) / 255.0, (sumR / totalPixels) / 255.0);

                return (page, border);
            }
            finally
            {
                tmp?.Dispose();
            }
        }

        private int ClampRound255(double v)
        {
            int iv = (int)Math.Round(v * 255.0);
            if (iv < 0) iv = 0;
            if (iv > 255) iv = 255;
            return iv;
        }

        private Scalar ScalarFromNormalized(Vec3d normBgr)
        {
            int b = ClampRound255(normBgr.Item0);
            int g = ClampRound255(normBgr.Item1);
            int r = ClampRound255(normBgr.Item2);
            return new Scalar(b, g, r); // BGR order
        }

        private Vec3b Vec3bFromNormalized(Vec3d normBgr)
        {
            return new Vec3b(
                (byte)ClampRound255(normBgr.Item0),
                (byte)ClampRound255(normBgr.Item1),
                (byte)ClampRound255(normBgr.Item2)
            );
        }

        private Stream BitmapSourceToStream(BitmapSource bmpSource)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return ms;
        }

        public void SaveCurrentImage(string path, TiffCompression compression)
        {
            //throw new NotImplementedException();
        }

        private bool SafeBool(object? v, bool def)
        {
            if (v == null) return def;
            if (v is bool bb) return bb;

            // common numeric: 1/0
            if (v is int i) return i != 0;
            if (v is long l) return l != 0L;
            if (v is double d) return !double.IsNaN(d) && Math.Abs(d) > double.Epsilon && d != 0.0;
            if (v is float f) return Math.Abs(f) > float.Epsilon && f != 0f;

            // strings like "true","false","1","0","yes","no","on","off"
            var s = v.ToString()?.Trim();
            if (string.IsNullOrEmpty(s)) return def;
            if (bool.TryParse(s, out var rb)) return rb;
            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv)) return iv != 0;

            switch (s.ToLowerInvariant())
            {
                case "yes":
                case "y":
                case "on":
                    return true;
                case "no":
                case "n":
                case "off":
                    return false;
            }

            return def;
        }

        private double SafeDouble(object? v, double def)
        {
            if (v == null || v == DBNull.Value) return def;

            // fast-path for common numeric CLR types
            if (v is double d) return d;
            if (v is float f) return (double)f;
            if (v is decimal m) return (double)m;
            if (v is int i) return i;
            if (v is long l) return l;
            if (v is short sh) return sh;
            if (v is byte b) return b;
            if (v is sbyte sb) return sb;
            if (v is uint ui) return ui;
            if (v is ulong ul) return ul;
            if (v is ushort us) return us;

            // If it's already an IConvertible (strings included), prefer CurrentCulture for UI-sourced values
            try
            {
                // Prefer current culture (user input), then invariant as fallback
                try
                {
                    return Convert.ToDouble(v, CultureInfo.CurrentCulture);
                }
                catch { /* try invariant below */ }

                try
                {
                    return Convert.ToDouble(v, CultureInfo.InvariantCulture);
                }
                catch { /* try string parsing below */ }

                // Fallback: parse string representation (CurrentCulture then Invariant)
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    s = s.Trim();
                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed))
                        return parsed;
                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
                        return parsed;
                }
            }
            catch
            {
                // unexpected — swallow and return default
            }

            return def;
        }

        private float SafeDoubleToFloat(object? v, float def)
        {
            if (v == null || v == DBNull.Value) return def;

            // fast-path for common numeric CLR types
            if (v is double d) return (float)d;
            if (v is float f) return f;
            if (v is decimal m) return (float)m;
            if (v is int i) return (float)i;
            if (v is long l) return l;
            if (v is short sh) return sh;
            if (v is byte b) return b;
            if (v is sbyte sb) return sb;
            if (v is uint ui) return ui;
            if (v is ulong ul) return ul;
            if (v is ushort us) return us;

            // If it's already an IConvertible (strings included), prefer CurrentCulture for UI-sourced values
            try
            {
                // Prefer current culture (user input), then invariant as fallback
                try
                {
                    return (float)Convert.ToDouble(v, CultureInfo.CurrentCulture);
                }
                catch { /* try invariant below */ }

                try
                {
                    return (float)Convert.ToDouble(v, CultureInfo.InvariantCulture);
                }
                catch { /* try string parsing below */ }

                // Fallback: parse string representation (CurrentCulture then Invariant)
                var s = v.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    s = s.Trim();
                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var parsed))
                        return (float)parsed;
                    if (double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed))
                        return (float)parsed;
                }
            }
            catch
            {
                // unexpected — swallow and return default
            }

            return def;
        }

        private int SafeInt(object? v, int def)
        {
            if (v == null) return def;
            try
            {
                // Convert handles boxed numeric types and numeric strings
                return Convert.ToInt32(v);
            }
            catch
            {
                // fallback: try parsing as double then round
                try
                {
                    if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                                         System.Globalization.CultureInfo.InvariantCulture, out var d))
                        return (int)Math.Round(d);
                }
                catch { }
                return def;
            }
        }


        //private void ApplyBinarize(BinarizeMethod method, BinarizeParameters parameters)
        //{
        //    switch (method)
        //    {
        //        case BinarizeMethod.Threshold:
        //            break;
        //        case BinarizeMethod.Adaptive:
        //            break;
        //        case BinarizeMethod.Sauvola:
        //            break;
        //        case BinarizeMethod.Majority:
        //            break;
        //    }
        //}

        private T ToStruct<T>(Dictionary<string, object> dict)
        where T : struct
        {
            var type = typeof(T);

            // если хочешь дефолтные значения — можешь здесь задать их руками
            object boxed = new T(); // boxed struct

            foreach (var kv in dict)
            {
                // ищем public поле с таким именем (Method, Threshold, ...)
                var field = type.GetField(
                    kv.Key,
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                if (field == null)
                    continue; // нет такого поля — пропускаем

                var targetType = field.FieldType;
                var value = kv.Value;

                var isNullable =
                    targetType.IsGenericType &&
                    targetType.GetGenericTypeDefinition() == typeof(Nullable<>);

                var underlyingType = isNullable
                    ? Nullable.GetUnderlyingType(targetType)!
                    : targetType;

                if (targetType.IsEnum)
                {
                    // Enum: поддерживаем строку и числовые значения
                    if (value is string s)
                    {
                        value = Enum.Parse(underlyingType, s, ignoreCase: true);
                    }
                    else
                    {
                        value = Enum.ToObject(
                            underlyingType,
                            Convert.ToInt32(value, CultureInfo.InvariantCulture));
                    }
                }
                else
                {
                    // обычные типы: int, bool, double и т.п.
                    if (!underlyingType.IsAssignableFrom(value.GetType()))
                    {
                        value = Convert.ChangeType(
                            value,
                            underlyingType,
                            CultureInfo.InvariantCulture);
                    }
                }

                if (isNullable)
                {
                    var nullableValue = Activator.CreateInstance(targetType, value);
                    field.SetValue(boxed, nullableValue);
                }
                else
                {
                    field.SetValue(boxed, value);
                }
            }

            return (T)boxed;
        }

        public void DumpStruct<T>(T value) where T : struct
        {
            var type = typeof(T);
            var sb = new StringBuilder();

            sb.Append(type.Name);
            sb.Append(" { ");

            bool first = true;

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!first)
                    sb.Append(", ");

                sb.Append(field.Name);
                sb.Append("=");

                var fieldValue = field.GetValue(value);
                sb.Append(fieldValue?.ToString() ?? "null");

                first = false;
            }

            sb.Append(" }");

            Debug.WriteLine(sb.ToString());
        }

        public static Scalar SampleCentralBgrMeanScalar_GridFast8U(
                                                                Mat src,
                                                                int sampleSizePx = 0,
                                                                double sampleFraction = 0.10,
                                                                int grid = 11)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Scalar(255, 255, 255, 255);

            int w = src.Cols, h = src.Rows;
            int minSide = Math.Min(w, h);

            int side;
            if (sampleSizePx > 0)
                side = Math.Max(1, Math.Min(sampleSizePx, minSide));
            else
            {
                double frac = (!double.IsNaN(sampleFraction) && sampleFraction > 0) ? sampleFraction : 0.10;
                side = Math.Max(1, (int)Math.Round(minSide * frac));
            }

            int rx = Math.Max(0, (w / 2) - (side / 2));
            int ry = Math.Max(0, (h / 2) - (side / 2));
            int rw = Math.Min(side, w - rx);
            int rh = Math.Min(side, h - ry);
            if (rw <= 0 || rh <= 0) return new Scalar(255, 255, 255, 255);

            var type = src.Type();
            if (type != MatType.CV_8UC3 && type != MatType.CV_8UC4 && type != MatType.CV_8UC1)
            {
                // редкий fallback (не fast)
                using var roiMat = new Mat(src, new Rect(rx, ry, rw, rh));
                var m = Cv2.Mean(roiMat);
                return (roiMat.Channels() == 1)
                    ? new Scalar(m.Val0, m.Val0, m.Val0, 255)
                    : new Scalar(m.Val0, m.Val1, m.Val2, 255);
            }

            // grid size clamps
            if (grid < 1) grid = 1;
            int gx = Math.Min(grid, rw);
            int gy = Math.Min(grid, rh);

            long sumB = 0, sumG = 0, sumR = 0;
            long count = (long)gx * gy;

            unsafe
            {
                byte* basePtr = (byte*)src.DataPointer;
                long step = src.Step(); // bytes per row

                if (type == MatType.CV_8UC1)
                {
                    for (int iy = 0; iy < gy; iy++)
                    {
                        int y = (gy == 1) ? (ry + rh / 2) : (ry + (iy * (rh - 1)) / (gy - 1));
                        byte* row = basePtr + y * step;
                        for (int ix = 0; ix < gx; ix++)
                        {
                            int x = (gx == 1) ? (rx + rw / 2) : (rx + (ix * (rw - 1)) / (gx - 1));
                            sumB += row[x];
                        }
                    }

                    long mean = (sumB + count / 2) / count;
                    return new Scalar(mean, mean, mean, 255);
                }

                int pixSize = (type == MatType.CV_8UC4) ? 4 : 3;

                for (int iy = 0; iy < gy; iy++)
                {
                    int y = (gy == 1) ? (ry + rh / 2) : (ry + (iy * (rh - 1)) / (gy - 1));
                    byte* row = basePtr + y * step;

                    for (int ix = 0; ix < gx; ix++)
                    {
                        int x = (gx == 1) ? (rx + rw / 2) : (rx + (ix * (rw - 1)) / (gx - 1));
                        byte* p = row + x * pixSize;

                        // OpenCV order: B,G,R,(A)
                        sumB += p[0];
                        sumG += p[1];
                        sumR += p[2];
                    }
                }
            }

            long meanB = (sumB + count / 2) / count;
            long meanG = (sumG + count / 2) / count;
            long meanR = (sumR + count / 2) / count;

            return new Scalar(meanB, meanG, meanR, 255);
        }

        private Mat ProcessSingle(Mat src,
                           ProcessorCommand command,
                           Dictionary<string, object> parameters, CancellationToken token, bool batchProcessing)
        {
            lock (_commandLock)
            {
                //using var src = WorkingImage;
                if (src != null)
                {

                    switch (command)
                    {
                        case ProcessorCommand.Binarize:
                            var binParams = ToStruct<BinarizeParameters>(parameters);
                            return Binarizer.Binarize(src, binParams.Method, binParams);
                        case ProcessorCommand.Enhance:
                            if (TryApplyEnhanceCommand(src, _token, parameters ?? new Dictionary<string, object>(), out Mat? result))
                                return result!;
                            break;
                        case ProcessorCommand.Deskew:
                            return NewDeskew(src, parameters);
                        case ProcessorCommand.BordersRemove:
                            {
                                double treshFrac = 0.40;
                                int contrastThr = 50;
                                double centralSample = 0.10;
                                double maxRemoveFrac = 0.45;

                                byte darkThresh = 40;
                                bool autoThresh = false;
                                int marginPercentForThresh = 10;
                                double shiftFactorForTresh = 0.25;
                                Scalar bgColor = SampleCentralBgrMeanScalar_GridFast8U(src, 0, 0.1);
                                int minAreaPx = 2000;
                                double minSpanFraction = 0.6;
                                double solidityThreshold = 0.6;
                                double minDepthFraction = 0.05;
                                int featherPx = 12;
                                int top = 0, bottom = 0, left = 0, right = 0;
                                bool manualCutDebug = false;
                                bool useTeleaHybrid = true;
                                bool applyManualCut = false;

                                int contourCannyLow = 50;
                                int contourCannyHigh = 150;
                                int contourMorphKernel = 5;
                                double contourMinAreaFrac = 0.10;
                                int contourPaddingPx = 10;
                                bool contourCut = true;

                                // integral method parameters
                                int brickThickness = 16;
                                int safetyOffsetPx = 2;
                                BordersRemover.BrickInpaintMode inpaintMode = BordersRemover.BrickInpaintMode.Fill;
                                double inpaintRadius = 0.0;
                                double borderColorTolerance = 0.5;
                                bool autoMaxBorderDepthFrac = true;
                                BordersRemover.MaxBorderDepthsFrac maxBorderDepthsFrac = new BordersRemover.MaxBorderDepthsFrac()
                                {
                                    Left = 0.15,
                                    Right = 0.15,
                                    Top = 0.15,
                                    Bottom = 0.15
                                };
                                double seedContrastStrictness = 1.0;
                                double seedBrightnessStrictness = 1.0;
                                double textureAllowance = 1.0;
                                int kInterpolation = 0;
                                bool integralCut = false;


                                foreach (var kv in parameters)
                                {
                                    if (kv.Key == null) continue;

                                    switch (kv.Key)
                                    {
                                        case "autoThresh":
                                            autoThresh = SafeBool(kv.Value, autoThresh);
                                            break;
                                        case "marginPercent":
                                            marginPercentForThresh = SafeInt(kv.Value, marginPercentForThresh);
                                            break;
                                        case "shiftFactor":
                                            shiftFactorForTresh = SafeDouble(kv.Value, shiftFactorForTresh);
                                            break;
                                        case "bgColor":
                                            //int i = SafeInt(kv.Value, 0);
                                            //int color = Math.Max(0, Math.Min(255, i));
                                            //bgColor = new Scalar(0, 0, 255);
                                            //bgColor = new Scalar(color, color, color);
                                            //bgColor = SampleCentralGrayScalar(src, 0, 0.1);

                                            //bgColor = SampleCentralGrayScalar_Fast8U(src, 0, 0.1);
                                            Debug.WriteLine("bgColor:", bgColor.ToString());
                                            break;
                                        case "darkThreshold":
                                            int iThresh = SafeInt(kv.Value, darkThresh);
                                            darkThresh = (byte)(iThresh < 0 ? 0 : (iThresh > 255 ? 255 : iThresh));
                                            break;
                                        case "treshFrac":
                                            treshFrac = SafeDouble(kv.Value, treshFrac);
                                            break;
                                        case "minSpanFraction":
                                            minSpanFraction = SafeDouble(kv.Value, minSpanFraction);
                                            break;
                                        case "solidityThreshold":
                                            solidityThreshold = SafeDouble(kv.Value, solidityThreshold);
                                            break;
                                        case "minDepthFraction":
                                            minDepthFraction = SafeDouble(kv.Value, minDepthFraction);
                                            break;

                                        case "contrastThr":
                                            contrastThr = SafeInt(kv.Value, contrastThr);
                                            break;
                                        case "minAreaPx":
                                            minAreaPx = SafeInt(kv.Value, minAreaPx);
                                            break;
                                        case "featherPx":
                                            featherPx = SafeInt(kv.Value, featherPx);
                                            break;
                                        case "useTeleaHybrid":
                                            useTeleaHybrid = SafeBool(kv.Value, useTeleaHybrid);
                                            break;

                                        case "centralSample":
                                            centralSample = SafeDouble(kv.Value, centralSample);
                                            break;

                                        case "maxRemoveFrac":
                                            maxRemoveFrac = SafeDouble(kv.Value, maxRemoveFrac);
                                            break;
                                        case "contourCannyLow":
                                            contourCannyLow = SafeInt(kv.Value, contourCannyLow);
                                            break;
                                        case "contourCannyHigh":
                                            contourCannyHigh = SafeInt(kv.Value, contourCannyHigh);
                                            break;
                                        case "contourMorphKernel":
                                            contourMorphKernel = SafeInt(kv.Value, contourMorphKernel);
                                            break;
                                        case "contourMinAreaFrac":
                                            contourMinAreaFrac = SafeDouble(kv.Value, contourMinAreaFrac);
                                            break;
                                        case "contourPaddingPx":
                                            contourPaddingPx = SafeInt(kv.Value, contourPaddingPx);
                                            break;
                                        case "contourCut":
                                            contourCut = SafeBool(kv.Value, contourCut);
                                            break;
                                        case "manualTop":
                                            top = SafeInt(kv.Value, top);
                                            break;
                                        case "manualBottom":
                                            bottom = SafeInt(kv.Value, bottom);
                                            break;
                                        case "manualLeft":
                                            left = SafeInt(kv.Value, left);
                                            break;
                                        case "manualRight":
                                            right = SafeInt(kv.Value, right);
                                            break;
                                        case "manualCutDebug":
                                            manualCutDebug = batchProcessing ? false : SafeBool(kv.Value, manualCutDebug);
                                            break;
                                        case "cutMethod":
                                            applyManualCut = SafeBool(kv.Value, applyManualCut);
                                            break;
                                        case "scanStepPx":
                                            brickThickness = SafeInt(kv.Value, brickThickness);
                                            break;
                                        case "borderSafetyOffsetPx":
                                            safetyOffsetPx = SafeInt(kv.Value, safetyOffsetPx);
                                            break;
                                        case "inpaintMode":
                                            var raw = kv.Value?.ToString();
                                            if (string.IsNullOrWhiteSpace(raw))
                                            {
                                                inpaintMode = BordersRemover.BrickInpaintMode.Fill;
                                                break;
                                            }

                                            raw = raw.Trim().Trim('"');          // in case value comes with quotes
                                            raw = raw.Replace("–", "-").Replace("—", "-"); // normalize unicode dashes

                                            // 1) numeric value support (0/1/2)
                                            if (int.TryParse(raw, out var modeInt) &&
                                                Enum.IsDefined(typeof(BordersRemover.BrickInpaintMode), modeInt))
                                            {
                                                inpaintMode = (BordersRemover.BrickInpaintMode)modeInt;
                                                break;
                                            }

                                            // 2) normalize to compare
                                            var key = raw.Replace(" ", "").Replace("-", "");

                                            // 3) aliases
                                            if (key.Equals("Fill", StringComparison.OrdinalIgnoreCase))
                                                inpaintMode = BordersRemover.BrickInpaintMode.Fill;
                                            else if (key.Equals("Telea", StringComparison.OrdinalIgnoreCase))
                                                inpaintMode = BordersRemover.BrickInpaintMode.Telea;
                                            else if (key.Equals("NS", StringComparison.OrdinalIgnoreCase))
                                                inpaintMode = BordersRemover.BrickInpaintMode.NS;
                                            else
                                            {
                                                // 4) direct enum parse fallback (case-insensitive)
                                                if (!Enum.TryParse(raw, ignoreCase: true, out inpaintMode))
                                                    inpaintMode = BordersRemover.BrickInpaintMode.Fill;
                                            }

                                            break;
                                        case "inpaintRadius":
                                            inpaintRadius = SafeDouble(kv.Value, inpaintRadius);
                                            break;
                                        case "borderColorTolerance":
                                            borderColorTolerance = SafeDouble(kv.Value, borderColorTolerance);
                                            break;
                                        case "autoMaxBorderDepthFrac":
                                            autoMaxBorderDepthFrac = SafeBool(kv.Value, autoMaxBorderDepthFrac);
                                            break;
                                        case "maxBorderDepthFracLeft":
                                            maxBorderDepthsFrac.Left = SafeDouble(kv.Value, maxBorderDepthsFrac.Left);
                                            break;
                                        case "maxBorderDepthFracRight":
                                            maxBorderDepthsFrac.Right = SafeDouble(kv.Value, maxBorderDepthsFrac.Right);
                                            break;
                                        case "maxBorderDepthFracTop":
                                            maxBorderDepthsFrac.Top = SafeDouble(kv.Value, maxBorderDepthsFrac.Top);
                                            break;
                                        case "maxBorderDepthFracBottom":
                                            maxBorderDepthsFrac.Bottom = SafeDouble(kv.Value, maxBorderDepthsFrac.Bottom);
                                            break;
                                        case "seedContrastStrictness":
                                            seedContrastStrictness = SafeDouble(kv.Value, seedContrastStrictness);
                                            break;
                                        case "seedBrightnessStrictness":
                                            seedBrightnessStrictness = SafeDouble(kv.Value, seedBrightnessStrictness);
                                            break;
                                        case "textureAllowance":
                                            textureAllowance = SafeDouble(kv.Value, textureAllowance);
                                            break;
                                        case "kInterpolation":
                                            kInterpolation = SafeInt(kv.Value, kInterpolation);
                                            break;
                                        case "integralCut":
                                            integralCut = SafeBool(kv.Value, integralCut);
                                            break;
                                        default:
                                            // ignore unknown key
                                            break;
                                    }
                                }

                                foreach (var kv in parameters)
                                {
                                    if (kv.Key == "borderRemovalAlgorithm")
                                    {
                                        Debug.WriteLine(kv.Value.ToString());
                                        switch (kv.Value.ToString())
                                        {
                                            case "Auto":

                                                if (autoThresh)
                                                {
                                                    darkThresh = EstimateBlackThreshold(src, marginPercentForThresh, shiftFactorForTresh);
                                                }

                                                //Mat enhanced = Enhancer.ApplyClahe(src, clipLimit: 2.0, gridSize: 8);
                                                return RemoveBorders_Auto(src,
                                                    darkThresh,
                                                    bgColor,
                                                    minAreaPx,
                                                    minSpanFraction,
                                                    solidityThreshold,
                                                    minDepthFraction,
                                                    featherPx,
                                                    useTeleaHybrid
                                                );
                                            case "By Contrast":
                                                return RemoveBordersByRowColWhite(src,
                                                        threshFrac: treshFrac,
                                                        contrastThr: contrastThr,
                                                        centralSample: centralSample,
                                                        maxRemoveFrac: maxRemoveFrac
                                                    );
                                            case "By Contours":
                                                return RemoveBordersByContours(
                                                    src,
                                                    contourCannyLow,
                                                    contourCannyHigh,
                                                    contourMorphKernel,
                                                    contourMinAreaFrac,
                                                    contourPaddingPx,
                                                    contourCut ? BordersRemover.BordersRemovalMode.Cut : BordersRemover.BordersRemovalMode.Fill,
                                                    null);
                                            case "Manual":

                                                return RemoveBorders_Manual(src, top, bottom, left, right, applyManualCut, bgColor, manualCutDebug);
                                            case "Integral":
                                                return RemoveBorders_Integral(token,
                                                        batchProcessing,
                                                        src,
                                                        brickThickness,
                                                        borderColorTolerance,
                                                        safetyOffsetPx,
                                                        inpaintMode,
                                                        inpaintRadius,
                                                        autoMaxBorderDepthFrac,
                                                        maxBorderDepthsFrac,
                                                        seedContrastStrictness,
                                                        seedBrightnessStrictness,
                                                        textureAllowance,
                                                        kInterpolation,
                                                        integralCut,
                                                        null);

                                        }
                                    }

                                }

                            }
                            break;
                        case ProcessorCommand.Despeckle:
                            bool smallAreaRelative = true;
                            double smallAreaMultiplier = 0.25;
                            int smallAreaAbsolutePx = 64;
                            double maxDotHeightFraction = 0.35;
                            double proximityRadiusFraction = 0.8;
                            double squarenessTolerance = 0.6;
                            bool keepClusters = true;
                            bool useDilateBeforeCC = true;
                            string dilateKernel = "1x3";
                            int dilateIter = 1;
                            bool enableDustRemoval = false;
                            int dustMedianKsize = 3;
                            int dustOpenKernel = 3;
                            int dustOpenIter = 1;
                            bool enableDustShapeFilter = false;
                            double dustMinSolidity = 0.6;
                            double dustMaxAspectRatio = 3.0;
                            bool showDespeckleDebug = false;
                            string despeckleMethod = "Classic";

                            DespeckleSettings settings = new DespeckleSettings();
                            foreach (var kv in parameters)
                            {
                                if (kv.Key == null) continue;

                                switch (kv.Key)
                                {
                                    case "smallAreaRelative":
                                        settings.SmallAreaRelative = SafeBool(kv.Value, smallAreaRelative);
                                        break;
                                    case "smallAreaMultiplier":
                                        settings.SmallAreaMultiplier = SafeDouble(kv.Value, smallAreaMultiplier);
                                        break;
                                    case "smallAreaAbsolutePx":
                                        settings.SmallAreaAbsolutePx = SafeInt(kv.Value, smallAreaAbsolutePx);
                                        break;
                                    case "maxDotHeightFraction":
                                        settings.MaxDotHeightFraction = SafeDouble(kv.Value, maxDotHeightFraction);
                                        break;
                                    case "proximityRadiusFraction":
                                        settings.ProximityRadiusFraction = SafeDouble(kv.Value, proximityRadiusFraction);
                                        break;
                                    case "squarenessTolerance":
                                        settings.SquarenessTolerance = SafeDouble(kv.Value, squarenessTolerance);
                                        break;
                                    case "keepClusters":
                                        settings.KeepClusters = SafeBool(kv.Value, keepClusters);
                                        break;
                                    case "useDilateBeforeCC":
                                        settings.UseDilateBeforeCC = SafeBool(kv.Value, useDilateBeforeCC);
                                        break;
                                    case "dilateKernel":
                                        settings.DilateKernel = kv.Value.ToString();
                                        if (settings.DilateKernel == string.Empty) settings.DilateKernel = dilateKernel;
                                        break;
                                    case "dilateIter":
                                        settings.DilateIter = SafeInt(kv.Value, dilateIter);
                                        break;
                                    case "despeckleMethod":
                                        despeckleMethod = kv.Value?.ToString() ?? despeckleMethod;
                                        break;
                                    case "enableDustRemoval":
                                        settings.EnableDustRemoval = SafeBool(kv.Value, enableDustRemoval);
                                        break;
                                    case "dustMedianKsize":
                                        settings.DustMedianKsize = SafeInt(kv.Value, dustMedianKsize);
                                        break;
                                    case "dustOpenKernel":
                                        settings.DustOpenKernel = SafeInt(kv.Value, dustOpenKernel);
                                        break;
                                    case "dustOpenIter":
                                        settings.DustOpenIter = SafeInt(kv.Value, dustOpenIter);
                                        break;
                                    case "enableDustShapeFilter":
                                        settings.EnableDustShapeFilter = SafeBool(kv.Value, enableDustShapeFilter);
                                        break;
                                    case "dustMinSolidity":
                                        settings.DustMinSolidity = SafeDouble(kv.Value, dustMinSolidity);
                                        break;
                                    case "dustMaxAspectRatio":
                                        settings.DustMaxAspectRatio = SafeDouble(kv.Value, dustMaxAspectRatio);
                                        break;
                                    case "showDespeckleDebug":
                                        settings.ShowDespeckleDebug = batchProcessing ? false : SafeBool(kv.Value, showDespeckleDebug);
                                        break;

                                }
                            }

                            //_currentImage = DespeckleApplyToSource(_currentImage, settings, true, false, true);
                            if (despeckleMethod.Equals("Effective", StringComparison.OrdinalIgnoreCase))
                                return DespeckleEffective(src, settings);
                            return Despeckle(src, settings);
                        case ProcessorCommand.SmartCrop:

                            // U-net
                            int cropLevel = 62;

                            // EAST
                            //int eastInputWidth = 1280;
                            //int eastInputHeight = 1280;
                            //float eastScoreThreshold = 0.45f;
                            //float eastNmsThreshold = 0.45f;
                            //int tesseractMinConfidence = 50;
                            //int paddingPx = 20;
                            //int downscaleMaxWidth = 1600;
                            bool eastDebug = true;

                            var tds = TextDetectionSettings.CreateDefault();

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == null) continue;
                                switch (kv.Key)
                                {
                                    case "cropLevel":
                                        cropLevel = SafeInt(kv.Value, cropLevel);
                                        break;
                                    case "eastInputWidth":
                                        tds.EastInputWidth = SafeInt(kv.Value, tds.EastInputWidth);
                                        break;
                                    case "eastInputHeight":
                                        tds.EastInputHeight = SafeInt(kv.Value, tds.EastInputHeight);
                                        break;
                                    case "eastScoreThreshold":
                                        tds.EastScoreThreshold = SafeDoubleToFloat(kv.Value, tds.EastScoreThreshold);
                                        break;
                                    case "eastNmsThreshold":
                                        tds.EastNmsThreshold = SafeDoubleToFloat(kv.Value, tds.EastNmsThreshold);
                                        break;
                                    case "tesseractMinConfidence":
                                        tds.TesseractMinConfidence = SafeInt(kv.Value, tds.TesseractMinConfidence);
                                        break;
                                    case "paddingPx":
                                        tds.PaddingPx = SafeInt(kv.Value, tds.PaddingPx);
                                        break;
                                    case "downscaleMaxWidth":
                                        tds.DownscaleMaxWidth = SafeInt(kv.Value, tds.DownscaleMaxWidth);
                                        break;
                                    case "includeHandwritten":
                                        tds.IncludeHandwritten = SafeBool(kv.Value, tds.IncludeHandwritten);
                                        break;
                                    case "handwrittenSensitivity":
                                        tds.HandwrittenMinAreaFraction = SafeInt(kv.Value, tds.HandwrittenMinAreaFraction);
                                        break;
                                    case "includeStamps":
                                        tds.IncludeStamps = SafeBool(kv.Value, tds.IncludeStamps);
                                        break;
                                    case "eastDebug":
                                        eastDebug = batchProcessing ? false : SafeBool(kv.Value, eastDebug);
                                        break;

                                }
                            }

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == "autoCropMethod")
                                {
                                    switch (kv.Value.ToString())
                                    {
                                        case "U-net":
                                            var resultDDC = DetectDocumentAndCrop(src, cropLevel, false, batchProcessing, out Mat debugMask, out Mat debugOverlay);
                                            debugMask.Dispose();
                                            debugOverlay.Dispose();
                                            return resultDDC;
                                        case "EAST":
                                            return SmartCrop(
                                                src,
                                                tds,
                                                eastDebug
                                            );
                                    }
                                }
                            }
                            //applyAutoCropRectangleCurrent();
                            break;
                        case ProcessorCommand.LinesRemove:
                            int lineWidthPx = 1;
                            double minLengthFraction = 0.5;
                            LineOrientation orientation = LineOrientation.Vertical;
                            int offsetStartPx = 0;
                            int lineColorRed = 255;
                            int lineColorGreen = 255;
                            int lineColorBlue = 255;
                            int colorTolerance = 40;

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == null) continue;
                                switch (kv.Key)
                                {
                                    case "lineWidthPx":
                                        lineWidthPx = SafeInt(kv.Value, lineWidthPx);
                                        break;
                                    case "minLengthFraction":
                                        minLengthFraction = SafeDouble(kv.Value, minLengthFraction);
                                        break;
                                    case "orientation":
                                        switch (kv.Value)
                                        {
                                            case "Vertical":
                                                orientation = LineOrientation.Vertical;
                                                break;
                                            case "Horizontal":
                                                orientation = LineOrientation.Horizontal;
                                                break;
                                            default:
                                                orientation = LineOrientation.Both;
                                                break;
                                        }
                                        break;
                                    case "offsetStartPx":
                                        offsetStartPx = SafeInt(kv.Value, offsetStartPx);
                                        break;
                                    case "lineColorRed":
                                        lineColorRed = SafeInt(kv.Value, lineColorRed);
                                        break;
                                    case "lineColorGreen":
                                        lineColorGreen = SafeInt(kv.Value, lineColorGreen);
                                        break;
                                    case "lineColorBlue":
                                        lineColorBlue = SafeInt(kv.Value, lineColorBlue);
                                        break;
                                    case "colorTolerance":
                                        colorTolerance = SafeInt(kv.Value, colorTolerance);
                                        break;

                                }
                            }

                            //WorkingImage = RemoveLines(src, lineWidthPx, minLengthFraction, orientation, offsetStartPx, lineColorRed, lineColorGreen, lineColorBlue, colorTolerance);
                            //var mask = new Mat();
                            var resultLR = new Mat();
                            var mask = new Mat();
                            if (orientation == LineOrientation.Vertical)
                            {
                                resultLR = LinesRemover.RemoveScannerVerticalStripes(src, 3, 20, 0, out mask, false, null);

                            }
                            if (orientation == LineOrientation.Horizontal)
                            {
                                resultLR = LinesRemover.RemoveScannerHorizontalStripes(src, 3, 20, 0, out mask, false, null);
                            }
                            if (orientation == LineOrientation.Both)
                            {
                                using Mat firstResult = LinesRemover.RemoveScannerVerticalStripes(src, 3, 20, 0, out mask, false, null);
                                resultLR = LinesRemover.RemoveScannerHorizontalStripes(firstResult, 3, 20, 0, out mask, false, null);
                            }
                            mask.Dispose();
                            return resultLR;

                        case ProcessorCommand.DotsRemove:
                            return src.Clone();
                        case ProcessorCommand.ChannelsCorrection:
                            return src.Clone();
                        case ProcessorCommand.PunchHolesRemove:

                            Debug.WriteLine("Starting Punch removing");

                            PunchShape shape = PunchShape.Circle;
                            int diameter = 20;
                            int height = 20;
                            int width = 20;
                            double density = 0.50;
                            double sizeTolerance = 0.40;
                            int leftOffset = 100;
                            int rightOffset = 100;
                            int topOffset = 100;
                            int bottomOffset = 100;
                            double fillRatio = 0.9;
                            double roundness = 0.9;

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == null) continue;

                                switch (kv.Key)
                                {
                                    //case "Circle":
                                    //    shape = PunchShape.Circle;
                                    //    break;
                                    //case "Rect":
                                    //    shape = PunchShape.Rect;
                                    //    break;
                                    case "diameter":
                                        diameter = SafeInt(kv.Value, diameter);
                                        break;
                                    case "roundness":
                                        roundness = SafeDouble(kv.Value, roundness);
                                        break;
                                    case "height":
                                        height = SafeInt(kv.Value, height);
                                        break;
                                    case "width":
                                        width = SafeInt(kv.Value, width);
                                        break;
                                    case "fillRatio":
                                        fillRatio = SafeDouble(kv.Value, fillRatio);
                                        break;
                                    case "density":
                                        density = SafeDouble(kv.Value, density);
                                        break;
                                    case "sizeTolerance":
                                        sizeTolerance = SafeDouble(kv.Value, sizeTolerance);
                                        break;
                                    case "leftOffset":
                                        leftOffset = SafeInt(kv.Value, leftOffset);
                                        break;
                                    case "rightOffset":
                                        rightOffset = SafeInt(kv.Value, rightOffset);
                                        break;
                                    case "topOffset":
                                        topOffset = SafeInt(kv.Value, topOffset);
                                        break;
                                    case "bottomOffset":
                                        bottomOffset = SafeInt(kv.Value, bottomOffset);
                                        break;
                                    default:
                                        // ignore unknown key
                                        break;
                                }
                            }

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == "punchShape")
                                {
                                    switch (kv.Value.ToString())
                                    {
                                        case "Circle":
                                            shape = PunchShape.Circle;
                                            break;
                                        case "Rect":
                                            shape = PunchShape.Rect;
                                            break;
                                        case "Both":
                                            shape = PunchShape.Both;
                                            //Debug.WriteLine($"IN CASE Both SHAPE = {shape}");
                                            break;
                                    }
                                }

                            }

                            List<PunchSpec> specs = new List<PunchSpec>();

                            PunchSpec spec1 = new PunchSpec();
                            spec1.Shape = shape;
                            spec1.Diameter = diameter;
                            spec1.RectSize = new OpenCvSharp.Size(width, height);
                            spec1.Density = density;
                            spec1.SizeToleranceFraction = sizeTolerance;

                            //Debug.WriteLine(spec1.Shape.ToString());
                            //Debug.WriteLine(spec1.Diameter.ToString());
                            //Debug.WriteLine(width.ToString());
                            //Debug.WriteLine(height.ToString());
                            //Debug.WriteLine(spec1.Count.ToString());
                            //Debug.WriteLine(spec1.Density.ToString());
                            //Debug.WriteLine(spec1.SizeToleranceFraction.ToString());

                            PunchSpec spec2 = new PunchSpec();
                            spec2.Diameter = diameter;
                            spec2.RectSize = new OpenCvSharp.Size(width, height);
                            spec2.Density = density;
                            spec2.SizeToleranceFraction = sizeTolerance;


                            if (spec1.Shape == PunchShape.Circle)
                            {

                                specs.Add(spec1);

                            }
                            else if (spec1.Shape == PunchShape.Rect)
                            {
                                specs.Add(spec1);
                            }
                            else if (spec1.Shape == PunchShape.Both)
                            {
                                spec1.Shape = PunchShape.Circle;
                                spec2.Shape = PunchShape.Rect;
                                specs.Add(spec1);
                                specs.Add(spec2);
                            }
                            //Debug.WriteLine($"Shape: {spec1.Shape}");

                            //Debug.WriteLine($"Specs len: {specs.Count}");

                            var offsets = new Offsets
                            {
                                left = leftOffset,
                                right = rightOffset,
                                top = topOffset,
                                bottom = bottomOffset,
                            };


                            return PunchHolesRemove(src, specs, roundness, fillRatio, offsets);
                        case ProcessorCommand.PageSplit:
                            {
                                var splitParameters = parameters ?? new Dictionary<string, object>();
                                if (batchProcessing)
                                {
                                    ExecutePageSplitBatch(src, splitParameters);
                                }
                                else
                                {
                                    ExecutePageSplitPreview(src, splitParameters);
                                }
                                return src.Clone();
                            }
                    }
                }
            }
            return src.Clone();
        }

        private bool TryApplyEnhanceCommand(Mat src, CancellationToken token, Dictionary<string, object> parameters, out Mat? result)
        {
            try
            {
                string? method = null;
                if (parameters.TryGetValue("enhanceMethod", out var methodObj))
                    method = methodObj?.ToString();

                string methodName = method?.Trim() ?? string.Empty;
                bool isRetinex = methodName.Equals("Homomorphic Retinex", StringComparison.OrdinalIgnoreCase);
                bool isLevels = methodName.Equals("Levels & Gamma", StringComparison.OrdinalIgnoreCase) ||
                                methodName.Equals("Levels and Gamma", StringComparison.OrdinalIgnoreCase);
                bool isColorAdjust = methodName.Equals("Color Adjust", StringComparison.OrdinalIgnoreCase);
                bool isBrightnessContrast = methodName.Equals("Brightness & Contrast", StringComparison.OrdinalIgnoreCase) ||
                                            methodName.Equals("Brightness/Contrast", StringComparison.OrdinalIgnoreCase) ||
                                            methodName.Equals("Brightness and Contrast", StringComparison.OrdinalIgnoreCase);

                if (isRetinex)
                {
                    var outputMode = Enhancer.RetinexOutputMode.LogHighpass;
                    bool useLabL = true;
                    double sigma = 50.0;
                    double gammaHigh = 1.6;
                    double gammaLow = 0.7;
                    double eps = 1e-6;
                    bool robustNormalize = true;
                    double percentLow = 0.5;
                    double percentHigh = 99.5;
                    int histBins = 2048;
                    double expClampAbs = 4.0;

                    foreach (var kv in parameters)
                    {
                        switch (kv.Key)
                        {
                            case "retinexOutputMode":
                                var modeStr = kv.Value?.ToString();
                                if (!string.IsNullOrWhiteSpace(modeStr) &&
                                    Enum.TryParse(modeStr, true, out Enhancer.RetinexOutputMode parsedMode))
                                {
                                    outputMode = parsedMode;
                                }
                                break;
                            case "retinexUseLabL":
                                useLabL = SafeBool(kv.Value, useLabL);
                                break;
                            case "retinexSigma":
                                sigma = SafeDouble(kv.Value, sigma);
                                break;
                            case "retinexGammaHigh":
                                gammaHigh = SafeDouble(kv.Value, gammaHigh);
                                break;
                            case "retinexGammaLow":
                                gammaLow = SafeDouble(kv.Value, gammaLow);
                                break;
                            case "retinexEps":
                                eps = SafeDouble(kv.Value, eps);
                                break;
                            case "retinexRobustNormalize":
                                robustNormalize = SafeBool(kv.Value, robustNormalize);
                                break;
                            case "retinexPercentLow":
                                percentLow = SafeDouble(kv.Value, percentLow);
                                break;
                            case "retinexPercentHigh":
                                percentHigh = SafeDouble(kv.Value, percentHigh);
                                break;
                            case "retinexHistBins":
                                histBins = SafeInt(kv.Value, histBins);
                                break;
                            case "retinexExpClamp":
                                expClampAbs = SafeDouble(kv.Value, expClampAbs);
                                break;
                        }
                    }

                    percentLow = Math.Max(0.0, Math.Min(50.0, percentLow));
                    percentHigh = Math.Max(percentLow + 0.1, Math.Min(100.0, percentHigh));
                    histBins = Math.Max(32, Math.Min(8192, histBins));
                    expClampAbs = Math.Max(0.5, expClampAbs);

                    result = Enhancer.HomomorphicRetinex(
                        token,
                        src,
                        outputMode,
                        useLabL,
                        Math.Max(0.1, sigma),
                        Math.Max(0.1, gammaHigh),
                        Math.Max(0.01, gammaLow),
                        Math.Max(1e-8, eps),
                        robustNormalize,
                        percentLow,
                        percentHigh,
                        histBins,
                        expClampAbs);
                    return true;
                }

                if (isLevels)
                {
                    double blackPct = 1.0;
                    double whitePct = 95.0;
                    double levelsGamma = 0.85;
                    double targetWhite = 255.0;

                    foreach (var kv in parameters)
                    {
                        switch (kv.Key)
                        {
                            case "levelsBlackPercent":
                                blackPct = SafeDouble(kv.Value, blackPct);
                                break;
                            case "levelsWhitePercent":
                                whitePct = SafeDouble(kv.Value, whitePct);
                                break;
                            case "levelsGamma":
                                levelsGamma = SafeDouble(kv.Value, levelsGamma);
                                break;
                            case "levelsTargetWhite":
                                targetWhite = SafeDouble(kv.Value, targetWhite);
                                break;
                        }
                    }

                    result = ApplyLevelsAndGamma(src, token, blackPct, whitePct, levelsGamma, targetWhite);
                    return true;
                }

                if (isColorAdjust)
                {
                    double red = 0;
                    double green = 0;
                    double blue = 0;
                    double hue = 0;
                    double saturation = 0;

                    foreach (var kv in parameters)
                    {
                        switch (kv.Key)
                        {
                            case "colorRed":
                                red = SafeDouble(kv.Value, red);
                                break;
                            case "colorGreen":
                                green = SafeDouble(kv.Value, green);
                                break;
                            case "colorBlue":
                                blue = SafeDouble(kv.Value, blue);
                                break;
                            case "colorHue":
                                hue = SafeDouble(kv.Value, hue);
                                break;
                            case "colorSaturation":
                                saturation = SafeDouble(kv.Value, saturation);
                                break;
                        }
                    }

                    red = Math.Max(-100.0, Math.Min(100.0, red));
                    green = Math.Max(-100.0, Math.Min(100.0, green));
                    blue = Math.Max(-100.0, Math.Min(100.0, blue));
                    hue = Math.Max(-180.0, Math.Min(180.0, hue));
                    saturation = Math.Max(-100.0, Math.Min(100.0, saturation));

                    result = Enhancer.AdjustColor(token, src, red, green, blue, hue, saturation);
                    return true;
                }

                if (isBrightnessContrast)
                {
                    double brightness = 0;
                    double contrast = 0;

                    foreach (var kv in parameters)
                    {
                        switch (kv.Key)
                        {
                            case "brightness":
                                brightness = SafeDouble(kv.Value, brightness);
                                break;
                            case "contrast":
                                contrast = SafeDouble(kv.Value, contrast);
                                break;
                        }
                    }

                    brightness = Math.Max(-100.0, Math.Min(100.0, brightness));
                    contrast = Math.Max(-100.0, Math.Min(100.0, contrast));

                    result = Enhancer.AdjustBrightnessContrast(token, src, brightness, contrast);
                    return true;
                }

                double claheClipLimit = 4.0;
                int claheGridSize = 8;

                if (parameters.TryGetValue("claheClipLimit", out var clipObj))
                    claheClipLimit = SafeDouble(clipObj, claheClipLimit);
                if (parameters.TryGetValue("claheGridSize", out var gridObj))
                    claheGridSize = SafeInt(gridObj, claheGridSize);

                result = Enhancer.ApplyClahe(
                    token,
                    src,
                    Math.Max(0.1, claheClipLimit),
                    Math.Max(1, claheGridSize));
                return true;
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Enhance process cancelled!");
#endif
                result = null;
                return false;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error in Enhance module: {ex.Message}");
                result = null;
                return false;
            }
        }

        private Mat ApplyLevelsAndGamma(Mat src, CancellationToken token, double blackPct, double whitePct, double gamma, double targetWhite)
        {
            if (src == null || src.Empty())
                return src?.Clone() ?? new Mat();

            double clampedBlack = Math.Max(0.0, Math.Min(blackPct, 20.0));
            double clampedWhite = Math.Max(clampedBlack + 0.1, Math.Min(whitePct, 100.0));
            double clampedGamma = Math.Max(0.05, Math.Min(gamma, 5.0));
            byte whiteTarget = (byte)Math.Max(1, Math.Min(255, Math.Round(targetWhite)));

            if (src.Channels() == 1)
            {
                using var gray8 = new Mat();
                if (src.Type() == MatType.CV_8UC1)
                    src.CopyTo(gray8);
                else
                    src.ConvertTo(gray8, MatType.CV_8UC1);

                var leveled = Enhancer.LevelsAndGamma8U(gray8, _token, clampedBlack, clampedWhite, clampedGamma, whiteTarget);
                gray8.Dispose();

                var colored = new Mat();
                Cv2.CvtColor(leveled, colored, ColorConversionCodes.GRAY2BGR);
                leveled.Dispose();
                return colored;
            }

            using var bgr = new Mat();
            if (src.Type() == MatType.CV_8UC3)
                src.CopyTo(bgr);
            else if (src.Type() == MatType.CV_8UC4)
                Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
            else
                src.ConvertTo(bgr, MatType.CV_8UC3);

            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
            var channels = lab.Split();
            try
            {
                var leveledL = Enhancer.LevelsAndGamma8U(channels[0], _token, clampedBlack, clampedWhite, clampedGamma, whiteTarget);
                channels[0].Dispose();
                channels[0] = leveledL;

                using var merged = new Mat();
                Cv2.Merge(channels, merged);

                var result = new Mat();
                Cv2.CvtColor(merged, result, ColorConversionCodes.Lab2BGR);
                return result;
            }
            finally
            {
                foreach (var ch in channels)
                {
                    if (!ch.IsDisposed)
                        ch.Dispose();
                }
            }
        }

        private Mat RemoveBorders_Integral(CancellationToken token,
                                                        bool batchProcessing,
                                                        Mat src, int brickThickness,
                                                        double borderColorTolerance,
                                                        int safetyOffsetPx,
                                                        BordersRemover.BrickInpaintMode inpaintMode,
                                                        double inpaintRadius,
                                                        bool autoMaxBorderDepthFrac,
                                                        BordersRemover.MaxBorderDepthsFrac maxBorderDepthsFrac,
                                                        double seedContrastStrictness,
                                                        double seedBrightnessStrictness,
                                                        double textureAllowance,
                                                        int kInterpolation,
                                                        bool cutResult,
                                                        Scalar? fillColor = null)
        {

            try
            {
                Mat result = new Mat();
                result = BordersRemover.RemoveBorders_LabBricks(token, src, brickThickness,
                                                        borderColorTolerance,
                                                        safetyOffsetPx,
                                                        inpaintMode,
                                                        inpaintRadius,
                                                        autoMaxBorderDepthFrac,
                                                        maxBorderDepthsFrac,
                                                        seedContrastStrictness,
                                                        seedBrightnessStrictness,
                                                        textureAllowance,
                                                        kInterpolation,
                                                        out var depthStats,
                                                        fillColor);
                if (cutResult && depthStats.HasAnyMeasurements)
                    return CutByAverageDepth(src, depthStats);
                return result;
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Integral Border Removal cancelled!");
#endif      
                throw;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error in Integral Border Removal: {ex.Message}");
                //Debug.WriteLine($"Error in ApplyCommand: {ex}");
                return src.Clone();
            }
        }

        private Mat CutByAverageDepth(Mat src, BordersRemover.BorderDepthStats stats)
        {
            int left = Math.Max(0, (int)Math.Round(stats.MinLeft > 0 ? stats.MinLeft : stats.AverageLeft));
            int right = Math.Max(0, (int)Math.Round(stats.MinRight > 0 ? stats.MinRight : stats.AverageRight));
            int top = Math.Max(0, (int)Math.Round(stats.MinTop > 0 ? stats.MinTop : stats.AverageTop));
            int bottom = Math.Max(0, (int)Math.Round(stats.MinBottom > 0 ? stats.MinBottom : stats.AverageBottom));

            int width = src.Cols - left - right;
            int height = src.Rows - top - bottom;

            if (width <= 0 || height <= 0)
                return src.Clone();

            var rect = new Rect(left, top, width, height);
            using var roi = new Mat(src, rect);
            return roi.Clone();
        }

        private Scalar GetBgColor(Mat src)
        {
            var bgScalar = Scalar.All(0);

            int rws = src.Rows;
            int cls = src.Cols;
            var thr = EstimateBlackThreshold(src);
            int cornerSize = Math.Max(1, Math.Min(32, Math.Min(rws, cls) / 30));
            double sb = 0, sg = 0, sr = 0; int cnt = 0;
            var rects = new[]
            {
                        new Rect(0,0,cornerSize,cornerSize),
                        new Rect(Math.Max(0,cls-cornerSize),0,cornerSize,cornerSize),
                        new Rect(0,Math.Max(0,rws-cornerSize),cornerSize,cornerSize),
                        new Rect(Math.Max(0,cls-cornerSize), Math.Max(0,rws-cornerSize), cornerSize, cornerSize)
                    };
            foreach (var r in rects)
            {
                _token.ThrowIfCancellationRequested();
                if (r.Width <= 0 || r.Height <= 0) continue;
                using var patch = new Mat(src, r);
                var mean = Cv2.Mean(patch);
                double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                if (brightness > thr * 1.0) { sb += mean.Val0; sg += mean.Val1; sr += mean.Val2; cnt++; }
            }
            if (cnt > 0) bgScalar = new Scalar(sb / cnt, sg / cnt, sr / cnt);
            return bgScalar;
        }

        private Mat DetectDocumentAndCrop(Mat src, int cropLevel, bool debug, bool batchProcessing, out Mat debugMask, out Mat debugOverlay)
        {
            debugMask = new Mat();
            debugOverlay = new Mat();
            if (_docBoundaryModel == null)
            {
                Debug.WriteLine("DocBoundaryModel is not initialized. Smart crop disabled.");
                return src.Clone();
            }

            // 1) Предикт маски
            Scalar bgColor = GetBgColor(src);
            // create new Mat with bgColor and add 20px on each side
            using Mat bigMat = new Mat(src.Rows + 40, src.Cols + 40, src.Type(), bgColor);
            var rect = new Rect(20, 20, src.Cols, src.Rows);
            using var roi = new Mat(bigMat, rect);
            src.CopyTo(roi);
            try
            {
                using Mat mask = _docBoundaryModel.PredictMask(bigMat, cropLevel, _token);
                debugMask = mask.Clone();

                // 2) Обрезка
                Mat cropped = DocumentCropper.CropByMask(bigMat, mask, out debugOverlay);

                return cropped;
            }
            catch (OperationCanceledException) when (!batchProcessing)
            {
                Debug.WriteLine("Document Detection cancelled (UI)!");
                return src.Clone();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DetectDocumentAndCrop failed: {ex}");
                debugMask = new Mat();
                debugOverlay = new Mat();
                return src.Clone();
            }
        }



        public bool TryApplyCommand(
                                ProcessorCommand command,
                                Dictionary<string, object> parameters = null,
                                bool batchProcessing = false,
                                string currentFilePath = null,
                                Action<string> log = null)
        {
            using var src = WorkingImage; // cloned inside WorkingImage getter
            var result = ProcessSingle(src, command, parameters ?? new Dictionary<string, object>(), _token, batchProcessing);
            if (result == null)
            {
                var msg = $"[{currentFilePath ?? "<unknown>"}] Command {command} returned null.";
                log?.Invoke(msg);
                Debug.WriteLine(msg);

                if (batchProcessing)
                {
                    throw new InvalidOperationException(msg);
                }

                // в UI-режиме просто не меняем картинку
                return false;
            }
            WorkingImage = result;

            return true;
        }

        private Mat RemoveBorders_Manual(Mat src, int top, int bottom, int left, int right, bool applyCut, Scalar bgColor, bool debug)
        {
            int x = left;
            int y = top;
            int width = src.Cols - left - right;
            int height = src.Rows - top - bottom;
            if (width <= 0 || height <= 0) return src.Clone();
            try
            {

                Mat result = BordersRemover.ManualCut(_token, src, x, y, width, height,
                                                        applyCut ? BordersRemover.BordersRemovalMode.Cut : BordersRemover.BordersRemovalMode.Fill,
                                                        bgColor,
                                                        debug);
                return result;
            }
            catch (OperationCanceledException)
            {
#if DEBUG
                Debug.WriteLine("Manual Cut cancelled!");
#endif  
                return src.Clone();
            }
            catch (Exception ex)
            {
#if DEBUG
                Debug.WriteLine($"Despeckle failed: {ex}");
#endif
                return src.Clone();
            }
        }


        public void UpdateCancellationToken(CancellationToken token)
        {
            _token = token;
        }

        private Mat Despeckle(Mat src, DespeckleSettings settings)
        {
            try
            {
                Mat result = Despeckler.DespeckleApplyToSource(_token, src, settings, settings.ShowDespeckleDebug, true, true);
                return result;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Despeckle cancelled!");
                return src.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Despeckle failed: {ex}");
                return src.Clone();
            }
        }

        private Mat DespeckleEffective(Mat src, DespeckleSettings settings)
        {
            try
            {
                Mat result = Despeckler.DespeckleEffective(_token, src, settings, settings.ShowDespeckleDebug, true, true);
                return result;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("DespeckleEffective cancelled!");
                return src.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DespeckleEffective failed: {ex}");
                return src.Clone();
            }
        }

        private Mat RemoveLines(Mat src,
            int lineWidthPx,
            double minLengthFraction,
            LineOrientation orientation,
            int offsetStartPx,
            int lineColorRed,
            int lineColorGreen,
            int lineColorBlue,
            int colorTolerance)
        {
            Scalar lineColorRgb = new Scalar(lineColorRed, lineColorGreen, lineColorBlue);
            bool inverColorMeaning = false;
            Mat mask;
            Mat result = LinesRemover.RemoveEdgeStripes(src,
                lineWidthPx,
                minLengthFraction,
                orientation,
                offsetStartPx,
                lineColorRgb,
                out mask,
                colorTolerance,
                inverColorMeaning);

            return result;
        }

        private Mat MajorityBinarize(Mat src, BinarizeParameters p)
        {
            int step = 10; // 5 or 10
            int range = Math.Max(0, p.MajorityOffset); // max absolute offset
            int[] deltas = BuildMajorityDeltas(range, step);

            return MajorityVotingBinarize(src, p.Threshold, deltas);
        }

        private static int[] BuildMajorityDeltas(int range, int step)
        {
            range = Math.Max(0, range);
            step = Math.Max(1, step);

            if (range == 0)
                return new[] { 0 };

            // Max multiple of step that fits into range
            int coreMax = (range / step) * step;

            // If range not divisible by step -> we add edges -range and +range
            bool addEdges = coreMax != range;

            // Core always includes 0 (because it is symmetric)
            int coreCount = (coreMax / step) * 2 + 1; // [-coreMax, ..., 0, ..., +coreMax]
            int total = addEdges ? coreCount + 2 : coreCount;

            var deltas = new int[total];
            int idx = 0;

            if (addEdges)
                deltas[idx++] = -range;

            for (int v = -coreMax; v <= coreMax; v += step)
                deltas[idx++] = v;

            if (addEdges)
                deltas[idx++] = +range;

            return deltas;
        }

        private static Mat ConvertWithChannelScalesToGray_CustomWeights(Mat src, double rPercent, double gPercent, double bPercent, out Mat? alphaMat)
        {
            alphaMat = null;
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

            double rScale = Math.Max(0.0, Math.Min(100.0, rPercent)) / 100.0;
            double gScale = Math.Max(0.0, Math.Min(100.0, gPercent)) / 100.0;
            double bScale = Math.Max(0.0, Math.Min(100.0, bPercent)) / 100.0;

            if (src.Type().Channels == 1) return src.Clone();

            Mat workBgr;
            if (src.Type().Channels == 4)
            {
                var bgra = src;
                Mat[] bgraCh = Cv2.Split(bgra);
                alphaMat = bgraCh[3].Clone();
                workBgr = new Mat();
                Cv2.Merge(new[] { bgraCh[0], bgraCh[1], bgraCh[2] }, workBgr);
                foreach (var m in bgraCh) m.Dispose();
            }
            else
            {
                workBgr = src.Clone();
            }

            Mat workF = new Mat();
            workBgr.ConvertTo(workF, MatType.CV_32F);

            // split channels
            Mat[] ch = Cv2.Split(workF); // B,G,R

            // apply channel scales
            if (ch.Length >= 3)
            {
                if (bScale != 1.0) Cv2.Multiply(ch[0], bScale, ch[0]);
                if (gScale != 1.0) Cv2.Multiply(ch[1], gScale, ch[1]);
                if (rScale != 1.0) Cv2.Multiply(ch[2], rScale, ch[2]);
            }

            // compute weighted sum: grayF = 0.114*B + 0.587*G + 0.299*R
            Mat grayF = new Mat();
            // grayF = 0.114*B
            Cv2.Multiply(ch[0], 0.114, grayF);
            // grayF += 0.587*G
            Mat tmp = new Mat();
            Cv2.Multiply(ch[1], 0.587, tmp);
            Cv2.Add(grayF, tmp, grayF);
            tmp.Dispose();
            // grayF += 0.299*R
            Cv2.Multiply(ch[2], 0.299, tmp);
            Cv2.Add(grayF, tmp, grayF);
            tmp.Dispose();

            // cleanup channels & workF
            foreach (var m in ch) m.Dispose();
            workF.Dispose();
            workBgr.Dispose();

            // convert float gray to 8U (saturate)
            Mat grayU8 = new Mat();
            grayF.ConvertTo(grayU8, MatType.CV_8U);
            grayF.Dispose();

            return grayU8;
        }

        private Mat PunchHolesRemove(Mat src, List<PunchSpec> specs, double roundness, double fillRatio, Offsets offsets)
        {
            if (src == null || src.Empty()) return new Mat();
            try
            {
                return PunchHoleRemover.RemovePunchHoles(_token, src, specs, roundness, fillRatio, offsets.top, offsets.bottom, offsets.left, offsets.right);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("PunchHoles Removal cancelled!");
                return src.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return src.Clone();
            }

        }




        private void ExecutePageSplitPreview(Mat src, Dictionary<string, object> parameters)
        {
            if (_appManager == null)
                return;

            try
            {
                string method = "Auto";
                if (parameters != null && parameters.TryGetValue("splitMethod", out var methodObj))
                    method = methodObj?.ToString() ?? "Auto";

                var splitter = new PageSplitter(BuildPageSplitterSettings(parameters));
                PageSplitter.SplitResult? result = null;
                try
                {
                    if (method.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                    {
                        double cutLinePercent = 50.0;
                        int overlapPx = 0;
                        if (parameters != null)
                        {
                            if (parameters.TryGetValue("manualCutLinePercent", out var cutObj))
                                cutLinePercent = SafeDouble(cutObj, cutLinePercent);
                            if (parameters.TryGetValue("manualOverlapPx", out var overlapObj))
                                overlapPx = SafeInt(overlapObj, overlapPx);
                        }
                        result = splitter.SplitManual(src, cutLinePercent, overlapPx, _token);
                    }
                    else
                    {
                        result = splitter.SplitAuto(src, false, _token);
                    }
                    if (result.Success && result.Left != null && result.Right != null)
                    {
                        var leftBmp = MatToBitmapSource(result.Left);
                        var rightBmp = MatToBitmapSource(result.Right);
                        _appManager.SetSplitPreviewImages(leftBmp, rightBmp);
                    }
                    else
                    {
                        _appManager.ClearSplitPreviewImages();
                        var reason = result?.Reason;
                        if (!string.IsNullOrWhiteSpace(reason))
                            ErrorOccured?.Invoke($"Page split failed: {reason}");
                    }
                }
                finally
                {
                    result?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                _appManager.ClearSplitPreviewImages();
            }
            catch (Exception ex)
            {
                _appManager.ClearSplitPreviewImages();
                ErrorOccured?.Invoke($"Page split failed: {ex.Message}");
            }
        }
        private void ExecutePageSplitBatch(Mat src, Dictionary<string, object> parameters)
        {
            ClearSplitResults();
            try
            {
                string method = "Auto";
                if (parameters != null && parameters.TryGetValue("splitMethod", out var methodObj))
                    method = methodObj?.ToString() ?? "Auto";

                var splitter = new PageSplitter(BuildPageSplitterSettings(parameters));
                PageSplitter.SplitResult? result = null;
                try
                {
                    if (method.Equals("Manual", StringComparison.OrdinalIgnoreCase))
                    {
                        double cutLinePercent = 50.0;
                        int overlapPx = 0;
                        if (parameters != null)
                        {
                            if (parameters.TryGetValue("manualCutLinePercent", out var cutObj))
                                cutLinePercent = SafeDouble(cutObj, cutLinePercent);
                            if (parameters.TryGetValue("manualOverlapPx", out var overlapObj))
                                overlapPx = SafeInt(overlapObj, overlapPx);
                        }
                        result = splitter.SplitManual(src, cutLinePercent, overlapPx, _token);
                    }
                    else
                    {
                        result = splitter.SplitAuto(src, false, _token);
                    }
                    if (result.Success && result.Left != null && result.Right != null)
                    {
                        var left = result.Left;
                        var right = result.Right;
                        result.Left = null;
                        result.Right = null;
                        SetSplitResults(left, right);
                    }
                    else
                    {
                        var reason = result?.Reason;
                        ErrorOccured?.Invoke($"Page split failed: {reason ?? "Unknown reason"}");
                    }
                }
                finally
                {
                    result?.Dispose();
                }
            }
            catch (OperationCanceledException)
            {
                ClearSplitResults();
                throw;
            }
            catch (Exception ex)
            {
                ClearSplitResults();
                ErrorOccured?.Invoke($"Page split failed: {ex.Message}");
            }
        }
        private PageSplitter.Settings BuildPageSplitterSettings(Dictionary<string, object> parameters)
        {
            var settings = new PageSplitter.Settings();

            if (parameters != null)
            {
                bool hasPadPercent = false;
                if (parameters.TryGetValue("padPercent", out var padPercentObj))
                {
                    settings.PadPercent = SafeDouble(padPercentObj, settings.PadPercent);
                    hasPadPercent = true;
                }

                if (!hasPadPercent && parameters.TryGetValue("padPx", out var padObj))
                {
                    settings.PadPx = SafeInt(padObj, settings.PadPx);
                    settings.PadPercent = 0.0;
                }

                if (parameters.TryGetValue("centralBandStart", out var bandStartObj))
                    settings.CentralBandStart = SafeDouble(bandStartObj, settings.CentralBandStart);
                if (parameters.TryGetValue("centralBandEnd", out var bandEndObj))
                    settings.CentralBandEnd = SafeDouble(bandEndObj, settings.CentralBandEnd);
                if (parameters.TryGetValue("analysisMaxWidth", out var analysisObj))
                    settings.AnalysisMaxWidth = SafeInt(analysisObj, settings.AnalysisMaxWidth);
                if (parameters.TryGetValue("useClahe", out var useClaheObj))
                    settings.UseClahe = SafeBool(useClaheObj, settings.UseClahe);
                if (parameters.TryGetValue("claheClipLimit", out var clipObj))
                    settings.ClaheClipLimit = SafeDouble(clipObj, settings.ClaheClipLimit);
                if (parameters.TryGetValue("claheTileGrid", out var gridObj))
                    settings.ClaheTileGrid = SafeInt(gridObj, settings.ClaheTileGrid);
                if (parameters.TryGetValue("adaptiveBlockSize", out var blockObj))
                    settings.AdaptiveBlockSize = SafeInt(blockObj, settings.AdaptiveBlockSize);
                if (parameters.TryGetValue("adaptiveC", out var cObj))
                    settings.AdaptiveC = SafeDouble(cObj, settings.AdaptiveC);
                if (parameters.TryGetValue("closeKernelWidthFrac", out var kwObj))
                    settings.CloseKernelWidthFrac = SafeDouble(kwObj, settings.CloseKernelWidthFrac);
                if (parameters.TryGetValue("closeKernelHeightPx", out var khObj))
                    settings.CloseKernelHeightPx = SafeInt(khObj, settings.CloseKernelHeightPx);
                if (parameters.TryGetValue("smoothWindowPx", out var smoothObj))
                    settings.SmoothWindowPx = SafeInt(smoothObj, settings.SmoothWindowPx);
                if (parameters.TryGetValue("useLabConfirmation", out var labObj))
                    settings.UseLabConfirmation = SafeBool(labObj, settings.UseLabConfirmation);
                if (parameters.TryGetValue("labGutterHalfWidthPx", out var gutterObj))
                    settings.LabGutterHalfWidthPx = SafeInt(gutterObj, settings.LabGutterHalfWidthPx);
                if (parameters.TryGetValue("labNeighborWidthPx", out var neighborObj))
                    settings.LabNeighborWidthPx = SafeInt(neighborObj, settings.LabNeighborWidthPx);
                if (parameters.TryGetValue("minLDiff", out var minLDiffObj))
                    settings.MinLDiff = SafeDouble(minLDiffObj, settings.MinLDiff);
                if (parameters.TryGetValue("maxGutterStdRatio", out var ratioObj))
                    settings.MaxGutterStdRatio = SafeDouble(ratioObj, settings.MaxGutterStdRatio);
                if (parameters.TryGetValue("weightProjection", out var weightProjObj))
                    settings.WeightProjection = SafeDouble(weightProjObj, settings.WeightProjection);
                if (parameters.TryGetValue("weightLab", out var weightLabObj))
                    settings.WeightLab = SafeDouble(weightLabObj, settings.WeightLab);
            }

            return settings;
        }
        public Mat[]? GetSplitResults()
        {
            lock (_splitLock)
            {
                if (_splitWorkingImages == null)
                    return null;

                var clones = new List<Mat>(_splitWorkingImages.Length);
                foreach (var mat in _splitWorkingImages)
                {
                    if (mat == null || mat.IsDisposed)
                        continue;
                    if (mat.Empty())
                        continue;
                    clones.Add(mat.Clone());
                }

                return clones.Count > 0 ? clones.ToArray() : null;
            }
        }
        public void ClearSplitResults()
        {
            lock (_splitLock)
            {
                ClearSplitResultsUnsafe();
            }
        }
        private void SetSplitResults(Mat left, Mat right)
        {
            if (left == null || right == null)
                return;

            lock (_splitLock)
            {
                ClearSplitResultsUnsafe();
                _splitWorkingImages = new[] { left, right };
            }
        }
        private void ClearSplitResultsUnsafe()
        {
            if (_splitWorkingImages == null)
                return;

            foreach (var mat in _splitWorkingImages)
            {
                mat?.Dispose();
            }

            _splitWorkingImages = null;
        }




        private Mat SmartCrop(Mat src, TextDetectionSettings tds, bool debug = true)
        {

            string eastPath = Path.Combine(AppContext.BaseDirectory, "Models", "frozen_east_text_detection.pb");
            string tessData = Path.Combine(AppContext.BaseDirectory, "tessdata");
            string tessLang = "eng"; // или "eng"
            var cropper = new TextAwareCropper(_token, eastPath, tessData, tessLang);
            //var cropped = cropper.CropKeepingText(src);
            Mat cropped = new Mat();
            try
            {
                if (true)
                {
                    cropped = cropper.ShowDetectedAreas(src, tds, debug);
                }
                else
                {
                    //cropped = cropper.CropKeepingText(src, eastInputWidth, eastInputHeight,
                    //                               eastScoreThreshold, eastNmsThreshold,
                    //                               tesseractMinConfidence,
                    //                               paddingPx,
                    //                               downscaleMaxWidth);
                }

            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Smart Crop (EAST) cancelled!");
                return src.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while performing EAST Smart crop {ex}");
                return src.Clone();
            }

            return cropped;
        }



        private Stream MatToStream(Mat mat)
        {
            BitmapSource bmpSource = MatToBitmapSource(mat);
            return BitmapSourceToStream(bmpSource);
        }

        // Safe Mat -> BitmapSource conversion (no use of .Depth or non-existent members)
        private BitmapSource MatToBitmapSource(Mat mat)
        {
            if (mat == null || mat.Empty()) return null!;

            //using var mat = matOrg.Clone();

            // Desired types: 8-bit single/three/four channels
            MatType desiredType;
            PixelFormat pixelFormat;

            // If mat already one of CV_8UC1/CV_8UC3/CV_8UC4 we will use that exact layout
            if (mat.Type() == MatType.CV_8UC1)
            {
                desiredType = MatType.CV_8UC1;
                pixelFormat = PixelFormats.Gray8;
            }
            else if (mat.Type() == MatType.CV_8UC3)
            {
                desiredType = MatType.CV_8UC3;
                pixelFormat = PixelFormats.Bgr24; // OpenCV BGR -> Bgr24
            }
            else if (mat.Type() == MatType.CV_8UC4)
            {
                desiredType = MatType.CV_8UC4;
                pixelFormat = PixelFormats.Bgra32; // OpenCV BGRA -> Bgra32
            }
            else
            {
                // Not one of the exact desired types: decide target based on channels
                int ch = mat.Channels();
                if (ch == 1) { desiredType = MatType.CV_8UC1; pixelFormat = PixelFormats.Gray8; }
                else if (ch == 3) { desiredType = MatType.CV_8UC3; pixelFormat = PixelFormats.Bgr24; }
                else if (ch == 4) { desiredType = MatType.CV_8UC4; pixelFormat = PixelFormats.Bgra32; }
                else
                {
                    // fallback: convert to 3-channel 8-bit BGR
                    desiredType = MatType.CV_8UC3;
                    pixelFormat = PixelFormats.Bgr24;
                }
            }

            // Create a view that matches desiredType exactly (8-bit depth + correct channels).
            // We will create a new Mat 'view' only if conversion is needed.
            Mat view = mat;
            bool viewOwned = false;

            // If mat.Type() already equals desiredType we can use it directly.
            if (mat.Type() != desiredType)
            {
                // We need to produce an 8-bit Mat with correct number of channels.
                // Two-step robust approach:
                // 1) If depth != 8-bit, first convert depth to 8-bit keeping channels
                // 2) Then, if channels differ, use CvtColor to change channels

                // Step 1: ensure 8-bit depth (CV_8U) while keeping channels
                Mat step1 = mat;
                bool step1Owned = false;

                // Mat.Type() encodes both depth & channels; if depth isn't 8U, convert to CV_8UC(channels)
                // We check whether current mat is an 8-bit type by comparing against any CV_8UC*
                bool isCurrently8Bit =
                    mat.Type() == MatType.CV_8UC1 ||
                    mat.Type() == MatType.CV_8UC3 ||
                    mat.Type() == MatType.CV_8UC4;

                if (!isCurrently8Bit)
                {
                    step1 = new Mat();
                    // ConvertTo will clamp/scale values; choose CV_8UC<channels>
                    mat.ConvertTo(step1, MatType.CV_8UC(mat.Channels()));
                    step1Owned = true;
                }

                // Step 2: if channels mismatch -> CvtColor from step1 -> view
                if (step1.Channels() == (desiredType.Channels))
                {
                    // Same channel count -> just convert type (if needed)
                    view = new Mat();
                    step1.ConvertTo(view, desiredType);
                    viewOwned = true;
                }
                else
                {
                    view = new Mat();
                    // Cases: 1->3, 1->4, 3->1, 3->4, 4->3
                    if (step1.Channels() == 1 && desiredType.Channels == 3)
                        Cv2.CvtColor(step1, view, ColorConversionCodes.GRAY2BGR);
                    else if (step1.Channels() == 1 && desiredType.Channels == 4)
                        Cv2.CvtColor(step1, view, ColorConversionCodes.GRAY2BGRA);
                    else if (step1.Channels() == 3 && desiredType.Channels == 1)
                        Cv2.CvtColor(step1, view, ColorConversionCodes.BGR2GRAY);
                    else if (step1.Channels() == 3 && desiredType.Channels == 4)
                        Cv2.CvtColor(step1, view, ColorConversionCodes.BGR2BGRA);
                    else if (step1.Channels() == 4 && desiredType.Channels == 3)
                        Cv2.CvtColor(step1, view, ColorConversionCodes.BGRA2BGR);
                    else
                    {
                        // fallback: try direct ConvertTo to desiredType
                        step1.ConvertTo(view, desiredType);
                    }
                    viewOwned = true;
                }

                if (step1Owned) step1.Dispose();
            }

            try
            {
                int width = view.Cols;
                int height = view.Rows;
                int stride = (int)view.Step(); // use real step (may include padding)
                long bufferSizeLong = (long)stride * height;
                if (bufferSizeLong > int.MaxValue) throw new NotSupportedException("Image too large for WriteableBitmap.");

                int bufferSize = (int)bufferSizeLong;
                IntPtr dataPtr = view.Data;

                var wb = new WriteableBitmap(width, height, 96, 96, pixelFormat, null);
                var rect = new System.Windows.Int32Rect(0, 0, width, height);
                wb.WritePixels(rect, dataPtr, bufferSize, stride);
                wb.Freeze();
                return wb;
            }
            finally
            {
                if (viewOwned && view != null && !view.IsDisposed) view.Dispose();
            }
        }




        public static Scalar SampleCentralGrayScalar(Mat src, int sampleSizePx = 0, double sampleFraction = 0.10)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Scalar(255, 255, 255); // fallback white

            int w = src.Cols;
            int h = src.Rows;
            int minSide = Math.Min(w, h);

            // choose sample side
            int side;
            if (sampleSizePx > 0)
            {
                side = Math.Max(1, Math.Min(sampleSizePx, minSide));
            }
            else
            {
                double frac = double.IsNaN(sampleFraction) || sampleFraction <= 0 ? 0.10 : sampleFraction;
                side = Math.Max(1, (int)Math.Round(minSide * frac));
            }

            // center roi
            int cx = w / 2;
            int cy = h / 2;
            int rx = Math.Max(0, cx - side / 2);
            int ry = Math.Max(0, cy - side / 2);
            int rw = Math.Min(side, w - rx);
            int rh = Math.Min(side, h - ry);
            if (rw <= 0 || rh <= 0) return new Scalar(255, 255, 255);

            var roi = new Rect(rx, ry, rw, rh);

            // extract roi
            using var sample = new Mat(src, roi);

            // compute mean
            Scalar mean = Cv2.Mean(sample); // returns (B, G, R, A)

            double gray;
            if (sample.Channels() == 1)
            {
                // mean.Val0 already gray
                gray = mean.Val0;
            }
            else
            {
                // OpenCV mean is BGR order in Scalar (Val0=B, Val1=G, Val2=R)
                double b = mean.Val0;
                double g = mean.Val1;
                double r = mean.Val2;
                gray = 0.299 * r + 0.587 * g + 0.114 * b;
            }

            // clamp and round
            int gInt = (int)Math.Round(Math.Max(0.0, Math.Min(255.0, gray)));

            return new Scalar(gInt, gInt, gInt);
        }




        private Mat MatToGray(Mat src)
        {
            if (src == null || src.Empty()) return null; // уже в градациях серого
            var gray = new Mat();
            try
            {
                switch (src.Channels())
                {
                    case 1:
                        // уже в градациях серого
                        src.CopyTo(gray);
                        break;
                    case 3:
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                        break;
                    case 4:
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                        break;
                    default:
                        throw new ArgumentException("MatToGray supports only 1, 3, or 4 channel Mats", nameof(src));
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MatToGray error: {ex}");
                return gray;
            }


            return gray;
        }

        private Mat MajorityVotingBinarize(Mat srcColor, int baseThreshold = -1, int[] deltas = null)
        {
            if (srcColor == null) throw new ArgumentNullException(nameof(srcColor));
            if (deltas == null) deltas = new[] { -20, 0, 20 };

            using var gray = new Mat();
            if (srcColor.Channels() == 3)
                Cv2.CvtColor(srcColor, gray, ColorConversionCodes.BGR2GRAY);
            else if (srcColor.Channels() == 4)
                Cv2.CvtColor(srcColor, gray, ColorConversionCodes.BGRA2GRAY);
            else
                srcColor.CopyTo(gray);

            int rows = gray.Rows, cols = gray.Cols;
            int n = deltas.Length;

            // base threshold: Otsu by default (more stable for documents)
            int baseT = baseThreshold;
            if (baseT <= 0)
            {
                using var tmp = new Mat();
                double otsuVal = Cv2.Threshold(gray, tmp, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                baseT = (int)Math.Round(otsuVal);
            }

            // accumulator: use 16-bit to be safe
            Mat acc = Mat.Zeros(gray.Size(), MatType.CV_16UC1);
            var binList = new List<Mat>(n);

            for (int i = 0; i < n; i++)
            {
                int t = Math.Max(0, Math.Min(255, baseT + deltas[i]));
                var b = new Mat();

                // BinaryInv -> text (dark) becomes 255 (foreground), background 0
                Cv2.Threshold(gray, b, t, 255, ThresholdTypes.BinaryInv);

                // Convert 0/255 -> 0/1 in 16-bit and add to acc
                using var mask01 = new Mat();
                b.ConvertTo(mask01, MatType.CV_16UC1, 1.0 / 255.0); // now 0 or 1 in CV_16U
                Cv2.Add(acc, mask01, acc);

                binList.Add(b); // store with text==255
            }

            // majority: need = ceil(n/2)
            int need = (n + 1) / 2;
            using var maskNeed16 = new Mat();
            Cv2.Threshold(acc, maskNeed16, need - 1, 255, ThresholdTypes.Binary); // CV_16U, 0/255

            using var maskNeed = new Mat();
            maskNeed16.ConvertTo(maskNeed, MatType.CV_8UC1); // 0/255 8-bit

            // result single-channel: default white (255), then set black (0) where maskNeed
            var result = new Mat(rows, cols, MatType.CV_8UC1, Scalar.All(255));
            result.SetTo(new Scalar(0), maskNeed); // black where majority says text

            // compute median height from the middle bin (exists because binList has text==255)
            int midIdx = Array.IndexOf(deltas, 0);
            if (midIdx < 0) midIdx = n / 2;
            Mat refBin = binList[midIdx];

            int medianH = 20;
            using (var lbl = new Mat())
            using (var stats = new Mat())
            using (var cents = new Mat())
            {
                int compN = Cv2.ConnectedComponentsWithStats(refBin, lbl, stats, cents, PixelConnectivity.Connectivity8, MatType.CV_32S);
                var heights = new List<int>();
                for (int i = 1; i < compN; i++)
                {
                    int h = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);
                    if (h >= 1) heights.Add(h);
                }
                if (heights.Count > 0)
                {
                    heights.Sort();
                    medianH = heights[heights.Count / 2];
                }
            }

            int dotMax = Math.Max(1, (int)Math.Round(medianH * 0.35));
            int dotAreaMax = Math.Max(4, dotMax * dotMax);

            // unionSmall: collect small components from each bin (binList have text==255)
            var unionSmall = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
            for (int bi = 0; bi < binList.Count; bi++)
            {
                using var lbl = new Mat();
                using var stats = new Mat();
                using var cents = new Mat();
                int compN = Cv2.ConnectedComponentsWithStats(binList[bi], lbl, stats, cents, PixelConnectivity.Connectivity8, MatType.CV_32S);

                for (int ci = 1; ci < compN; ci++)
                {
                    int area = stats.Get<int>(ci, (int)ConnectedComponentsTypes.Area);
                    int h = stats.Get<int>(ci, (int)ConnectedComponentsTypes.Height);
                    int w = stats.Get<int>(ci, (int)ConnectedComponentsTypes.Width);

                    if (h <= dotMax && w <= dotMax && area <= dotAreaMax)
                    {
                        var tmp = new Mat();
                        Cv2.InRange(lbl, new Scalar(ci), new Scalar(ci), tmp);

                        var newUnion = new Mat();
                        Cv2.BitwiseOr(unionSmall, tmp, newUnion);

                        unionSmall.Dispose();
                        unionSmall = newUnion; // теперь newUnion живёт дальше — освободите unionSmall позже
                        tmp.Dispose();
                    }
                }
            }

            // unionSmall now marks small foreground components (255) -> set those pixels black (0) in result
            result.SetTo(new Scalar(0), unionSmall);

            // convert single-channel result (text==0) into 3-channel BGR to be compatible with pipeline
            var colorResult = new Mat();
            Cv2.CvtColor(result, colorResult, ColorConversionCodes.GRAY2BGR);

            // cleanup
            acc.Dispose();
            unionSmall.Dispose();
            foreach (var b in binList) b.Dispose();
            result.Dispose();

            // return 3-channel image (BGR) — pipeline expects 3 channels
            return colorResult;
        }

        public static Deskewer.Parameters ParseParametersSimple(Dictionary<string, object>? parameters)
        {
            const int defaultCanny1 = 50;
            const int defaultCanny2 = 150;
            const int defaultMorphKernel = 5;
            const int defaultPerspectiveStrength = 5;
            const int defaultHoughThreshold = 80;
            const int defaultMinLineLength = 200;
            const int defaultMaxLineGap = 20;
            const double defaultProjMinAngle = -15.0;
            const double defaultProjMaxAngle = 15.0;
            const double defaultProjCoarseStep = 1.0;
            const double defaultProjRefineStep = 0.2;

            var result = new Deskewer.Parameters
            {
                Method = Deskewer.DeskewMethod.Auto,
                byBorders = false,
                cTresh1 = defaultCanny1,
                cTresh2 = defaultCanny2,
                morphKernel = defaultMorphKernel,
                perspectiveStrength = defaultPerspectiveStrength,
                houghTreshold = defaultHoughThreshold,
                minLineLength = defaultMinLineLength,
                maxLineGap = defaultMaxLineGap,
                projMinAngle = defaultProjMinAngle,
                projMaxAngle = defaultProjMaxAngle,
                projCoarseStep = defaultProjCoarseStep,
                projRefineStep = defaultProjRefineStep
            };

            if (parameters == null || parameters.Count == 0) return result;

            int SafeInt(object? v, int def)
            {
                if (v == null) return def;
                try
                {
                    // Convert handles boxed numeric types and numeric strings
                    return Convert.ToInt32(v);
                }
                catch
                {
                    // fallback: try parsing as double then round
                    try
                    {
                        if (double.TryParse(v.ToString(), System.Globalization.NumberStyles.Any,
                                             System.Globalization.CultureInfo.InvariantCulture, out var d))
                            return (int)Math.Round(d);
                    }
                    catch { }
                    return def;
                }
            }

            double SafeDouble(object? v, double def)
            {
                if (v == null) return def;
                try
                {
                    return Convert.ToDouble(v, CultureInfo.InvariantCulture);
                }
                catch
                {
                    if (double.TryParse(v.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                        return d;
                    return def;
                }
            }

            foreach (var kv in parameters)
            {
                if (kv.Key == null) continue;

                switch (kv.Key) // keys are exact (case-sensitive) as you requested
                {
                    case "deskewAlgorithm":
                        {
                            var s = kv.Value?.ToString() ?? "Auto";
                            if (s.Equals("ByBorders", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.ByBorders;
                                result.byBorders = true;
                            }
                            else if (s.Equals("Hough", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.Hough;
                                result.byBorders = false;
                            }
                            else if (s.Equals("Projection", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.Projection;
                                result.byBorders = false;
                            }
                            else if (s.Equals("PCA", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.PCA;
                                result.byBorders = false;
                            }
                            else if (s.Equals("Moments", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.Moments;
                                result.byBorders = false;
                            }
                            else if (s.Equals("Perspective", StringComparison.OrdinalIgnoreCase))
                            {
                                result.Method = Deskewer.DeskewMethod.Perspective;
                                result.byBorders = false;
                            }
                            else
                            {
                                result.Method = Deskewer.DeskewMethod.Auto;
                                result.byBorders = false;
                            }
                        }
                        break;

                    case "cannyTresh1":
                        result.cTresh1 = SafeInt(kv.Value, result.cTresh1);
                        break;

                    case "cannyTresh2":
                        result.cTresh2 = SafeInt(kv.Value, result.cTresh2);
                        break;

                    case "morphKernel":
                        result.morphKernel = Math.Max(1, SafeInt(kv.Value, result.morphKernel));
                        break;
                    case "perspectiveStrength":
                        result.perspectiveStrength = Math.Max(0, Math.Min(10, SafeInt(kv.Value, result.perspectiveStrength)));
                        break;

                    case "minLineLength":
                        {
                            int v = SafeInt(kv.Value, result.minLineLength);
                            result.minLineLength = Math.Max(0, v);
                        }
                        break;

                    case "houghTreshold":
                        result.houghTreshold = SafeInt(kv.Value, result.houghTreshold);
                        break;

                    case "maxLineGap":
                        result.maxLineGap = SafeInt(kv.Value, result.maxLineGap);
                        break;

                    case "projMinAngle":
                        result.projMinAngle = SafeDouble(kv.Value, result.projMinAngle);
                        break;

                    case "projMaxAngle":
                        result.projMaxAngle = SafeDouble(kv.Value, result.projMaxAngle);
                        break;

                    case "projCoarseStep":
                        result.projCoarseStep = SafeDouble(kv.Value, result.projCoarseStep);
                        break;

                    case "projRefineStep":
                        result.projRefineStep = SafeDouble(kv.Value, result.projRefineStep);
                        break;

                    default:
                        // unknown key: ignore
                        break;
                }
            }

            if (result.Method == Deskewer.DeskewMethod.Auto)
            {
                result.byBorders = false;
                result.cTresh1 = defaultCanny1;
                result.cTresh2 = defaultCanny2;
                result.morphKernel = defaultMorphKernel;
                result.perspectiveStrength = defaultPerspectiveStrength;
                result.houghTreshold = defaultHoughThreshold;
                result.minLineLength = defaultMinLineLength;
                result.maxLineGap = defaultMaxLineGap;
                result.projMinAngle = defaultProjMinAngle;
                result.projMaxAngle = defaultProjMaxAngle;
                result.projCoarseStep = defaultProjCoarseStep;
                result.projRefineStep = defaultProjRefineStep;
            }

            return result;
        }

        public Mat NewDeskew(Mat src, Dictionary<string, object> parameters)
        {
            if (src == null || src.Empty()) return new Mat();


            var p = new Deskewer.Parameters();

            p = ParseParametersSimple(parameters);
            try
            {
                Mat result = Deskewer.Deskew(_token, p.Method, src, p.cTresh1, p.cTresh2, p.morphKernel, p.minLineLength, p.houghTreshold, p.maxLineGap, p.projMinAngle, p.projMaxAngle, p.projCoarseStep, p.projRefineStep, p.perspectiveStrength);
                return result;
            }
            catch (OperationCanceledException)
            {
                string logMessage = "Deskew operation cancelled.";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while Deskew: {ex.Message}");
                return src.Clone();
            }

        }


        private Mat RemoveBordersByContours(Mat src,
                                                int cannyLow,
                                                int cannyHigh,
                                                int morphKernel,
                                                double minAreaFrac,
                                                int paddingPx,
                                                BordersRemovalMode mode,
                                                Scalar? bgColor = null)
        {
            try
            {
                var result = BordersRemover.RemoveBordersByContours(_token, src, cannyLow, cannyHigh, morphKernel, minAreaFrac, paddingPx, mode, bgColor);
                return result;
            }
            catch (OperationCanceledException)
            {
                string logMessage = "Border removal (By contours) cancelled.";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
            catch (Exception ex)
            {
                string logMessage = $"Error while removing borders (By contours): {ex.Message}";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
        }

        private Mat RemoveBordersByRowColWhite(Mat src,
                                                double threshFrac,
                                                int contrastThr,
                                                double centralSample,
                                                double maxRemoveFrac)
        {
            try
            {
                Mat result = BordersRemover.RemoveBordersByRowColWhite(_token, src, threshFrac, contrastThr, centralSample, maxRemoveFrac);
                return result;
            }
            catch (OperationCanceledException)
            {
                string logMessage = "Border removal (By contrast) cancelled.";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
            catch (Exception ex)
            {
                string logMessage = $"Error while removing borders (By contrast): {ex.Message}";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
        }

        private Mat RemoveBorders_Auto(Mat src, byte darkThresh, Scalar? bgColor, int minAreaPx, double minSpanFraction, double solidityThreshold,
                                    double minDepthFraction, int featherPx, bool useTeleaHybrid)
        {
            try
            {
                Mat result = BordersRemover.RemoveBorderArtifactsGeneric_Safe(_token, src,
                                                    darkThresh,
                                                    bgColor,
                                                    minAreaPx,
                                                    minSpanFraction,
                                                    solidityThreshold,
                                                    minDepthFraction,
                                                    featherPx,
                                                    useTeleaHybrid
                                                );
                return result;
            }
            catch (OperationCanceledException)
            {
                string logMessage = "Border removal (Auto) cancelled.";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
            catch (Exception ex)
            {
                string logMessage = $"Error while removing borders (Auto): {ex.Message}";
                Debug.WriteLine(logMessage);
                return src.Clone();
            }
        }

        // Возвращает порог (0..255). Использует яркостный канал Y (BGR->YCrCb -> channel 0).
        // marginPercent — вырезаем рамку, чтобы не учитывать чёрные поля; 10% обычно ок.
        // shiftFactor — сколько смещаем порог в сторону текста (0..1). 0.0 = ровно середина между mean(text) и mean(bg).
        private byte EstimateBlackThreshold(Mat img, int marginPercent = 10, double shiftFactor = 0.25)
        {
            if (img == null) throw new ArgumentNullException(nameof(img));
            if (img.Empty()) return 16; // fallback

            // 1) работаем с копией по яркости
            using var tmp = new Mat();
            if (img.Channels() == 3)
            {
                // BGR -> YCrCb, берем Y (яркость)
                using var ycrcb = new Mat();
                Cv2.CvtColor(img, ycrcb, ColorConversionCodes.BGR2YCrCb);
                Cv2.ExtractChannel(ycrcb, tmp, 0);
            }
            else
            {
                Cv2.CvtColor(img, tmp, ColorConversionCodes.GRAY2BGR);
                Cv2.CvtColor(tmp, tmp, ColorConversionCodes.BGR2GRAY);
            }

            // 2) вырежем центральную область (чтобы не учитывать чёрную рамку)
            int w = tmp.Width, h = tmp.Height;
            int mx = (int)(w * (marginPercent / 100.0));
            int my = (int)(h * (marginPercent / 100.0));
            int cw = Math.Max(8, w - mx * 2);
            int ch = Math.Max(8, h - my * 2);
            var cropRect = new Rect(mx, my, cw, ch);

            using var crop = new Mat(tmp, cropRect);

            // 3) сгладим немного, чтобы уменьшить шум
            Cv2.GaussianBlur(crop, crop, new OpenCvSharp.Size(3, 3), 0);

            // 4) Otsu на центральной области — возвращает порог (double)
            using var bin = new Mat();
            double otsuThr = Cv2.Threshold(crop, bin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            // Otsu выбирает порог разделения на тёмные/светлые. Мы хотим узнать средние по группам.

            // 5) посчитаем среднюю яркость для двух групп: <=otsuThr (textCandidate) и >otsuThr (bgCandidate)
            // Притерпимся к случаям, когда одна из групп пуста
            double sumText = 0, sumBg = 0;
            int cntText = 0, cntBg = 0;

            for (int y = 0; y < crop.Rows; y++)
            {
                for (int x = 0; x < crop.Cols; x++)
                {
                    byte v = crop.At<byte>(y, x);
                    if (v <= otsuThr) { sumText += v; cntText++; }
                    else { sumBg += v; cntBg++; }
                }
            }

            // если одна из групп пустая — fallback к простому подходу
            if (cntText == 0 || cntBg == 0)
            {
                // если всё слишком светлое или тёмное, используем умеренный порог
                int fallback = 40;
                return (byte)fallback;
            }

            double meanText = sumText / cntText;
            double meanBg = sumBg / cntBg;

            // 6) выберем порог между meanText и meanBg, смещая его к тексту методом shiftFactor
            // shiftFactor 0 = середина, 0.5 = ближе к фону,  - но мы берем 0..1: 0 => midpoint, >0 смещает в сторону текста (консервативнее)
            double thr = meanText + (meanBg - meanText) * (0.5 - shiftFactor);
            // ограничим в диапазоне
            thr = Math.Min(250, Math.Max(1, thr));
            return (byte)Math.Round(thr);
        }

        private Mat RotateImageForDetection(Mat srcGrayBinary, double angle)
        {
            // Поворот без смены размера (вырезаем белые поля при подсчётах — это ok для оценки)
            var center = new Point2f(srcGrayBinary.Width / 2f, srcGrayBinary.Height / 2f);
            var M = Cv2.GetRotationMatrix2D(center, angle, 1.0);
            var dst = new Mat();
            Cv2.WarpAffine(srcGrayBinary, dst, M, new OpenCvSharp.Size(srcGrayBinary.Width, srcGrayBinary.Height),
                           InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(0)); // задний фон = 0 (т.к. текст = белый/255)
            return dst;
        }

        public double GetSkewAngleByHough(Mat src,
            double cannyThresh1 = 50, double cannyThresh2 = 150,
            int houghThreshold = 100, double minLineLength = 100, double maxLineGap = 10)
        {
            // Работает на уменьшенной копии для скорости
            int maxDetectWidth = 1000;
            Mat small = src.Width > maxDetectWidth ? src.Resize(new OpenCvSharp.Size(maxDetectWidth, (int)(src.Height * (maxDetectWidth / (double)src.Width)))) : src.Clone();

            using var gray = new Mat();
            Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

            // Убираем шум (блюр)
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(5, 5), 0);

            // Края
            using var edges = new Mat();
            Cv2.Canny(gray, edges, cannyThresh1, cannyThresh2);

            // HoughLinesP - детектим сегменты
            LineSegmentPoint[] lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, houghThreshold, minLineLength, maxLineGap);

            if (lines == null || lines.Length == 0)
            {
                small.Dispose();
                return double.NaN; // не найдено
            }

            // Собираем углы линий в градусах (от -90 до +90)
            var angles = new List<double>(lines.Length);
            foreach (var l in lines)
            {
                double dx = l.P2.X - l.P1.X;
                double dy = l.P2.Y - l.P1.Y;
                if (Math.Abs(dx) < 1e-6) continue;
                double angle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                // для горизонтальных текстовых строк обычно ожидаем угол около 0
                // нормализуем в диапазон [-90, 90]
                if (angle > 90) angle -= 180;
                if (angle <= -90) angle += 180;
                angles.Add(angle);
            }

            small.Dispose();

            if (angles.Count == 0) return double.NaN;

            // Медиана более устойчива к шуму, но можно брать модальное значение (binning) — медиана обычно хороша.
            angles.Sort();
            double median = angles[angles.Count / 2];

            // Иногда Hough ловит вертикальные/вертикально-поперечные линии — ограничим по модулю (например ±45)
            if (Math.Abs(median) > 45) return double.NaN;

            // Возвращаем угол, который нужно применить (обычно -median для выравнивания)
            return -median;
        }




    }
}
