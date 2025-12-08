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
using System.Windows.Media.Media3D;

namespace ImgViewer.Models
{
    public class OpenCVImageProcessor : IImageProcessor, IDisposable
    {
        private Mat _currentImage;
        private Scalar _pageColor;
        private Scalar _borderColor;
        private Mat _blurred;
        private readonly IAppManager _appManager;

        private CancellationToken _token;

        private readonly object _imageLock = new();
        private readonly object _commandLock = new();

        private readonly DocBoundaryModel _docBoundaryModel;

        private Mat WorkingImage
        {
            get
            {
                lock (_imageLock)
                    return _currentImage.Clone();
            }
            set
            {
                if (value == null) return;
                Mat old;
                //Mat newMat = value.Clone();
                lock (_imageLock)
                {
                    old = _currentImage;
                    _currentImage = value;
                    old?.Dispose();
                    if (_appManager == null) return;
                    _appManager.SetBmpImageOnPreview(MatToBitmapSource(value));
                }

                
            }
        }
        public ImageSource CurrentImage
        {
            set
            {
                try
                {
                    using var mat = BitmapSourceToMat((BitmapSource)value);
                    if (mat == null || mat.Empty()) return;
                    WorkingImage = mat.Clone();
                }
                catch (OperationCanceledException)
                {

                }
            }
        }




        public OpenCVImageProcessor(IAppManager appManager, CancellationToken token)
        {
            _appManager = appManager;
            _token = token;
            var onnxToken = CancellationTokenSource.CreateLinkedTokenSource(token).Token;
            _docBoundaryModel = new DocBoundaryModel(onnxToken, "Models/ML/model.onnx");
        }

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
            GCHandle? handle = null;

            try
            {
                _token.ThrowIfCancellationRequested();
                src.CopyPixels(buffer, stride, 0);

                // pin buffer short-lived, создать Mat view и совершить Clone/CvtColor
                handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                IntPtr ptr = handle.Value.AddrOfPinnedObject();

                Mat result = CreateMatFromBuffer(buffer, w, h, stride, copyFormat);


                return result;
            }
            finally
            {
                if (handle.HasValue && handle.Value.IsAllocated) handle.Value.Free();
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        private Mat CreateMatFromBuffer(byte[] buffer, int width, int height, int srcStride, PixelFormat copyFormat)
        {
            if (copyFormat == PixelFormats.Bgr24)
            {
                var mat = new Mat(height, width, MatType.CV_8UC3);
                IntPtr dstPtr = mat.Data;
                int dstStride = (int)mat.Step();
                int copyRowBytes = Math.Min(width * 3, srcStride);
                for (int r = 0; r < height; r++)
                    Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);
                return mat;
            }

            if (copyFormat == PixelFormats.Bgra32)
            {
                using var mat4 = new Mat(height, width, MatType.CV_8UC4);
                IntPtr dstPtr = mat4.Data;
                int dstStride = (int)mat4.Step();
                int copyRowBytes = Math.Min(width * 4, srcStride);
                for (int r = 0; r < height; r++)
                    Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);

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
                    Marshal.Copy(buffer, r * srcStride, IntPtr.Add(dstPtr, r * dstStride), copyRowBytes);

                var result = new Mat();
                Cv2.CvtColor(mat1, result, ColorConversionCodes.GRAY2BGR);
                return result;
            }

            throw new NotSupportedException("Unsupported PixelFormat");
        }


        public void Dispose()
        {
            _docBoundaryModel.Dispose();
            //throw new NotImplementedException();

        }

        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;

        public void Load(string path)
        {
            //throw new NotImplementedException();
            try
            {
                //_currentImage = Cv2.ImRead(path, ImreadModes.Color);

                //BitmapSource bmpSource = MatToBitmapSource(_currentImage);
                //ImageUpdated?.Invoke(BitmapSourceToStream(bmpSource));
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
            }
            //_pageColor = ScalarFromNormalized(AnalyzePageAndBorderColorsSimple(_currentImage).pageColorNorm);
            //_borderColor = ScalarFromNormalized(AnalyzePageAndBorderColorsSimple(_currentImage).borderColorNorm);

        }


        public Stream? GetStreamForSaving(ImageFormat format, TiffCompression compression)
        {
            //throw new NotImplementedException();
            if (WorkingImage != null && !WorkingImage.Empty())
            {
                if (format == ImageFormat.Tiff)
                {
                    var paramsList = new List<int>();
                    switch (compression)
                    {
                        case TiffCompression.None:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.None);
                            break;
                        case TiffCompression.CCITTG4:
                            if (WorkingImage.Channels() != 1)
                            {
                                // для G4 нужно 1-битное изображение
                                //using var gray = new Mat();
                                //Cv2.CvtColor(_currentImage, gray, ColorConversionCodes.BGR2GRAY);
                                //_currentImage = gray.Clone();
                                //using var bin = new Mat();
                                //Cv2.Threshold(gray, bin, 128, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                                //_currentImage = bin.Clone();
                                //if (_currentImage.Type() != MatType.CV_8UC1)
                                //    _currentImage.ConvertTo(_currentImage, MatType.CV_8UC1);
                            }
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.CCITTG4);
                            break;
                        case TiffCompression.CCITTG3:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.CCITTG3);
                            break;
                        case TiffCompression.LZW:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.LZW);
                            break;
                        case TiffCompression.Deflate:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.Deflate);
                            break;
                        case TiffCompression.JPEG:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.JPEG);
                            break;
                        case TiffCompression.PackBits:
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.PackBits);
                            break;
                        default:
                            // default to None
                            paramsList.Add((int)ImwriteFlags.TiffCompression);
                            paramsList.Add((int)TiffCompression.None);
                            break;
                    }
                    //byte[] tiffData = _currentImage.ImEncode(".tiff", paramsList.ToArray());
                    byte[] pngData = WorkingImage.ImEncode(".png");
                    return new MemoryStream(pngData);
                }
                else
                {
                    // для других форматов просто PNG
                    return MatToStream(WorkingImage);
                }
            }
            return null;
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
            if (src.Type() == MatType.CV_8UC3)
            {
                // ничего не делаем
            }
            else if (src.Type() == MatType.CV_8UC1)
            {
                tmp = new Mat();
                Cv2.CvtColor(src, tmp, ColorConversionCodes.GRAY2BGR);
                img = tmp;
            }
            else if (src.Type() == MatType.CV_8UC4)
            {
                tmp = new Mat();
                Cv2.CvtColor(src, tmp, ColorConversionCodes.BGRA2BGR);
                img = tmp;
            }
            else
            {
                // универсальный fallback: конвертируем в 8UC3 через преобразование типа + возможное BGR конвертирование
                tmp = new Mat();
                src.ConvertTo(tmp, MatType.CV_8UC3);
                // Если исходный имел 1 канал, ConvertTo даст 3 одинаковых канала — но это крайний случай.
                img = tmp;
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

        private Mat ApplyProcessingBlur(Mat source, int kernelSize = 3, string blurMode = "gaussian")
        {
            if (source == null) return null!;
            // ensure safe copy of source
            var src = source.Clone();

            // sanitize kernel
            int k = Math.Max(0, kernelSize);
            if (k < 3)
                return src; // no blur — return clone so caller can dispose safely

            // make odd
            if ((k & 1) == 0) k++;

            Mat outMat = new Mat();
            switch (blurMode?.ToLowerInvariant())
            {
                case "median":
                    Cv2.MedianBlur(src, outMat, k); // k must be odd >= 3
                    break;
                case "bilateral":
                    // bilateral uses diameter, sigmaColor, sigmaSpace; pick reasonable defaults
                    Cv2.BilateralFilter(src, outMat, k, k * 2, k / 2);
                    break;
                case "box":
                    Cv2.Blur(src, outMat, new OpenCvSharp.Size(k, k));
                    break;
                case "gaussian":
                default:
                    Cv2.GaussianBlur(src, outMat, new OpenCvSharp.Size(k, k), 0);
                    break;
            }

            src.Dispose();
            return outMat;
        }

        private void ApplyBinarize(BinarizeMethod method, BinarizeParameters parameters)
        {
            switch (method)
            {
                case BinarizeMethod.Threshold:
                    break;
                case BinarizeMethod.Adaptive:
                    break;
                case BinarizeMethod.Sauvola:
                    break;
                case BinarizeMethod.Majority:
                    break;
            }
        }

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

        public Mat ProcessSingle(Mat src,
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

                            //int threshold = 128;
                            //int blockSize = 3;
                            //double c = 14;
                            //bool useGaussian = false;
                            //bool useMorphology = false;
                            //int morphKernel = 3;
                            //int morphIters = 1;
                            //int majorityOffset = 20;

                            var binParams = ToStruct<BinarizeParameters>(parameters);

                            switch (binParams.Method)
                            {
                                case BinarizeMethod.Threshold:
                                    //DumpStruct(binParams);
                                    return BinarizeThreshold(src, binParams.Threshold);
                                    break;
                                case BinarizeMethod.Adaptive:
                                    return BinarizeAdaptive(src, binParams, invert: false);
                                    break;
                                case BinarizeMethod.Sauvola:
                                    return SauvolaBinarize(src, binParams);
                                    break;
                                case BinarizeMethod.Majority:
                                    return MajorityBinarize(src, binParams);
                                    break;
                            }
                            break;
                        case ProcessorCommand.Deskew:
                            //Deskewer.Parameters p = new Deskewer.Parameters();
                            foreach (var kv in parameters)
                            {
                                Debug.WriteLine(kv.Key.ToString());
                                Debug.WriteLine(kv.Value.ToString());

                            }
                            return NewDeskew(src, parameters);
                            //Deskew();
                            break;
                        case ProcessorCommand.BordersRemove:
                            {
                                //BordersDeskew();
                                //threshFrac(0..1) : чем выше — тем жёстче требование к считать строку бордюром.
                                //0.6 — хорошая стартовая точка.Для очень толстых рамок можно поднять до 0.75–0.9
                                //contrastThr: порог яркости.Для слабых контрастов уменьшите (15..25); для сильных — увеличьте.
                                //centralSample: если документ сильно смещён в кадре, уменьшите (например 0.2),
                                //либо используйте более устойчивую выборку(несколько областей).
                                //maxRemoveFrac: защита от катастрофического удаления.Оставьте не выше 0.3.
                                double treshFrac = 0.40;
                                int contrastThr = 50;
                                double centralSample = 0.10;
                                double maxRemoveFrac = 0.45;

                                byte darkThresh = 40;
                                bool autoThresh = false;
                                int marginPercentForThresh = 10;
                                double shiftFactorForTresh = 0.25;
                                Scalar? bgColor = null;
                                int minAreaPx = 2000;
                                double minSpanFraction = 0.6;
                                double solidityThreshold = 0.6;
                                double minDepthFraction = 0.05;
                                int featherPx = 12;
                                int top = 0, bottom = 0, left = 0, right = 0;
                                bool manualCutDebug = false;
                                bool useTeleaHybrid = true;
                                bool applyManualCut = false;

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
                                            int i = SafeInt(kv.Value, 0);
                                            int color = Math.Max(0, Math.Min(255, i));
                                            //bgColor = new Scalar(0, 0, 255);
                                            //bgColor = new Scalar(color, color, color);
                                            bgColor = SampleCentralGrayScalar(WorkingImage, 0, 0.1);
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
                                                break;
                                            case "By Contrast":
                                                return RemoveBordersByRowColWhite(src,
                                                        threshFrac: treshFrac,
                                                        contrastThr: contrastThr,
                                                        centralSample: centralSample,
                                                        maxRemoveFrac: maxRemoveFrac
                                                    );
                                                break;
                                            case "Manual":

                                                return RemoveBorders_Manual(src, top, bottom, left, right, applyManualCut, manualCutDebug);
                                                break;

                                        }
                                    }

                                }

                            }
                            break;
                        case ProcessorCommand.Despeckle:
                            //applyDespeckleCurrent();

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
                            bool showDespeckleDebug = false;

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
                                    case "showDespeckleDebug":
                                        settings.ShowDespeckleDebug = batchProcessing ? false : SafeBool(kv.Value, showDespeckleDebug);
                                        break;

                                }
                            }

                            //_currentImage = DespeckleApplyToSource(_currentImage, settings, true, false, true);
                            return Despeckle(src, settings);
                            //_currentImage = DespeckleAfterBinarization(_currentImage, settings);

                            break;
                        case ProcessorCommand.SmartCrop:


                            int cropLevel = 62;
                            int eastInputWidth = 1280;
                            int eastInputHeight = 1280;
                            float eastScoreThreshold = 0.45f;
                            float eastNmsThreshold = 0.45f;
                            int tesseractMinConfidence = 50;
                            int paddingPx = 20;
                            int downscaleMaxWidth = 1600;
                            bool eastDebug = true;

                            foreach (var kv in parameters)
                            {
                                if (kv.Key == null) continue;
                                switch (kv.Key)
                                {
                                    case "cropLevel":
                                        cropLevel = SafeInt(kv.Value, cropLevel);
                                        break;
                                    case "eastInputWidth":
                                        eastInputWidth = SafeInt(kv.Value, eastInputWidth);
                                        break;
                                    case "eastInputHeight":
                                        eastInputHeight = SafeInt(kv.Value, eastInputHeight);
                                        break;
                                    case "eastScoreThreshold":
                                        eastScoreThreshold = SafeDoubleToFloat(kv.Value, eastScoreThreshold);
                                        break;
                                    case "eastNmsThreshold":
                                        eastNmsThreshold = SafeDoubleToFloat(kv.Value, eastNmsThreshold);
                                        break;
                                    case "tesseractMinConfidence":
                                        tesseractMinConfidence = SafeInt(kv.Value, tesseractMinConfidence);
                                        break;
                                    case "paddingPx":
                                        paddingPx = SafeInt(kv.Value, paddingPx);
                                        break;
                                    case "downscaleMaxWidth":
                                        downscaleMaxWidth = SafeInt(kv.Value, downscaleMaxWidth);
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
                                            return DetectDocumentAndCrop(src, cropLevel, false, out Mat debugMask, out Mat debugOverlay); ;
                                            break;
                                        case "EAST":
                                            //return SmartCrop(src);
                                            return SmartCrop(
                                                src,
                                                eastInputWidth,
                                                eastInputHeight,
                                                eastScoreThreshold,
                                                eastNmsThreshold,
                                                tesseractMinConfidence,
                                                paddingPx,
                                                downscaleMaxWidth, eastDebug
                                            );
                                            break;
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
                            Mat mask;
                            if (orientation == LineOrientation.Vertical)
                            {
                                return LinesRemover.RemoveScannerVerticalStripes(src, 3, 20, 0, out mask, false, null);
                            }
                            if (orientation == LineOrientation.Horizontal)
                            {
                                return LinesRemover.RemoveScannerHorizontalStripes(src, 3, 20, 0, out mask, false, null);
                            }
                            if (orientation == LineOrientation.Both)
                            {
                                Mat firstResult = LinesRemover.RemoveScannerVerticalStripes(src, 3, 20, 0, out mask, false, null);
                                return LinesRemover.RemoveScannerHorizontalStripes(firstResult, 3, 20, 0, out mask, false, null);
                            }



                            break;
                        case ProcessorCommand.DotsRemove:
                            //RemoveSpecksWithHandler();
                            break;
                        case ProcessorCommand.ChannelsCorrection:
                            break;
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



                            break;

                    }
                }
            }
            return null;
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

        private Mat DetectDocumentAndCrop(Mat src, int cropLevel, bool debug, out Mat debugMask, out Mat debugOverlay)
        {
            // 1) Предикт маски
            Mat mask = new Mat();
            Scalar bgColor = GetBgColor(src);
            // create new Mat with bgColor and add 20px on each side
            Mat bigMat = new Mat(src.Rows + 40, src.Cols + 40, src.Type(), bgColor);
            src.CopyTo(new Mat(bigMat, new Rect(20, 20, src.Cols, src.Rows)));
            mask = _docBoundaryModel.PredictMask(bigMat, cropLevel);
                                                                
            debugMask = mask.Clone();

            // 2) Обрезка
            Mat cropped = DocumentCropper.CropByMask(src, mask, out debugOverlay);

            return cropped;
        }



        public void ApplyCommand(ProcessorCommand command, Dictionary<string, object> parameters = null, bool batchProcessing = false)
        {
            
            WorkingImage = ProcessSingle(WorkingImage, command, parameters ?? new Dictionary<string, object>(), _token, batchProcessing);

        }

        private Mat? RemoveBorders_Manual(Mat src, int top, int bottom, int left, int right, bool applyCut, bool debug)
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
                                                        debug);
                return result;
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("Manual Cut cancelled!");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }
        }


        public void UpdateCancellationToken(CancellationToken token)
        {
            _token = token;
        }

        private Mat Despeckle(Mat src, DespeckleSettings settings)
        {
            return Despeckler.DespeckleApplyToSource(_token, src, settings, settings.ShowDespeckleDebug, true, true);
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
            int range = p.MajorityOffset; // max absolute offset
            var deltas = Enumerable.Range(-10, (range * 2) / step + 1)
                                   .Select(i => i * step)
                                   .ToArray();
            return MajorityVotingBinarize(src, p.Threshold, deltas);
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

        private Mat? PunchHolesRemove(Mat src, List<PunchSpec> specs, double roundness, double fillRatio, Offsets offsets)
        {
            if (src == null || src.Empty()) return new Mat();
            try
            {
                return PunchHoleRemover.RemovePunchHoles(_token, src, specs, roundness, fillRatio, offsets.top, offsets.bottom, offsets.left, offsets.right);
            }
            catch (OperationCanceledException)
            {
                Debug.WriteLine("PunchHoles Removal cancelled!");
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                return null;
            }

        }




        //public Mat DespeckleAfterBinarization(Mat bin, DespeckleSettings? settings = null, bool debug = false)
        //{
        //    if (bin == null) throw new ArgumentNullException(nameof(bin));
        //    if (bin.Type() != MatType.CV_8UC1) throw new ArgumentException("Expect CV_8UC1 binary image (text=0).");

        //    // default settings if null
        //    settings ??= new DespeckleSettings
        //    {
        //        SmallAreaRelative = true,
        //        SmallAreaMultiplier = 0.25,
        //        SmallAreaAbsolutePx = 64,
        //        MaxDotHeightFraction = 0.35,
        //        ProximityRadiusFraction = 0.8,
        //        SquarenessTolerance = 0.6,
        //        KeepClusters = true,
        //        UseDilateBeforeCC = true,
        //        DilateKernel = "1x3", // "1x3", "3x1" or "3x3"
        //        DilateIter = 1,
        //        ShowDespeckleDebug = false
        //    };

        //    if (settings.SmallAreaMultiplier <= 0) settings.SmallAreaMultiplier = 0.25;
        //    if (settings.SmallAreaAbsolutePx <= 0) settings.SmallAreaAbsolutePx = 64;
        //    if (settings.DilateIter < 0) settings.DilateIter = 0;

        //    if (!(settings.DilateKernel == "1x3" || settings.DilateKernel == "3x1" || settings.DilateKernel == "3x3"))
        //    {
        //        // fallback to default
        //        settings.DilateKernel = "1x3";
        //    }

        //    if (!settings.SmallAreaRelative && settings.SmallAreaAbsolutePx <= 0)
        //        settings.SmallAreaAbsolutePx = 64;

        //    // If despeckling disabled by giving absurd values, still allow quick exit:
        //    // (you can add explicit Enable flag in settings if needed)
        //    // Start processing
        //    Mat work = bin.Clone();

        //    // origBlackMask: 255 where original had text (pixel == 0)
        //    using var origBlackMask = new Mat();
        //    Cv2.InRange(work, new Scalar(0), new Scalar(0), origBlackMask); // 255 where text==0

        //    // labelingMat: copy of work; we may dilate labelingMat to merge touching dots->glyphs,
        //    // but we will intersect removal masks with origBlackMask so only original pixels removed.
        //    Mat labelingMat = work.Clone();

        //    if (settings.UseDilateBeforeCC && settings.DilateIter > 0)
        //    {
        //        Mat kernel;
        //        switch (settings.DilateKernel)
        //        {
        //            case "3x1":
        //                kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 1));
        //                break;
        //            case "3x3":
        //                kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        //                break;
        //            default: // "1x3"
        //                kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3));
        //                break;
        //        }
        //        var tmp = new Mat();
        //        Cv2.Dilate(labelingMat, tmp, kernel, iterations: settings.DilateIter);
        //        labelingMat.Dispose();
        //        kernel.Dispose();
        //        labelingMat = tmp;
        //    }

        //    // connected components (on labelingMat)
        //    using var labels = new Mat();
        //    using var stats = new Mat();
        //    using var centroids = new Mat();
        //    int nLabels = Cv2.ConnectedComponentsWithStats(labelingMat, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);

        //    var comps = new List<(int label, Rect bbox, int area)>();
        //    for (int lbl = 1; lbl < nLabels; lbl++) // label 0 = background
        //    {
        //        int left = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Left);
        //        int top = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Top);
        //        int width = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Width);
        //        int height = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Height);
        //        int area = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Area);
        //        comps.Add((lbl, new Rect(left, top, width, height), area));
        //    }

        //    if (comps.Count == 0)
        //    {
        //        labelingMat.Dispose();
        //        return work;
        //    }

        //    // median char height (robust)
        //    var heights = comps.Select(c => c.bbox.Height).Where(h => h >= 3).ToArray();
        //    int medianHeight = heights.Length > 0 ? heights.OrderBy(h => h).ElementAt(heights.Length / 2) : 20;

        //    // compute thresholds
        //    int smallAreaThrPx = settings.SmallAreaRelative
        //        ? Math.Max(1, (int)Math.Round(settings.SmallAreaMultiplier * medianHeight * medianHeight))
        //        : Math.Max(1, settings.SmallAreaAbsolutePx);

        //    int maxDotHeight = Math.Max(1, (int)Math.Round(settings.MaxDotHeightFraction * medianHeight));
        //    double proximityRadius = Math.Max(1.0, settings.ProximityRadiusFraction * medianHeight);
        //    double squarenessTolerance = Math.Max(0.0, Math.Min(1.0, settings.SquarenessTolerance));

        //    int rows = work.Rows, cols = work.Cols;
        //    var horProj = new int[rows];
        //    for (int y = 0; y < rows; y++) horProj[y] = cols - Cv2.CountNonZero(work.Row(y)); // black px per row
        //    int projThr = Math.Max(1, cols / 100);
        //    var textRows = new HashSet<int>(Enumerable.Range(0, rows).Where(y => horProj[y] > projThr));

        //    Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        //    var bigBoxes = comps.Where(c => c.bbox.Height >= medianHeight * 0.6 || c.area > smallAreaThrPx * 4)
        //                        .Select(c => c.bbox).ToArray();

        //    static double DistPointToRect(Point p, Rect r)
        //    {
        //        int dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
        //        int dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
        //        return Math.Sqrt(dx * dx + dy * dy);
        //    }

        //    var smallComps = comps.Where(c => c.area < smallAreaThrPx || c.bbox.Height <= maxDotHeight).ToArray();
        //    var toRemoveLabels = new List<int>();
        //    var toKeepLabels = new HashSet<int>();

        //    int rowCheckRange = Math.Max(1, medianHeight / 3);
        //    int clusterHoriz = Math.Max(3, (int)(medianHeight * 0.6));

        //    foreach (var c in smallComps)
        //    {
        //        var rect = c.bbox;
        //        var center = Center(rect);

        //        double minDistToBig = double.MaxValue;
        //        foreach (var br in bigBoxes)
        //        {
        //            double d = DistPointToRect(center, br);
        //            if (d < minDistToBig) minDistToBig = d;
        //        }
        //        bool nearBig = minDistToBig < proximityRadius;

        //        bool onTextLine = false;
        //        for (int ry = Math.Max(0, center.Y - rowCheckRange); ry <= Math.Min(rows - 1, center.Y + rowCheckRange); ry++)
        //        {
        //            if (textRows.Contains(ry)) { onTextLine = true; break; }
        //        }

        //        bool squareLike = Math.Abs(rect.Width - rect.Height) <= Math.Max(1, rect.Height * squarenessTolerance);

        //        bool partOfCluster = false;
        //        if (settings.KeepClusters)
        //        {
        //            foreach (var c2 in smallComps)
        //            {
        //                if (c2.label == c.label) continue;
        //                if (Math.Abs(Center(c2.bbox).Y - center.Y) <= rowCheckRange &&
        //                    Math.Abs(Center(c2.bbox).X - center.X) <= clusterHoriz)
        //                {
        //                    partOfCluster = true;
        //                    break;
        //                }
        //            }
        //        }

        //        if (nearBig || (onTextLine && squareLike) || partOfCluster)
        //        {
        //            toKeepLabels.Add(c.label);
        //            continue;
        //        }

        //        toRemoveLabels.Add(c.label);
        //    }

        //    // remove: build mask from labels and intersect with original black mask (so we never delete pixels created by dilation)
        //    foreach (int lbl in toRemoveLabels)
        //    {
        //        using var mask = new Mat();
        //        Cv2.InRange(labels, new Scalar(lbl), new Scalar(lbl), mask); // 255 where label==lbl (on labelingMat)
        //        using var intersect = new Mat();
        //        Cv2.BitwiseAnd(mask, origBlackMask, intersect); // ensure only original black pixels removed
        //        work.SetTo(new Scalar(255), intersect);
        //    }

        //    // debug visualization
        //    if (debug || settings.ShowDespeckleDebug)
        //    {
        //        var vis = new Mat();
        //        Cv2.CvtColor(bin, vis, ColorConversionCodes.GRAY2BGR);
        //        foreach (var c in comps)
        //        {
        //            var color = toKeepLabels.Contains(c.label) ? Scalar.Green : toRemoveLabels.Contains(c.label) ? Scalar.Red : Scalar.Yellow;
        //            Cv2.Rectangle(vis, c.bbox, color, 1);
        //        }

        //        labelingMat.Dispose();
        //        return vis;
        //    }

        //    labelingMat.Dispose();
        //    return work;
        //}

        private Mat? SmartCrop(Mat src, int eastInputWidth = 1280, int eastInputHeight = 1280,
                                float eastScoreThreshold = 0.45f,
                                float eastNmsThreshold = 0.45f,
                                int tesseractMinConfidence = 50,
                                int paddingPx = 20,
                                int downscaleMaxWidth = 1600, bool debug = true)
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
                    cropped = cropper.ShowDetectedAreas(src, eastInputWidth, eastInputHeight,
                                                   eastScoreThreshold, eastNmsThreshold,
                                                   tesseractMinConfidence,
                                                   paddingPx,
                                                   downscaleMaxWidth, debug);
                }
                else
                {
                    cropped = cropper.CropKeepingText(src, eastInputWidth, eastInputHeight,
                                                   eastScoreThreshold, eastNmsThreshold,
                                                   tesseractMinConfidence,
                                                   paddingPx,
                                                   downscaleMaxWidth);
                }
               
            }
            catch (OperationCanceledException)
            {

            }

            return cropped;
        }



        private Stream MatToStream(Mat mat)
        {
            BitmapSource bmpSource = MatToBitmapSource(mat);
            return BitmapSourceToStream(bmpSource);
        }

        public Stream? LoadAsPNGStream(string path, int targetBPP)
        {
            try
            {
                using var mat = Cv2.ImRead(path, ImreadModes.Color);
                BitmapSource bmpSource = MatToBitmapSource(mat);
                // Сохраняем в MemoryStream как PNG
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;
                return ms;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
                return null;
            }
        }

        // Safe Mat -> BitmapSource conversion (no use of .Depth or non-existent members)
        private BitmapSource MatToBitmapSource(Mat matOrg)
        {
            if (matOrg == null || matOrg.Empty()) return null!;

            using var mat = matOrg.Clone();

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



        // OLD METHOD
        //private BitmapSource MatToBitmapSource(Mat mat)
        //{
        //    if (mat == null || mat.Empty())
        //    {
        //        Debug.WriteLine("MatToBitmapSource: input Mat is null or empty.");
        //        return null;
        //    }

        //    // Быстрая конвертация через OpenCvSharp.Extensions:
        //    //var bmp = mat.ToBitmap(); // создаёт System.Drawing.Bitmap (GDI+) — не идеально для WPF
        //    // Но лучше: создать WriteableBitmap и скопировать байты
        //    var wb = new WriteableBitmap(mat.Width, mat.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
        //    int stride = mat.Cols * mat.ElemSize();
        //    wb.WritePixels(new System.Windows.Int32Rect(0, 0, mat.Width, mat.Height), mat.Data, mat.Rows * stride, stride);
        //    wb.Freeze();
        //    return wb;
        //}


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




        private Mat? MatToGray(Mat src)
        {
            if (src == null || src.Empty()) return null; // уже в градациях серого
            var gray = new Mat();
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
                    {
                        // универсальный fallback: конвертируем в 8UC3 через преобразование типа + возможное BGR конвертирование
                        using var tmp = new Mat();
                        src.ConvertTo(tmp, MatType.CV_8UC3);
                        Cv2.CvtColor(tmp, gray, ColorConversionCodes.BGR2GRAY);
                    }
                    break;
            }

            return gray;
        }

        private Mat? BinarizeAdaptive(Mat src, BinarizeParameters p, bool invert = false)
        {
            if (src == null || src.Empty()) return null;

            // Debug all args
            DumpStruct(p);

            using var gray = MatToGray(src);
            if (gray == null) return null;


            int bs;
            if (p.BlockSize.HasValue && p.BlockSize > 0)
            {
                bs = p.BlockSize.Value;
            }
            else
            {
                // heuristic: блок ~min(width, height) / 30, clamp to[3..201]
                int baseBs = Math.Max(3, Math.Min(201, Math.Min(gray.Cols, gray.Rows) / 30));
                if ((baseBs & 1) == 0) baseBs++; // сделать нечётным
                bs = baseBs;
            }
            if (bs < 3) bs = 3;
            if ((bs & 1) == 0) bs++; // сделать нечётным

            using var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(1, 1), 0);

            using var bin = new Mat();
            var adaptiveType = p.UseGaussian ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC;
            var threshType = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            Cv2.AdaptiveThreshold(blur, bin, 255, adaptiveType, threshType, bs, p.MeanC);

            if (p.UseMorphology)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(p.MorphKernelBinarize, p.MorphKernelBinarize));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel, iterations: p.MorphIterationsBinarize);
            }

            using var color = new Mat();
            Cv2.CvtColor(bin, color, ColorConversionCodes.GRAY2BGR);

            return color.Clone();

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



        private Mat? BinarizeThreshold(Mat src, int threshold = 128)
        {
            if (src == null || src.Empty()) return null;

            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);

            // Конвертируем обратно в BGR — тогда весь pipeline, ожидающий 3 канала, продолжит работать
            using var color = new Mat();
            Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);

            return color.Clone(); // сохраняем результат как 3-канальную матрицу
        }

        //threshFrac(0..1) : чем выше — тем жёстче требование к считать строку бордюром.
        //0.6 — хорошая стартовая точка.Для очень толстых рамок можно поднять до 0.75–0.9
        //contrastThr: порог яркости.Для слабых контрастов уменьшите (15..25); для сильных — увеличьте.
        //centralSample: если документ сильно смещён в кадре, уменьшите (например 0.2),
        //либо используйте более устойчивую выборку(несколько областей).
        //maxRemoveFrac: защита от катастрофического удаления.Оставьте не выше 0.3.




        public static Deskewer.Parameters ParseParametersSimple(Dictionary<string, object>? parameters)
        {
            var result = new Deskewer.Parameters
            {
                byBorders = false,
                cTresh1 = 50,
                cTresh2 = 150,
                morphKernel = 5,
                houghTreshold = 80,
                minLineLength = 200
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

            foreach (var kv in parameters)
            {
                if (kv.Key == null) continue;

                switch (kv.Key) // keys are exact (case-sensitive) as you requested
                {
                    case "deskewAlgorithm":
                        {
                            var s = kv.Value?.ToString() ?? "Auto";
                            // original logic: treat anything that is not "Auto" as byBorders=true
                            // If you want only explicit "ByBorders" to set true, change the condition accordingly.
                            result.byBorders = !s.Equals("Auto", StringComparison.OrdinalIgnoreCase);
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

                    case "minLineLength":
                        {
                            int v = SafeInt(kv.Value, result.minLineLength);
                            Math.Max(0, v);
                        }
                        break;

                    case "houghTreshold":
                        result.houghTreshold = SafeInt(kv.Value, result.houghTreshold);
                        break;

                    default:
                        // unknown key: ignore
                        break;
                }
            }

            return result;
        }

        public Mat? NewDeskew(Mat src, Dictionary<string, object> parameters)
        {
            if (src == null || src.Empty()) return new Mat();


            var p = new Deskewer.Parameters();

            p = ParseParametersSimple(parameters);
            try
            {
                Mat result = Deskewer.Deskew(_token, src, p.byBorders, p.cTresh1, p.cTresh2, p.morphKernel, p.minLineLength, p.houghTreshold);
                return result;
            }
            catch (OperationCanceledException)
            {

                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while Deskew: {ex.Message}");
                return null;
            }

        }

        //private void BordersDeskew()
        //{
        //    if (_currentImage == null || _currentImage.Empty()) return;
        //    using var src = _currentImage.Clone();
        //    //_currentImage = Deskewer.Deskew(src, true);
        //}

        //public void Deskew()
        //{
        //    if (_currentImage == null || _currentImage.Empty()) return;




        //    double angle = GetSkewAngleByHough(_currentImage, cannyThresh1: 50, cannyThresh2: 150, houghThreshold: 80, minLineLength: Math.Min(_currentImage.Width, 200), maxLineGap: 20);
        //    Debug.WriteLine($"Deskew: angle by Hough = {angle}");

        //    if (double.IsNaN(angle))
        //    {
        //        angle = GetSkewAngleByProjection(_currentImage, minAngle: -15, maxAngle: 15, coarseStep: 1.0, refineStep: 0.2);
        //    }


        //    if (double.IsNaN(angle) || Math.Abs(angle) < 0.005) // если угол ~0 — не поворачивать
        //    {
        //        Debug.WriteLine($"Deskew: angle is zero or NaN ({angle}), skipping rotation.");
        //        return;
        //    }


        //    using var src = _currentImage.Clone();
        //    double rotation = -angle;
        //    double rad = rotation * Math.PI / 180.0;
        //    double absCos = Math.Abs(Math.Cos(rad));
        //    double absSin = Math.Abs(Math.Sin(rad));
        //    int newW = (int)Math.Round(src.Width * absCos + src.Height * absSin);
        //    int newH = (int)Math.Round(src.Width * absSin + src.Height * absCos);

        //    var center = new Point2f(src.Width / 2f, src.Height / 2f);
        //    var M = Cv2.GetRotationMatrix2D(center, rotation, 1.0);
        //    M.Set(0, 2, M.Get<double>(0, 2) + (newW / 2.0 - center.X));
        //    M.Set(1, 2, M.Get<double>(1, 2) + (newH / 2.0 - center.Y));

        //    using var rotated = new Mat();
        //    Cv2.WarpAffine(src, rotated, M, new OpenCvSharp.Size(newW, newH), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255)); // 0 - black background


        //    var result = rotated.Clone();
        //    _currentImage = result;
        //}








        //Mat PrecomputeDarkMask_BackgroundNormalized(Mat src)
        //{
        //    var gray = new Mat();
        //    Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        //    // оценка фона большим ядром (например 101x101)
        //    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(101, 101));
        //    var bg = new Mat();
        //    Cv2.MorphologyEx(gray, bg, MorphTypes.Open, kernel);

        //    // вычитаем фон — получаем более ровную яркость
        //    var norm = new Mat();
        //    Cv2.Subtract(gray, bg, norm);

        //    // optional contrast
        //    Cv2.Normalize(norm, norm, 0, 255, NormTypes.MinMax);

        //    // Otsu или адаптивный порог
        //    var darkMask = new Mat();
        //    Cv2.Threshold(norm, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        //    gray.Dispose(); bg.Dispose(); norm.Dispose();
        //    return darkMask;
        //}

        private Mat? RemoveBordersByRowColWhite(Mat src,
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
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while removing borders (By contrast): {ex.Message}");
                return null;
            }
        }

        private Mat? RemoveBorders_Auto(Mat src, byte darkThresh, Scalar? bgColor, int minAreaPx, double minSpanFraction, double solidityThreshold,
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
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error while removing borders (Auto): {ex.Message}");
                return null;
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


        //Mat PrecomputeDarkMask_Otsu(Mat src)
        //{
        //    var gray = new Mat();
        //    Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

        //    // небольшая фильтрация шума
        //    Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

        //    // Otsu + инверсия: dark -> 255
        //    var darkMask = new Mat();
        //    Cv2.Threshold(gray, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

        //    // убираем мелкие отверстия/шум
        //    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        //    Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kernel);

        //    gray.Dispose();
        //    return darkMask; // caller обязан Dispose
        //}

        //Mat PrecomputeDarkMask_Adaptive(Mat src, int blockSize = 31, int C = 10)
        //{
        //    var gray = new Mat();
        //    // лучше работать с яркостным каналом Y
        //    var ycrcb = new Mat();
        //    Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
        //    Cv2.ExtractChannel(ycrcb, gray, 0); // Y канал
        //    ycrcb.Dispose();

        //    // опционально CLAHE, чтобы усилить контраст
        //    var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
        //    clahe.Apply(gray, gray);

        //    // сглаживание
        //    Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

        //    var darkMask = new Mat();
        //    // AdaptiveThreshold: используем BinaryInv чтобы тёмные стали 255
        //    Cv2.AdaptiveThreshold(gray, darkMask, 255,
        //        AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, blockSize, C);

        //    // морфологическая очистка
        //    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        //    Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kernel);

        //    gray.Dispose();
        //    clahe.Dispose();
        //    return darkMask;
        //}





        //public Mat FillBlackBorderAreas(
        //    Mat src,
        //    Scalar? bgColor = null,
        //    byte blackThreshold = 8,
        //    double minSpanFraction = 0.8,    // доля ширины/высоты, чтобы считать компонент "полосой"
        //    int minAreaPx = 2000,            // если компонент >= этой площади — можно считать большим
        //    double solidityThreshold = 0.55  // если заполненность bbox >= threshold => считать сплошной
        //)
        //{
        //    if (src == null) throw new ArgumentNullException(nameof(src));
        //    if (src.Empty()) return src;

        //    using var srcClone = src.Clone();

        //    Mat working;
        //    bool createdWorking = false;
        //    if (srcClone.Channels() == 1)
        //    {
        //        working = new Mat();
        //        Cv2.CvtColor(srcClone, working, ColorConversionCodes.GRAY2BGR);
        //        createdWorking = true;
        //    }
        //    else if (srcClone.Type() != MatType.CV_8UC3)
        //    {
        //        working = new Mat();
        //        srcClone.ConvertTo(working, MatType.CV_8UC3);
        //        createdWorking = true;
        //    }
        //    else
        //    {
        //        working = srcClone;
        //        createdWorking = false;
        //    }

        //    try
        //    {
        //        int rows = working.Rows;
        //        int cols = working.Cols;

        //        // --- опред. цвета фона (углы) ---
        //        Scalar chosenBg;
        //        if (bgColor.HasValue) chosenBg = bgColor.Value;
        //        else
        //        {
        //            int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
        //            var cornerMeans = new List<Scalar>();
        //            var rects = new[]
        //            {
        //                new Rect(0, 0, cornerSize, cornerSize),
        //                new Rect(Math.Max(0, cols - cornerSize), 0, cornerSize, cornerSize),
        //                new Rect(0, Math.Max(0, rows - cornerSize), cornerSize, cornerSize),
        //                new Rect(Math.Max(0, cols - cornerSize), Math.Max(0, rows - cornerSize), cornerSize, cornerSize)
        //            };
        //            foreach (var r in rects)
        //            {
        //                if (r.Width <= 0 || r.Height <= 0) continue;
        //                using var patch = new Mat(working, r);
        //                var mean = Cv2.Mean(patch);
        //                double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
        //                if (brightness > blackThreshold * 1.5) cornerMeans.Add(mean);
        //            }
        //            if (cornerMeans.Count > 0)
        //            {
        //                double b = 0, g = 0, rr = 0;
        //                foreach (var s in cornerMeans) { b += s.Val0; g += s.Val1; rr += s.Val2; }
        //                chosenBg = new Scalar(b / cornerMeans.Count, g / cornerMeans.Count, rr / cornerMeans.Count);
        //            }
        //            else chosenBg = new Scalar(255, 255, 255);
        //        }

        //        // --- маска тёмных пикселей ---
        //        using var gray = new Mat();
        //        Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

        //        using var darkMask = new Mat();
        //        Cv2.Threshold(gray, darkMask, blackThreshold, 255, ThresholdTypes.BinaryInv); // dark -> 255

        //        // --- компоненты связности ---
        //        using var labels = new Mat();
        //        using var stats = new Mat();
        //        using var cents = new Mat();
        //        int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, cents);

        //        var filled = working.Clone();

        //        if (nLabels > 1)
        //        {
        //            for (int i = 1; i < nLabels; i++)
        //            {
        //                int x = stats.At<int>(i, 0);
        //                int y = stats.At<int>(i, 1);
        //                int w = stats.At<int>(i, 2);
        //                int h = stats.At<int>(i, 3);
        //                int area = stats.At<int>(i, 4);

        //                bool touchesLeft = x <= 0;
        //                bool touchesTop = y <= 0;
        //                bool touchesRight = (x + w) >= (cols);
        //                bool touchesBottom = (y + h) >= (rows);

        //                bool touchesAny = touchesLeft || touchesTop || touchesRight || touchesBottom;
        //                if (!touchesAny) continue;

        //                // основные эвристики:
        //                bool considerAsBorder = false;

        //                // 1) span: если касается top/bottom — смотрим ширину
        //                if (touchesTop || touchesBottom)
        //                {
        //                    double widthFraction = (double)w / cols;
        //                    if (widthFraction >= minSpanFraction) considerAsBorder = true;
        //                }

        //                // 2) span: если касается left/right — смотрим высоту
        //                if (touchesLeft || touchesRight)
        //                {
        //                    double heightFraction = (double)h / rows;
        //                    if (heightFraction >= minSpanFraction) considerAsBorder = true;
        //                }

        //                // 3) площадь: очень большие объекты можно закрашивать
        //                if (area >= minAreaPx) considerAsBorder = true;

        //                // 4) противоположные стороны -> явно полоса
        //                if ((touchesLeft && touchesRight) || (touchesTop && touchesBottom))
        //                    considerAsBorder = true;

        //                // 5) solidity = area / (w*h) — для сплошной заливки близко к 1, для текста значительно меньше.
        //                double solidity = 0.0;
        //                if (w > 0 && h > 0) solidity = (double)area / (w * h);
        //                if (solidity >= solidityThreshold) considerAsBorder = true;

        //                // Доп. эвристика: плотность внутри (простая) — если плотность пикселей низкая, это обычно текст (пропускаем)
        //                // (но уже учтено в solidity)

        //                if (!considerAsBorder) continue;

        //                // наконец — маска этой компоненты
        //                using var compMask = new Mat();
        //                Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask);

        //                // Но перед заливкой: можно дополнительно убедиться, что средняя яркость
        //                // внутри bbox не слишком похожа на внутреннюю область документа (опционально).
        //                // Для простоты — сразу зальём:
        //                //-------

        //                filled.SetTo(chosenBg, compMask);
        //            }
        //        }

        //        var result = filled.Clone();
        //        filled.Dispose();
        //        return result;
        //    }
        //    finally
        //    {
        //        if (createdWorking && working != null) working.Dispose();
        //    }
        //}

        //public Mat FillBlackBorderAreasOld(Mat src, Scalar? bgColor = null, byte blackThreshold = 8)
        //{
        //    if (src == null) throw new ArgumentNullException(nameof(src));
        //    if (src.Empty()) return src;

        //    // работаем с клоном входа
        //    using var srcClone = src.Clone();

        //    Mat working = null;
        //    bool createdWorking = false;
        //    if (srcClone.Channels() == 1)
        //    {
        //        working = new Mat();
        //        Cv2.CvtColor(srcClone, working, ColorConversionCodes.GRAY2BGR);
        //        createdWorking = true;
        //    }
        //    else if (srcClone.Type() != MatType.CV_8UC3)
        //    {
        //        working = new Mat();
        //        srcClone.ConvertTo(working, MatType.CV_8UC3);
        //        createdWorking = true;
        //    }
        //    else
        //    {
        //        working = srcClone;
        //        createdWorking = false;
        //    }

        //    try
        //    {
        //        int rows = working.Rows;
        //        int cols = working.Cols;

        //        // --- определяем цвет фона (простая стратегия по углам) ---
        //        Scalar chosenBg;
        //        if (bgColor.HasValue)
        //        {
        //            chosenBg = bgColor.Value;
        //        }
        //        else
        //        {
        //            int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
        //            var corners = new List<Scalar>();
        //            var rects = new[]
        //            {
        //        new Rect(0, 0, cornerSize, cornerSize),
        //        new Rect(Math.Max(0, cols - cornerSize), 0, cornerSize, cornerSize),
        //        new Rect(0, Math.Max(0, rows - cornerSize), cornerSize, cornerSize),
        //        new Rect(Math.Max(0, cols - cornerSize), Math.Max(0, rows - cornerSize), cornerSize, cornerSize)
        //    };

        //            foreach (var r in rects)
        //            {
        //                if (r.Width <= 0 || r.Height <= 0) continue;
        //                using var patch = new Mat(working, r);
        //                var mean = Cv2.Mean(patch);
        //                double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
        //                if (brightness > blackThreshold * 1.5)
        //                    corners.Add(mean);
        //            }

        //            if (corners.Count > 0)
        //            {
        //                double b = 0, g = 0, r = 0;
        //                foreach (var s in corners) { b += s.Val0; g += s.Val1; r += s.Val2; }
        //                chosenBg = new Scalar(b / corners.Count, g / corners.Count, r / corners.Count);
        //            }
        //            else
        //            {
        //                chosenBg = new Scalar(255, 255, 255);
        //            }
        //        }

        //        // --- маска темных пикселей ---
        //        using var gray = new Mat();
        //        Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

        //        using var darkMask = new Mat();
        //        Cv2.Threshold(gray, darkMask, blackThreshold, 255, ThresholdTypes.BinaryInv); // темные -> 255

        //        // --- connected components: создаём Mats заранее (не используя out) ---
        //        var labels = new Mat();
        //        var stats = new Mat();
        //        var centroids = new Mat();

        //        try
        //        {
        //            // В разных версиях OpenCvSharp есть разные перегрузки; эта вызовет нужную версию
        //            int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, centroids);

        //            // Копия для заполнения
        //            var filled = working.Clone();

        //            if (nLabels > 1)
        //            {
        //                for (int i = 1; i < nLabels; i++)
        //                {
        //                    int x = stats.At<int>(i, 0);
        //                    int y = stats.At<int>(i, 1);
        //                    int w = stats.At<int>(i, 2);
        //                    int h = stats.At<int>(i, 3);

        //                    bool touches = (x <= 0) || (y <= 0) || (x + w >= cols - 1) || (y + h >= rows - 1);
        //                    if (!touches) continue;

        //                    // compMask: где labels == i
        //                    using var compMask = new Mat();
        //                    Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask); // эквивалент labels==i

        //                    // заполняем эту компоненту цветом фона
        //                    filled.SetTo(chosenBg, compMask);
        //                }
        //            }

        //            var result = filled.Clone();
        //            filled.Dispose();
        //            return result;
        //        }
        //        finally
        //        {
        //            labels.Dispose();
        //            stats.Dispose();
        //            centroids.Dispose();
        //        }
        //    }
        //    finally
        //    {
        //        if (createdWorking && working != null)
        //            working.Dispose();
        //    }
        //}

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

        //public double GetSkewAngleByProjection(Mat src, double minAngle = -15, double maxAngle = 15, double coarseStep = 1.0, double refineStep = 0.1)
        //{
        //    // Метод: ищем угол, при котором горизонтальные проекции (row sums) дают наиболее выраженные пики => максимальная дисперсия
        //    // Для скорости работаем на уменьшенной серой бинарной картинке.
        //    int detectWidth = 1000;
        //    Mat small = src.Width > detectWidth ? src.Resize(new OpenCvSharp.Size(detectWidth, (int)(src.Height * (detectWidth / (double)src.Width)))) : src.Clone();

        //    using var gray = new Mat();
        //    Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

        //    // Adaptive threshold или Otsu
        //    using var bw = new Mat();
        //    Cv2.Threshold(gray, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        //    // Инвертируем: текст = 1
        //    Cv2.BitwiseNot(bw, bw);

        //    // Убираем мелкие шумы (опционно)
        //    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        //    Cv2.MorphologyEx(bw, bw, MorphTypes.Open, kernel);

        //    Func<Mat, double> scoreFor = (Mat m) =>
        //    {
        //        // считаем сумму по строкам (double[])
        //        var rowSums = new double[m.Rows];
        //        Func<Mat, double> scoreFor = (Mat m) =>
        //        {
        //            int rows = m.Rows;
        //            int cols = m.Cols;
        //            int stride = (int)m.Step();
        //            var buffer = new byte[stride * rows];
        //            Marshal.Copy(m.Data, buffer, 0, buffer.Length);

        //            double[] rowSums = new double[rows];
        //            for (int r = 0; r < rows; r++)
        //            {
        //                int off = r * stride;
        //                double sum = 0;
        //                for (int c = 0; c < cols; c++)
        //                    sum += buffer[off + c];
        //                rowSums[r] = sum;
        //            }

        //            double mean = rowSums.Average();
        //            double var = rowSums.Select(v => (v - mean) * (v - mean)).Average();
        //            return var;
        //        };
        //        // Нормализуем и считаем дисперсию — большие пики (строки текста) дают большую дисперсию
        //        double mean = rowSums.Average();
        //        double var = rowSums.Select(v => (v - mean) * (v - mean)).Average();
        //        return var;
        //    };

        //    // coarse search
        //    double bestAngle = 0;
        //    double bestScore = double.MinValue;
        //    for (double a = minAngle; a <= maxAngle; a += coarseStep)
        //    {
        //        using var rot = RotateImageForDetection(bw, a);
        //        double s = scoreFor(rot);
        //        if (s > bestScore) { bestScore = s; bestAngle = a; }
        //    }

        //    // refine around bestAngle
        //    double refineMin = Math.Max(minAngle, bestAngle - coarseStep);
        //    double refineMax = Math.Min(maxAngle, bestAngle + coarseStep);
        //    for (double a = refineMin; a <= refineMax; a += refineStep)
        //    {
        //        using var rot = RotateImageForDetection(bw, a);
        //        double s = scoreFor(rot);
        //        if (s > bestScore) { bestScore = s; bestAngle = a; }
        //    }

        //    small.Dispose();
        //    return -bestAngle; // возвращаем знак для поворота (чтобы выпрямить)
        //}

        // Sauvola локальная бинаризация (быстро через boxFilter)
        // srcGray: CV_8UC1 grayscale
        // windowSize: локальное окно (нечетное) — 15..51 (25 обычный старт)
        // k: обычно 0.2..0.5 (0.34 хороший старт)
        // R: динамический диапазон (обычно 128)

        private Mat Sauvola(Mat srcGray, int windowSize = 25, double k = 0.34, double R = 128.0, int pencilStrokeBoost = 0)
        {

            if (srcGray.Empty()) throw new ArgumentException("srcGray is empty");



            Mat gray = srcGray;
            if (gray.Type() != MatType.CV_8UC1)
            {
                gray = new Mat();
                Cv2.CvtColor(srcGray, gray, ColorConversionCodes.BGR2GRAY);
            }

            if (windowSize % 2 == 0) windowSize++; // ensure odd

            // Convert to double for precision
            Mat srcD = new Mat();
            gray.ConvertTo(srcD, MatType.CV_64F);

            // mean = boxFilter(src, ksize) normalized
            Mat mean = new Mat();
            Cv2.BoxFilter(srcD, mean, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // meanSq: compute boxFilter(src*src)
            Mat sq = new Mat();
            Cv2.Multiply(srcD, srcD, sq);
            Mat meanSq = new Mat();
            Cv2.BoxFilter(sq, meanSq, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // std = sqrt(meanSq - mean*mean)
            Mat std = new Mat();
            Cv2.Subtract(meanSq, mean.Mul(mean), std); // std now holds variance
            Cv2.Max(std, 0.0, std); // clamp small negatives
            Cv2.Sqrt(std, std);

            // threshold = mean * (1 + k * (std/R - 1))
            Mat thresh = new Mat();
            Cv2.Divide(std, R, thresh);                 // thresh = std / R
            Cv2.Subtract(thresh, 1.0, thresh);          // thresh = std/R - 1
            Cv2.Multiply(thresh, k, thresh);            // thresh = k*(std/R -1)
            Cv2.Add(thresh, 1.0, thresh);               // thresh = 1 + k*(std/R -1)
            Cv2.Multiply(mean, thresh, thresh);         // thresh = mean * (...)

            double pencilMargin = (double)pencilStrokeBoost; // TODO: вынести в BinarizeParameters (SauvolaPencilMargin)
            using var threshShifted = new Mat();
            Cv2.Add(thresh, new Scalar(pencilMargin), threshShifted);
            Cv2.Min(threshShifted, new Scalar(255.0), threshShifted);

            // binarize: srcD > thresh -> 255 else 0
            Mat bin = new Mat();
            Cv2.Compare(srcD, threshShifted, bin, CmpType.GT); // bin = 0 or 255 (CV_8U after convert)
            bin.ConvertTo(bin, MatType.CV_8UC1, 255.0);  // ensure 0/255

            // Clean-up mats
            srcD.Dispose(); mean.Dispose(); sq.Dispose(); meanSq.Dispose(); std.Dispose(); thresh.Dispose();

            return bin;
        }

        private Mat SauvolaBinarize(Mat src, BinarizeParameters p)
        {
            //Debug.WriteLine($"clahe grid size {p.SauvolaClaheGridSize}");
            using var binMat = BinarizeForHandwritten(src,
                                                      p.SauvolaUseClahe,
                                                      p.SauvolaClaheClip,
                                                      p.SauvolaClaheGridSize,
                                                      p.SauvolaWindowSize,
                                                      p.SauvolaK,
                                                      p.SauvolaR,
                                                      p.SauvolaMorphRadius,
                                                      p.PencilStrokeBoost);

            Mat bin8;
            if (binMat.Type() != MatType.CV_8UC1)
            {
                bin8 = new Mat();
                binMat.ConvertTo(bin8, MatType.CV_8UC1);
            }
            else
            {
                bin8 = binMat.Clone(); // сделаем клон, чтобы безопасно Dispose оригинала ниже
            }

            try
            {
                var colorMat = new Mat();
                Cv2.CvtColor(bin8, colorMat, ColorConversionCodes.GRAY2BGR);
                return colorMat;
            }
            finally
            {
                bin8.Dispose();
            }

        }



        private Mat BinarizeForHandwritten(Mat src, bool useClahe = true, double claheClip = 12.0, int claheGridSize = 8,
                                             int sauvolaWindow = 35, double sauvolaK = 0.34, double sauvolaR = 180, int morphRadius = 0, int pencilStrokeBoost = 0)
        {
            var claheGrid = new OpenCvSharp.Size(claheGridSize, claheGridSize);
            Mat gray = src;
            if (src.Type() != MatType.CV_8UC1)
            {
                gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            }
            Mat pre = gray;
            if (useClahe)
            {
                var clahe = Cv2.CreateCLAHE(claheClip, claheGrid);
                pre = new Mat();
                clahe.Apply(gray, pre);
                clahe.Dispose();
                if (!ReferenceEquals(gray, src)) gray.Dispose();
            }

            var bin = Sauvola(pre, sauvolaWindow, sauvolaK, sauvolaR, pencilStrokeBoost);

            // optional morphological cleaning (open to remove small noise, close to fill holes)
            if (morphRadius > 0)
            {
                var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * morphRadius + 1, 2 * morphRadius + 1));
                var cleaned = new Mat();
                Cv2.MorphologyEx(bin, cleaned, MorphTypes.Open, kernel);
                Cv2.MorphologyEx(cleaned, bin, MorphTypes.Close, kernel);
                kernel.Dispose();
                cleaned.Dispose();
            }

            if (!ReferenceEquals(pre, gray) && pre != src) pre.Dispose();
            if (gray != src && gray != pre) gray.Dispose();

            return bin;
        }
    }
}
