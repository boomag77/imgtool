using ImgViewer.Interfaces;
using OpenCvSharp;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    public class OpenCVImageProcessor : IImageProcessor, IDisposable
    {
        private byte[] _bmpBytes;
        private Mat _currentImage;
        private Scalar _pageColor;
        private Scalar _borderColor;
        private readonly IAppManager _appManager;

        public byte[] BmpBytes
        {
            set
            {
                if (value.Length == 0) return;
                _bmpBytes = value;
                using var ms = new MemoryStream(_bmpBytes);
                _currentImage = Mat.FromStream(ms, ImreadModes.Color);
            }
        }

        public OpenCVImageProcessor(IAppManager appManager, CancellationToken token)
        {
            _appManager = appManager;
            //throw new NotImplementedException();
        }

        

        public void Dispose()
        {
            //throw new NotImplementedException();

        }

        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;

        public void Load(string path)
        {
            //throw new NotImplementedException();
            try
            {
                _currentImage = Cv2.ImRead(path, ImreadModes.Color);
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

        public void SaveImageFax()
        {

        }

        public Stream? GetStreamForSaving(ImageFormat format, TiffCompression compression)
        {
            //throw new NotImplementedException();
            if (_currentImage != null && !_currentImage.Empty())
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
                            if (_currentImage.Channels() != 1)
                            {
                                // для G4 нужно 1-битное изображение
                                using var gray = new Mat();
                                Cv2.CvtColor(_currentImage, gray, ColorConversionCodes.BGR2GRAY);
                                using var bin = new Mat();
                                Cv2.Threshold(gray, bin, 128, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                                _currentImage = bin.Clone();
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
                    byte[] tiffData = _currentImage.ImEncode(".tiff", paramsList.ToArray());
                    return new MemoryStream(tiffData);
                }
                else
                {
                    // для других форматов просто PNG
                    return MatToStream(_currentImage);
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

        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object> parameters = null)
        {
            if (_currentImage != null)
            {
                switch (command)
                {
                    case ProcessorCommands.Binarize:
                        //Binarize();
                        BinarizeAdaptive();
                        break;
                    case ProcessorCommands.Deskew:
                        Deskew();
                        break;
                    case ProcessorCommands.BorderRemove:
                        Debug.WriteLine("ApplyCommandToCurrent: BorderRemove");
                        RemoveBordersByRowColWhite(threshFrac: 0.25, contrastThr: 50, centralSample: 0.50, maxRemoveFrac: 0.25);
                        break;
                    case ProcessorCommands.Despeckle:
                        //applyDespeckleCurrent();
                        break;
                    case ProcessorCommands.AutoCropRectangle:
                        //applyAutoCropRectangleCurrent();
                        break;
                    case ProcessorCommands.LineRemove:
                        //ApplyLinesRemoveCurrent();
                        break;
                    case ProcessorCommands.DotsRemove:
                        //RemoveSpecksWithHandler();
                        break;


                }
                updateImagePreview();
            }
        }

        private void updateImagePreview()
        {
            if (_currentImage != null)
            {
               _appManager.SetBmpImageOnPreview(MatToBitmapSource(_currentImage));
            }
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

        private BitmapSource MatToBitmapSource(Mat mat)
        {
            if (mat == null || mat.Empty())
            {
                Debug.WriteLine("MatToBitmapSource: input Mat is null or empty.");
                return null;
            }

            // Быстрая конвертация через OpenCvSharp.Extensions:
            //var bmp = mat.ToBitmap(); // создаёт System.Drawing.Bitmap (GDI+) — не идеально для WPF
            // Но лучше: создать WriteableBitmap и скопировать байты
            var wb = new WriteableBitmap(mat.Width, mat.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
            int stride = mat.Cols * mat.ElemSize();
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, mat.Width, mat.Height), mat.Data, mat.Rows * stride, stride);
            wb.Freeze();
            return wb;
        }





        //public void Binarize(int threshold = 128)
        //{
        //    //using var mat = BitmapSourceConverter.ToMat(src); // конвертация (может быть из OpenCvSharp.Extensions)
        //    using var gray = new Mat();
        //    Cv2.CvtColor(_currentImage, gray, ColorConversionCodes.BGR2GRAY);
        //    Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);
        //    _currentImage = gray.Clone();
        //    updateImagePreview();
        //    //return MatToBitmapSource(gray);
        //}

        private Mat? MatToGray(Mat src)
        {
            if (src == null || src.Empty()) return null; // уже в градациях серого
            using var gray = new Mat();
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
            src.Dispose();
            src = gray.Clone();
            return src;
        }

        private void BinarizeAdaptive(int? blockSize = null, double C = 7, bool useGaussian = true, bool invert = false)
        {
            if (_currentImage == null || _currentImage.Empty()) return;

            using var gray = MatToGray(_currentImage);
            if (gray == null) return;


            int bs;
            if (blockSize.HasValue && blockSize > 0)
            {
                bs = blockSize.Value;
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
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(3, 3), 0);

            using var bin = new Mat();
            var adaptiveType = useGaussian ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC;
            var threshType = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            Cv2.AdaptiveThreshold(blur, bin, 255, adaptiveType, threshType, bs, C);

            // 5) опционально: морфология небольшая (убрать шум/скрепить буквы) — можно раскомментировать при желании
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel, iterations: 1);

            using var color = new Mat();
            Cv2.CvtColor(bin, color, ColorConversionCodes.GRAY2BGR);

            var result = color.Clone();
            Mat old = null;
            lock (this)
            {
                old = _currentImage;
                _currentImage = result;
            }
            old?.Dispose();

            updateImagePreview();
        }


        private void Binarize(int threshold = 180)
        {
            if (_currentImage == null || _currentImage.Empty()) return;

            using var gray = new Mat();
            Cv2.CvtColor(_currentImage, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);

            // Конвертируем обратно в BGR — тогда весь pipeline, ожидающий 3 канала, продолжит работать
            using var color = new Mat();
            Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);

            _currentImage = color.Clone(); // сохраняем результат как 3-канальную матрицу
            updateImagePreview();
        }

        //threshFrac(0..1) : чем выше — тем жёстче требование к считать строку бордюром.
        //0.6 — хорошая стартовая точка.Для очень толстых рамок можно поднять до 0.75–0.9
        //contrastThr: порог яркости.Для слабых контрастов уменьшите (15..25); для сильных — увеличьте.
        //centralSample: если документ сильно смещён в кадре, уменьшите (например 0.2),
        //либо используйте более устойчивую выборку(несколько областей).
        //maxRemoveFrac: защита от катастрофического удаления.Оставьте не выше 0.3.

        public void RemoveBordersByRowColWhite(
        double threshFrac = 0.60,
        int contrastThr = 30,
        double centralSample = 0.30,
        double maxRemoveFrac = 0.25)
        {
            Debug.WriteLine("RemoveBordersByRowColWhite started. Before checking _currentImage");
            if (_currentImage == null || _currentImage.Empty())
                return;

            Debug.WriteLine("RemoveBordersByRowColWhite started.");

            // Подготовка источника (убедиться, что BGR CV_8UC3)
            Mat src = _currentImage;
            Mat srcBgr = src;
            bool converted = false;
            if (src.Type() != MatType.CV_8UC3)
            {
                srcBgr = new Mat();
                src.ConvertTo(srcBgr, MatType.CV_8UC3);
                converted = true;
            }

            // Грейскейл
            Mat gray = new Mat();
            Cv2.CvtColor(srcBgr, gray, ColorConversionCodes.BGR2GRAY);

            int h = gray.Rows;
            int w = gray.Cols;

            // Ограничим входные параметры
            centralSample = Math.Max(0.05, Math.Min(0.9, centralSample));
            threshFrac = Math.Max(0.01, Math.Min(0.99, threshFrac));
            maxRemoveFrac = Math.Max(0.01, Math.Min(0.5, maxRemoveFrac));

            // Центральный прямоугольник для оценки медианы
            int cx0 = (int)Math.Round(w * (0.5 - centralSample / 2.0));
            int cy0 = (int)Math.Round(h * (0.5 - centralSample / 2.0));
            int cx1 = (int)Math.Round(w * (0.5 + centralSample / 2.0));
            int cy1 = (int)Math.Round(h * (0.5 + centralSample / 2.0));
            cx0 = Math.Max(0, Math.Min(w - 1, cx0));
            cy0 = Math.Max(0, Math.Min(h - 1, cy0));
            cx1 = Math.Max(cx0 + 1, Math.Min(w, cx1));
            cy1 = Math.Max(cy0 + 1, Math.Min(h, cy1));

            Mat central = new Mat(gray, new Rect(cx0, cy0, cx1 - cx0, cy1 - cy0));
            int centralMedian = ComputeMatMedian(central);

            // Считаем количество "не-фон" пикселей в каждой строке и колонке
            int[] rowCounts = new int[h];
            int[] colCounts = new int[w];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte v = gray.At<byte>(y, x);
                    if (Math.Abs(v - centralMedian) > contrastThr)
                    {
                        rowCounts[y]++;
                        colCounts[x]++;
                    }
                }
            }

            // Фракции
            double[] rowFrac = new double[h];
            double[] colFrac = new double[w];
            for (int y = 0; y < h; y++) rowFrac[y] = (double)rowCounts[y] / w;
            for (int x = 0; x < w; x++) colFrac[x] = (double)colCounts[x] / h;

            // Сканируем от краёв
            int top = 0;
            for (int y = 0; y < h; y++)
            {
                if (rowFrac[y] > threshFrac) top = y + 1;
                else break;
            }
            int bottom = 0;
            for (int y = h - 1; y >= 0; y--)
            {
                if (rowFrac[y] > threshFrac) bottom = h - y;
                else break;
            }
            int left = 0;
            for (int x = 0; x < w; x++)
            {
                if (colFrac[x] > threshFrac) left = x + 1;
                else break;
            }
            int right = 0;
            for (int x = w - 1; x >= 0; x--)
            {
                if (colFrac[x] > threshFrac) right = w - x;
                else break;
            }


            // Защита от удаления слишком большой доли
            int maxTop = (int)Math.Round(maxRemoveFrac * h);
            int maxSide = (int)Math.Round(maxRemoveFrac * w);
            if (top > maxTop) top = maxTop;
            if (bottom > maxTop) bottom = maxTop;
            if (left > maxSide) left = maxSide;
            if (right > maxSide) right = maxSide;


            // Применяем заливку белым (in-place в новом Mat)
            Mat result = srcBgr.Clone();
            Scalar white = new Scalar(255, 255, 255);
            if (top > 0) result[new Rect(0, 0, w, top)].SetTo(white);
            if (bottom > 0) result[new Rect(0, h - bottom, w, bottom)].SetTo(white);
            if (left > 0) result[new Rect(0, 0, left, h)].SetTo(white);
            if (right > 0) result[new Rect(w - right, 0, right, h)].SetTo(white);

            // Заменяем поле _currentImage на result (освобождая прежний Mat)
            var old = _currentImage;
            _currentImage = result;
            updateImagePreview();

            // Освобождаем временные объекты
            central.Dispose();
            gray.Dispose();
            if (converted) srcBgr.Dispose(); // если создали новый Mat при конвертации
            old?.Dispose();

            // (опционально) логирование — можно убрать
            Debug.WriteLine($"RemoveBordersByRowColWhite applied: cuts(top,bottom,left,right) = ({top},{bottom},{left},{right}), centralMedian={centralMedian}");
        }

        /// <summary>
        /// Вспомогательная: медиана значений CV_8UC1 Mat.
        /// </summary>
        private static int ComputeMatMedian(Mat grayMat)
        {
            if (grayMat == null || grayMat.Empty()) return 255;
            if (grayMat.Type() != MatType.CV_8UC1)
            {
                using var tmp = new Mat();
                Cv2.CvtColor(grayMat, tmp, ColorConversionCodes.BGR2GRAY);
                grayMat = tmp;
            }

            List<byte> list = new List<byte>(grayMat.Rows * grayMat.Cols);
            for (int y = 0; y < grayMat.Rows; y++)
            {
                for (int x = 0; x < grayMat.Cols; x++)
                {
                    list.Add(grayMat.At<byte>(y, x));
                }
            }

            list.Sort();
            int mid = list.Count / 2;
            if (list.Count == 0) return 255;
            if (list.Count % 2 == 1) return list[mid];
            return (list[mid - 1] + list[mid]) / 2;
        }



        public void Deskew()
        {
            if (_currentImage == null || _currentImage.Empty()) return;


            // 1) Попробуем Hough — быстрее и хорошо работает если видны линии/контуры строк
            double angle = GetSkewAngleByHough(_currentImage, cannyThresh1: 50, cannyThresh2: 150, houghThreshold: 80, minLineLength: Math.Min(_currentImage.Width, 200), maxLineGap: 20);
            Debug.WriteLine($"Deskew: angle by Hough = {angle}");
            // 2) Если Hough ничего надёжного не дал -> используем projection profile
            if (double.IsNaN(angle))
            {
                angle = GetSkewAngleByProjection(_currentImage, minAngle: -15, maxAngle: 15, coarseStep: 1.0, refineStep: 0.2);
            }

            // 3) Если всё ещё NaN - выходим
            if (double.IsNaN(angle) || Math.Abs(angle) < 0.005) // если угол ~0 — не поворачивать
            {
                Debug.WriteLine($"Deskew: angle is zero or NaN ({angle}), skipping rotation.");
                return;
            }


            // 4) Применяем поворот с учётом нового размера (чтобы не обрезать)
            using var src = _currentImage.Clone();
            double rotation = -angle;
            double rad = rotation * Math.PI / 180.0;
            double absCos = Math.Abs(Math.Cos(rad));
            double absSin = Math.Abs(Math.Sin(rad));
            int newW = (int)Math.Round(src.Width * absCos + src.Height * absSin);
            int newH = (int)Math.Round(src.Width * absSin + src.Height * absCos);

            var center = new Point2f(src.Width / 2f, src.Height / 2f);
            var M = Cv2.GetRotationMatrix2D(center, rotation, 1.0);
            M.Set(0, 2, M.Get<double>(0, 2) + (newW / 2.0 - center.X));
            M.Set(1, 2, M.Get<double>(1, 2) + (newH / 2.0 - center.Y));

            using var rotated = new Mat();
            Cv2.WarpAffine(src, rotated, M, new OpenCvSharp.Size(newW, newH), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255)); // 0 - black background

            // 5) (опционально) Обрезаем вокруг содержимого, как в предыдущем примере.
            //using var mask = PrecomputeDarkMask_Otsu(src);
            //using (var mask = PrecomputeDarkMask_Otsu(rotated)) // считаем маску на rotated, а не на src
            //{
            //    // небольшая морфология чтобы закрыть дырки
            //    int morphK = Math.Max(3, Math.Min(rotated.Width, rotated.Height) / 200);
            //    using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(morphK, morphK));
            //    Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel, iterations: 1);

            //    // find external contours, pick the largest by area
            //    Cv2.FindContours(mask, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            //    if (contours != null && contours.Length > 0)
            //    {
            //        int maxIdx = 0;
            //        double maxArea = 0;
            //        for (int i = 0; i < contours.Length; i++)
            //        {
            //            double a = Cv2.ContourArea(contours[i]);
            //            if (a > maxArea) { maxArea = a; maxIdx = i; }
            //        }

            //        var bbox = Cv2.BoundingRect(contours[maxIdx]);

            //        // add padding (feather) but keep inside image bounds
            //        int pad = (int)(Math.Min(bbox.Width, bbox.Height) * 0.02) + 10; // 2% + 10px
            //        int x0 = Math.Max(0, bbox.X - pad);
            //        int y0 = Math.Max(0, bbox.Y - pad);
            //        int x1 = Math.Min(rotated.Width, bbox.X + bbox.Width + pad);
            //        int y1 = Math.Min(rotated.Height, bbox.Y + bbox.Height + pad);
            //        var cropRect = new Rect(x0, y0, x1 - x0, y1 - y0);

            //        if (cropRect.Width > 10 && cropRect.Height > 10)
            //        {
            //            using var cropped = new Mat(rotated, cropRect).Clone(); // .Clone чтобы не зависеть от rotated'а
            //                                                                    // теперь можно применить FillBlackBorderAreas к cropped
            //            var filledCropped = FillBlackBorderAreas(
            //                cropped,
            //                bgColor: null,
            //                blackThreshold: 200,
            //                minSpanFraction: 0.99,
            //                minAreaPx: 10000,
            //                solidityThreshold: 0.1
            //            );

            //            // заменим _currentImage на результат (освободим старый)
            //            _currentImage?.Dispose();
            //            _currentImage = filledCropped;
            //            updateImagePreview();
            //            return; // закончили Deskew
            //        }
            //    }
            //}

            //byte estimatedThr = EstimateBlackThreshold(src, marginPercent: 10, shiftFactor: 0.8);




            //var filled = FillBlackBorderAreas(
            //    rotated,
            //    bgColor: null,
            //    blackThreshold: 100,
            //    minSpanFraction: 0.9,
            //    minAreaPx: 2000,
            //    solidityThreshold: 0.1
            //);


            // Здесь можно повторно бинаризовать rotated и обрезать по самому большому контуру.
            // Для простоты сразу сохраняем rotated.
            //_currentImage?.Dispose();
            var result = rotated.Clone();
            _currentImage = result;

            //_currentImage = cleaned;
            //_currentImage = rotated.Clone();
            updateImagePreview();
        }








        Mat PrecomputeDarkMask_BackgroundNormalized(Mat src)
        {
            var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

            // оценка фона большим ядром (например 101x101)
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(101, 101));
            var bg = new Mat();
            Cv2.MorphologyEx(gray, bg, MorphTypes.Open, kernel);

            // вычитаем фон — получаем более ровную яркость
            var norm = new Mat();
            Cv2.Subtract(gray, bg, norm);

            // optional contrast
            Cv2.Normalize(norm, norm, 0, 255, NormTypes.MinMax);

            // Otsu или адаптивный порог
            var darkMask = new Mat();
            Cv2.Threshold(norm, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

            gray.Dispose(); bg.Dispose(); norm.Dispose();
            return darkMask;
        }

        public Mat RemoveBorderArtifactsGeneric_Safe(
            Mat src,
            byte thr,                       // порог для определения "тёмного" (например EstimateBlackThreshold(rotated))
            Scalar? bgColor = null,         // цвет фона (null -> автоопределение по углам)
            int minAreaPx = 2000,           // если площадь >= этого -> считается значимой
            double minSpanFraction = 0.6,   // если bbox покрывает >= этой доли по ширине/высоте -> кандидат
            double solidityThreshold = 0.6, // если solidity >= -> кандидат
            double minDepthFraction = 0.05, // проникновение вглубь, в долях min(rows,cols)
            int featherPx = 12              // радиус растушёвки для мягкого перехода
        )
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src;

            using var srcClone = src.Clone();
            Mat working = srcClone;
            bool disposeWorking = false;
            if (srcClone.Type() != MatType.CV_8UC3)
            {
                working = new Mat();
                srcClone.ConvertTo(working, MatType.CV_8UC3);
                disposeWorking = true;
            }

            try
            {
                int rows = working.Rows, cols = working.Cols;
                int minDepthPx = (int)Math.Round(minDepthFraction * Math.Min(rows, cols));

                // determine bg color from corners if not provided
                Scalar chosenBg = bgColor ?? new Scalar(255, 255, 255);
                if (!bgColor.HasValue)
                {
                    int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
                    double sb = 0, sg = 0, sr = 0; int cnt = 0;
                    var rects = new[]
                    {
                new Rect(0,0,cornerSize,cornerSize),
                new Rect(Math.Max(0,cols-cornerSize),0,cornerSize,cornerSize),
                new Rect(0,Math.Max(0,rows-cornerSize),cornerSize,cornerSize),
                new Rect(Math.Max(0,cols-cornerSize), Math.Max(0,rows-cornerSize), cornerSize, cornerSize)
            };
                    foreach (var r in rects)
                    {
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        using var patch = new Mat(working, r);
                        var mean = Cv2.Mean(patch);
                        double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                        if (brightness > thr * 1.0) { sb += mean.Val0; sg += mean.Val1; sr += mean.Val2; cnt++; }
                    }
                    if (cnt > 0) chosenBg = new Scalar(sb / cnt, sg / cnt, sr / cnt);
                }

                // 1) binary dark mask
                using var gray = new Mat();
                Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);
                using var darkMask = new Mat();
                Cv2.Threshold(gray, darkMask, thr, 255, ThresholdTypes.BinaryInv); // dark->255

                //+++
                int kernelWidth = Math.Max(15, working.Cols / 30);
                using var longKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(kernelWidth, 3));
                Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Close, longKernel);
                using var smallK = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.Dilate(darkMask, darkMask, smallK, iterations: 1);
                //+++

                // small open to reduce noise
                using (var kOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
                {
                    Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kOpen);
                }

                // 2) connected components
                using var labels = new Mat();
                using var stats = new Mat();
                using var cents = new Mat();
                int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, cents);

                // selected mask init
                //  var selectedMask = Mat.Zeros(darkMask.Size(), MatType.CV_8U);
                var selectedMask = new Mat(darkMask.Size(), MatType.CV_8U, Scalar.All(0));

                // iterate components
                for (int i = 1; i < nLabels; i++)
                {
                    int x = stats.At<int>(i, 0);
                    int y = stats.At<int>(i, 1);
                    int w = stats.At<int>(i, 2);
                    int h = stats.At<int>(i, 3);
                    int area = stats.At<int>(i, 4);

                    bool touchesLeft = x <= 0;
                    bool touchesTop = y <= 0;
                    bool touchesRight = (x + w) >= (cols - 1);
                    bool touchesBottom = (y + h) >= (rows - 1);
                    if (!(touchesLeft || touchesTop || touchesRight || touchesBottom)) continue;

                    double solidity = (w > 0 && h > 0) ? (double)area / (w * h) : 0.0;
                    double spanFractionW = (double)w / cols;
                    double spanFractionH = (double)h / rows;

                    // compute maxDepth by scanning the component mask area (safe, no ToArray)
                    int maxDepth = 0;
                    using (var compMask = new Mat())
                    {
                        Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask); // compMask: 255 where label==i

                        // scan bounding box only for speed
                        int x0 = Math.Max(0, x);
                        int y0 = Math.Max(0, y);
                        int x1 = Math.Min(cols - 1, x + w - 1);
                        int y1 = Math.Min(rows - 1, y + h - 1);

                        for (int yy = y0; yy <= y1; yy++)
                        {
                            for (int xx = x0; xx <= x1; xx++)
                            {
                                byte v = compMask.At<byte>(yy, xx);
                                if (v == 0) continue;
                                int d = Math.Min(Math.Min(xx, cols - 1 - xx), Math.Min(yy, rows - 1 - yy));
                                if (d > maxDepth) maxDepth = d;
                            }
                        }

                        bool select = false;
                        if (area >= minAreaPx) select = true;
                        if (solidity >= solidityThreshold) select = true;
                        if (spanFractionW >= minSpanFraction && (touchesTop || touchesBottom)) select = true;
                        if (spanFractionH >= minSpanFraction && (touchesLeft || touchesRight)) select = true;
                        if (maxDepth >= minDepthPx) select = true;
                        if ((touchesLeft && touchesRight) || (touchesTop && touchesBottom)) select = true;

                        if (select)
                        {
                            // add compMask -> selectedMask (BitwiseOr). Use Cv2.BitwiseOr with real Mats.
                            Cv2.BitwiseOr(selectedMask, compMask, selectedMask);
                        }
                    }
                }

                // if nothing selected -> return clone
                if (Cv2.CountNonZero(selectedMask) == 0)
                    return working.Clone();

                // 3) fill selected areas with background color (hard fill)
                var filled = working.Clone();
                filled.SetTo(chosenBg, selectedMask);

                // 4) smooth the seam: create blurred (soft) mask and do local per-pixel blend in ROI
                // create blurred mask (CV_8U -> blurred uchar)
                using var blurred = new Mat();
                int ksize = Math.Max(3, (featherPx / 2) * 2 + 1);
                Cv2.GaussianBlur(selectedMask, blurred, new OpenCvSharp.Size(ksize, ksize), 0);

                // compute bounding box of blurred mask (scan for nonzero)
                int top = -1, bottom = -1, left = -1, right = -1;
                for (int r = 0; r < rows; r++)
                {
                    for (int c = 0; c < cols; c++)
                    {
                        if (blurred.At<byte>(r, c) != 0)
                        {
                            if (top == -1 || r < top) top = r;
                            if (bottom == -1 || r > bottom) bottom = r;
                            if (left == -1 || c < left) left = c;
                            if (right == -1 || c > right) right = c;
                        }
                    }
                }

                // if no nonzero found (should not), return filled
                if (top == -1) return filled.Clone();

                // expand ROI a bit
                top = Math.Max(0, top - featherPx);
                bottom = Math.Min(rows - 1, bottom + featherPx);
                left = Math.Max(0, left - featherPx);
                right = Math.Min(cols - 1, right + featherPx);

                // per-pixel blend inside ROI using blurred mask as alpha (0..255)
                for (int r = top; r <= bottom; r++)
                {
                    for (int c = left; c <= right; c++)
                    {
                        byte a = blurred.At<byte>(r, c); // 0..255
                        if (a == 0) continue; // no change
                        if (a == 255)
                        {
                            // fully filled already
                            // ensure pixel in result is bg (it is because filled.SetTo done)
                            continue;
                        }
                        // alpha normalized
                        double alpha = a / 255.0;
                        var origB = working.At<Vec3b>(r, c);
                        var fillB = filled.At<Vec3b>(r, c);
                        byte nb = (byte)Math.Round(fillB.Item0 * alpha + origB.Item0 * (1 - alpha));
                        byte ng = (byte)Math.Round(fillB.Item1 * alpha + origB.Item1 * (1 - alpha));
                        byte nr = (byte)Math.Round(fillB.Item2 * alpha + origB.Item2 * (1 - alpha));
                        // write to filled result
                        filled.Set<Vec3b>(r, c, new Vec3b(nb, ng, nr));
                    }
                }

                return filled;
            }
            finally
            {
                if (disposeWorking && working != null) working.Dispose();
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


        Mat PrecomputeDarkMask_Otsu(Mat src)
        {
            var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);

            // небольшая фильтрация шума
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

            // Otsu + инверсия: dark -> 255
            var darkMask = new Mat();
            Cv2.Threshold(gray, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

            // убираем мелкие отверстия/шум
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kernel);

            gray.Dispose();
            return darkMask; // caller обязан Dispose
        }

        Mat PrecomputeDarkMask_Adaptive(Mat src, int blockSize = 31, int C = 10)
        {
            var gray = new Mat();
            // лучше работать с яркостным каналом Y
            var ycrcb = new Mat();
            Cv2.CvtColor(src, ycrcb, ColorConversionCodes.BGR2YCrCb);
            Cv2.ExtractChannel(ycrcb, gray, 0); // Y канал
            ycrcb.Dispose();

            // опционально CLAHE, чтобы усилить контраст
            var clahe = Cv2.CreateCLAHE(2.0, new OpenCvSharp.Size(8, 8));
            clahe.Apply(gray, gray);

            // сглаживание
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

            var darkMask = new Mat();
            // AdaptiveThreshold: используем BinaryInv чтобы тёмные стали 255
            Cv2.AdaptiveThreshold(gray, darkMask, 255,
                AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, blockSize, C);

            // морфологическая очистка
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kernel);

            gray.Dispose();
            clahe.Dispose();
            return darkMask;
        }





        public Mat FillBlackBorderAreas(
            Mat src,
            Scalar? bgColor = null,
            byte blackThreshold = 8,
            double minSpanFraction = 0.8,    // доля ширины/высоты, чтобы считать компонент "полосой"
            int minAreaPx = 2000,            // если компонент >= этой площади — можно считать большим
            double solidityThreshold = 0.55  // если заполненность bbox >= threshold => считать сплошной
        )
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src;

            using var srcClone = src.Clone();

            Mat working;
            bool createdWorking = false;
            if (srcClone.Channels() == 1)
            {
                working = new Mat();
                Cv2.CvtColor(srcClone, working, ColorConversionCodes.GRAY2BGR);
                createdWorking = true;
            }
            else if (srcClone.Type() != MatType.CV_8UC3)
            {
                working = new Mat();
                srcClone.ConvertTo(working, MatType.CV_8UC3);
                createdWorking = true;
            }
            else
            {
                working = srcClone;
                createdWorking = false;
            }

            try
            {
                int rows = working.Rows;
                int cols = working.Cols;

                // --- опред. цвета фона (углы) ---
                Scalar chosenBg;
                if (bgColor.HasValue) chosenBg = bgColor.Value;
                else
                {
                    int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
                    var cornerMeans = new List<Scalar>();
                    var rects = new[]
                    {
                        new Rect(0, 0, cornerSize, cornerSize),
                        new Rect(Math.Max(0, cols - cornerSize), 0, cornerSize, cornerSize),
                        new Rect(0, Math.Max(0, rows - cornerSize), cornerSize, cornerSize),
                        new Rect(Math.Max(0, cols - cornerSize), Math.Max(0, rows - cornerSize), cornerSize, cornerSize)
                    };
                    foreach (var r in rects)
                    {
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        using var patch = new Mat(working, r);
                        var mean = Cv2.Mean(patch);
                        double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                        if (brightness > blackThreshold * 1.5) cornerMeans.Add(mean);
                    }
                    if (cornerMeans.Count > 0)
                    {
                        double b = 0, g = 0, rr = 0;
                        foreach (var s in cornerMeans) { b += s.Val0; g += s.Val1; rr += s.Val2; }
                        chosenBg = new Scalar(b / cornerMeans.Count, g / cornerMeans.Count, rr / cornerMeans.Count);
                    }
                    else chosenBg = new Scalar(255, 255, 255);
                }

                // --- маска тёмных пикселей ---
                using var gray = new Mat();
                Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

                using var darkMask = new Mat();
                Cv2.Threshold(gray, darkMask, blackThreshold, 255, ThresholdTypes.BinaryInv); // dark -> 255

                // --- компоненты связности ---
                using var labels = new Mat();
                using var stats = new Mat();
                using var cents = new Mat();
                int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, cents);

                var filled = working.Clone();

                if (nLabels > 1)
                {
                    for (int i = 1; i < nLabels; i++)
                    {
                        int x = stats.At<int>(i, 0);
                        int y = stats.At<int>(i, 1);
                        int w = stats.At<int>(i, 2);
                        int h = stats.At<int>(i, 3);
                        int area = stats.At<int>(i, 4);

                        bool touchesLeft = x <= 0;
                        bool touchesTop = y <= 0;
                        bool touchesRight = (x + w) >= (cols);
                        bool touchesBottom = (y + h) >= (rows);

                        bool touchesAny = touchesLeft || touchesTop || touchesRight || touchesBottom;
                        if (!touchesAny) continue;

                        // основные эвристики:
                        bool considerAsBorder = false;

                        // 1) span: если касается top/bottom — смотрим ширину
                        if (touchesTop || touchesBottom)
                        {
                            double widthFraction = (double)w / cols;
                            if (widthFraction >= minSpanFraction) considerAsBorder = true;
                        }

                        // 2) span: если касается left/right — смотрим высоту
                        if (touchesLeft || touchesRight)
                        {
                            double heightFraction = (double)h / rows;
                            if (heightFraction >= minSpanFraction) considerAsBorder = true;
                        }

                        // 3) площадь: очень большие объекты можно закрашивать
                        if (area >= minAreaPx) considerAsBorder = true;

                        // 4) противоположные стороны -> явно полоса
                        if ((touchesLeft && touchesRight) || (touchesTop && touchesBottom))
                            considerAsBorder = true;

                        // 5) solidity = area / (w*h) — для сплошной заливки близко к 1, для текста значительно меньше.
                        double solidity = 0.0;
                        if (w > 0 && h > 0) solidity = (double)area / (w * h);
                        if (solidity >= solidityThreshold) considerAsBorder = true;

                        // Доп. эвристика: плотность внутри (простая) — если плотность пикселей низкая, это обычно текст (пропускаем)
                        // (но уже учтено в solidity)

                        if (!considerAsBorder) continue;

                        // наконец — маска этой компоненты
                        using var compMask = new Mat();
                        Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask);

                        // Но перед заливкой: можно дополнительно убедиться, что средняя яркость
                        // внутри bbox не слишком похожа на внутреннюю область документа (опционально).
                        // Для простоты — сразу зальём:
                        //-------

                        filled.SetTo(chosenBg, compMask);
                    }
                }

                var result = filled.Clone();
                filled.Dispose();
                return result;
            }
            finally
            {
                if (createdWorking && working != null) working.Dispose();
            }
        }

        public Mat FillBlackBorderAreasOld(Mat src, Scalar? bgColor = null, byte blackThreshold = 8)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src;

            // работаем с клоном входа
            using var srcClone = src.Clone();

            Mat working = null;
            bool createdWorking = false;
            if (srcClone.Channels() == 1)
            {
                working = new Mat();
                Cv2.CvtColor(srcClone, working, ColorConversionCodes.GRAY2BGR);
                createdWorking = true;
            }
            else if (srcClone.Type() != MatType.CV_8UC3)
            {
                working = new Mat();
                srcClone.ConvertTo(working, MatType.CV_8UC3);
                createdWorking = true;
            }
            else
            {
                working = srcClone;
                createdWorking = false;
            }

            try
            {
                int rows = working.Rows;
                int cols = working.Cols;

                // --- определяем цвет фона (простая стратегия по углам) ---
                Scalar chosenBg;
                if (bgColor.HasValue)
                {
                    chosenBg = bgColor.Value;
                }
                else
                {
                    int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
                    var corners = new List<Scalar>();
                    var rects = new[]
                    {
                new Rect(0, 0, cornerSize, cornerSize),
                new Rect(Math.Max(0, cols - cornerSize), 0, cornerSize, cornerSize),
                new Rect(0, Math.Max(0, rows - cornerSize), cornerSize, cornerSize),
                new Rect(Math.Max(0, cols - cornerSize), Math.Max(0, rows - cornerSize), cornerSize, cornerSize)
            };

                    foreach (var r in rects)
                    {
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        using var patch = new Mat(working, r);
                        var mean = Cv2.Mean(patch);
                        double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                        if (brightness > blackThreshold * 1.5)
                            corners.Add(mean);
                    }

                    if (corners.Count > 0)
                    {
                        double b = 0, g = 0, r = 0;
                        foreach (var s in corners) { b += s.Val0; g += s.Val1; r += s.Val2; }
                        chosenBg = new Scalar(b / corners.Count, g / corners.Count, r / corners.Count);
                    }
                    else
                    {
                        chosenBg = new Scalar(255, 255, 255);
                    }
                }

                // --- маска темных пикселей ---
                using var gray = new Mat();
                Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

                using var darkMask = new Mat();
                Cv2.Threshold(gray, darkMask, blackThreshold, 255, ThresholdTypes.BinaryInv); // темные -> 255

                // --- connected components: создаём Mats заранее (не используя out) ---
                var labels = new Mat();
                var stats = new Mat();
                var centroids = new Mat();

                try
                {
                    // В разных версиях OpenCvSharp есть разные перегрузки; эта вызовет нужную версию
                    int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, centroids);

                    // Копия для заполнения
                    var filled = working.Clone();

                    if (nLabels > 1)
                    {
                        for (int i = 1; i < nLabels; i++)
                        {
                            int x = stats.At<int>(i, 0);
                            int y = stats.At<int>(i, 1);
                            int w = stats.At<int>(i, 2);
                            int h = stats.At<int>(i, 3);

                            bool touches = (x <= 0) || (y <= 0) || (x + w >= cols - 1) || (y + h >= rows - 1);
                            if (!touches) continue;

                            // compMask: где labels == i
                            using var compMask = new Mat();
                            Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask); // эквивалент labels==i

                            // заполняем эту компоненту цветом фона
                            filled.SetTo(chosenBg, compMask);
                        }
                    }

                    var result = filled.Clone();
                    filled.Dispose();
                    return result;
                }
                finally
                {
                    labels.Dispose();
                    stats.Dispose();
                    centroids.Dispose();
                }
            }
            finally
            {
                if (createdWorking && working != null)
                    working.Dispose();
            }
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

        public double GetSkewAngleByProjection(Mat src, double minAngle = -15, double maxAngle = 15, double coarseStep = 1.0, double refineStep = 0.1)
        {
            // Метод: ищем угол, при котором горизонтальные проекции (row sums) дают наиболее выраженные пики => максимальная дисперсия
            // Для скорости работаем на уменьшенной серой бинарной картинке.
            int detectWidth = 1000;
            Mat small = src.Width > detectWidth ? src.Resize(new OpenCvSharp.Size(detectWidth, (int)(src.Height * (detectWidth / (double)src.Width)))) : src.Clone();

            using var gray = new Mat();
            Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);

            // Adaptive threshold или Otsu
            using var bw = new Mat();
            Cv2.Threshold(gray, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            // Инвертируем: текст = 1
            Cv2.BitwiseNot(bw, bw);

            // Убираем мелкие шумы (опционно)
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(bw, bw, MorphTypes.Open, kernel);

            Func<Mat, double> scoreFor = (Mat m) =>
            {
                // считаем сумму по строкам (double[])
                var rowSums = new double[m.Rows];
                Func<Mat, double> scoreFor = (Mat m) =>
                {
                    int rows = m.Rows;
                    int cols = m.Cols;
                    int stride = (int)m.Step();
                    var buffer = new byte[stride * rows];
                    Marshal.Copy(m.Data, buffer, 0, buffer.Length);

                    double[] rowSums = new double[rows];
                    for (int r = 0; r < rows; r++)
                    {
                        int off = r * stride;
                        double sum = 0;
                        for (int c = 0; c < cols; c++)
                            sum += buffer[off + c];
                        rowSums[r] = sum;
                    }

                    double mean = rowSums.Average();
                    double var = rowSums.Select(v => (v - mean) * (v - mean)).Average();
                    return var;
                };
                // Нормализуем и считаем дисперсию — большие пики (строки текста) дают большую дисперсию
                double mean = rowSums.Average();
                double var = rowSums.Select(v => (v - mean) * (v - mean)).Average();
                return var;
            };

            // coarse search
            double bestAngle = 0;
            double bestScore = double.MinValue;
            for (double a = minAngle; a <= maxAngle; a += coarseStep)
            {
                using var rot = RotateImageForDetection(bw, a);
                double s = scoreFor(rot);
                if (s > bestScore) { bestScore = s; bestAngle = a; }
            }

            // refine around bestAngle
            double refineMin = Math.Max(minAngle, bestAngle - coarseStep);
            double refineMax = Math.Min(maxAngle, bestAngle + coarseStep);
            for (double a = refineMin; a <= refineMax; a += refineStep)
            {
                using var rot = RotateImageForDetection(bw, a);
                double s = scoreFor(rot);
                if (s > bestScore) { bestScore = s; bestAngle = a; }
            }

            small.Dispose();
            return -bestAngle; // возвращаем знак для поворота (чтобы выпрямить)
        }
    }
}
