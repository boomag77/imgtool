using OpenCvSharp;

using System.Diagnostics;

namespace ImgViewer.Models
{
    public class Deskewer
    {

        private static double GetSkewAngleByBorders(CancellationToken token, Mat src, int cannyThresh1 = 50, int cannyThresh2 = 150,
                                   int morphKernel = 5, double minAreaFraction = 0.2)
        {
            if (src == null || src.Empty()) return double.NaN;

            token.ThrowIfCancellationRequested();
            // 1. grayscale
            using var gray = new Mat();
            if (src.Channels() == 3)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
            else
                src.CopyTo(gray);

            token.ThrowIfCancellationRequested();
            // 2. чуть размытие
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

            // 3. Попробуем адаптивный порог (лучше для разнотонных сканов)
            using var bin = new Mat();
            Cv2.AdaptiveThreshold(gray, bin, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 31, 10);
            // Если бордер тонкий/пунктирный — его лучше "склеить"
            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(morphKernel, morphKernel));
            token.ThrowIfCancellationRequested();
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel, iterations: 2);

            // 4. Альтернативно: Canny + close (иногда лучше выделяет линии)
            //var edges = new Mat();
            //Cv2.Canny(gray, edges, cannyThresh1, cannyThresh2);
            //Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, iterations: 2);

            token.ThrowIfCancellationRequested();
            // 5. Найти контуры
            Cv2.FindContours(bin, out OpenCvSharp.Point[][] contours, out HierarchyIndex[] _,
                             RetrievalModes.External, ContourApproximationModes.ApproxSimple);

            if (contours == null || contours.Length == 0) return double.NaN;

            // 6. Выбрать самый большой контур по площади
            double maxArea = 0;
            int maxIdx = -1;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area > maxArea) { maxArea = area; maxIdx = i; }
            }

            if (maxIdx < 0) return double.NaN;

            // Если площадь слишком мала по сравнению с картинкой — возможно нет рамки
            double imageArea = src.Width * (double)src.Height;
            if (maxArea < imageArea * (minAreaFraction * 0.01)) // minAreaFraction в процентах (например 20 = 20%)
                return double.NaN;

            var biggest = contours[maxIdx];

            token.ThrowIfCancellationRequested();
            // 7. Пытаемся аппроксимировать полигонон (проверим, есть ли четырёхугольник)
            var approx = Cv2.ApproxPolyDP(biggest, Cv2.ArcLength(biggest, true) * 0.02, true);

            double angle = double.NaN;

            if (approx.Length == 4)
            {
                // Найдём сторону с наибольшей длиной (обычно это ширина/высота рамки)
                double bestLen = 0;
                OpenCvSharp.Point p0 = approx[0], p1 = approx[1];
                for (int i = 0; i < 4; i++)
                {
                    var a = approx[i];
                    var b = approx[(i + 1) % 4];
                    double dx = b.X - a.X;
                    double dy = b.Y - a.Y;
                    double len = dx * dx + dy * dy; // квадрат длины
                    if (len > bestLen)
                    {
                        bestLen = len;
                        p0 = a; p1 = b;
                    }
                }
                // угол стороны p0->p1
                double ang = Math.Atan2(p1.Y - p0.Y, p1.X - p0.X) * 180.0 / Math.PI;
                // нормализуем к -90..90 и ближе к 0 (горизонтальной)
                if (ang > 90) ang -= 180;
                if (ang <= -90) ang += 180;
                // Свести к ближайшему горизонтальному (если рамка вертикальная, угол будет ~90 или ~-90,
                // но мы хотим угол, который сделает стороны параллельны краям)
                if (Math.Abs(ang) > 45) ang += (ang > 0 ? -90 : 90);
                angle = ang;
            }
            else
            {
                // fallback: минимальный вращённый прямоугольник
                var r = Cv2.MinAreaRect(biggest); // RotatedRect
                                                  // RotatedRect.Angle: в OpenCV даёт угол относительно горизонтали, но семантика может отличаться по sign
                double ang = r.Angle; // обычно в диапазоне (-90..0] или (-90..90), проверяйте на примерах
                                      // Нормализуем в -90..90
                if (ang > 90) ang -= 180;
                if (ang <= -90) ang += 180;
                // Приводим к углу, близкому к горизонтали:
                if (Math.Abs(ang) > 45) ang += (ang > 0 ? -90 : 90);
                angle = ang;
            }

            // Финальная валидация: если угол слишком мал — можно вернуть NaN (нет смысла)
            if (double.IsNaN(angle) || Math.Abs(angle) < 0.02) return double.NaN;

            return angle;
        }

        public struct Parameters
        {
            public bool byBorders { get; set; }
            public int cTresh1 { get; set; }
            public int cTresh2 { get; set; }
            public int morphKernel { get; set; }

            public int houghTreshold { get; set; }
            public int minLineLength { get; set; }

        }


        public static Mat Deskew(CancellationToken token, Mat orig, bool byBorders, int cTresh1, int cTresh2, int morphK, int minLL, int houghTresh)
        {
            if (orig == null || orig.Empty()) return orig;

            // работаем с копией
            var src0 = orig; // do not dispose caller-owned Mat
                                   // приводим src к CV_8UC3 (BGR) — это безопаснее для дальнейшей обработки/копирования
            using var src = EnsureBgr(src0);

            double finalAngle = double.NaN;
            if (byBorders)
            {
                double borderAngle = GetSkewAngleByBorders(token, src,
                    cannyThresh1: cTresh1,
                    cannyThresh2: cTresh2,
                    morphKernel: morphK,
                    minAreaFraction: 0.2);

                Debug.WriteLine($"Deskew: angle by Borders = {borderAngle:F3}");

                finalAngle = borderAngle;
                if (double.IsNaN(borderAngle))
                {
                    return src.Clone();
                }
            }
            else
            {
                
                // 1) candidate angles
                double houghAngle = GetSkewAngleByHough(token, src, cannyThresh1: cTresh1, cannyThresh2: cTresh2, houghThreshold: houghTresh, minLineLength: minLL, maxLineGap: 20);
                Debug.WriteLine($"Deskew: angle by Hough = {houghAngle:F3}");


                
                double projAngle = GetSkewAngleByProjection(token, src, minAngle: -15, maxAngle: 15, coarseStep: 1.0, refineStep: 0.2);
                Debug.WriteLine($"Deskew: angle by Projection = {projAngle:F3}");

                
                double pcaAngle = GetSkewAngleByPCA(token, src);
                Debug.WriteLine($"Deskew: angle by PCA = {pcaAngle:F3}");

                //double finalAngle = double.NaN;
                if (!double.IsNaN(houghAngle)) finalAngle = houghAngle;
                if (double.IsNaN(finalAngle) && !double.IsNaN(pcaAngle)) finalAngle = pcaAngle;
                if (double.IsNaN(finalAngle)) finalAngle = projAngle;

                if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
                {
                    Debug.WriteLine($"Deskew: angle is zero or NaN ({finalAngle}), skipping rotation.");
                    return src.Clone(); // возвращаем копию BGR
                }
            }



            if (finalAngle > 45) finalAngle -= 90;
            if (finalAngle < -45) finalAngle += 90;

            // вращаем на большом холсте
            double rotation = finalAngle;
            double rad = rotation * Math.PI / 180.0;
            double absCos = Math.Abs(Math.Cos(rad));
            double absSin = Math.Abs(Math.Sin(rad));
            int bigW = (int)Math.Round(src.Width * absCos + src.Height * absSin);
            int bigH = (int)Math.Round(src.Width * absSin + src.Height * absCos);
            var centerBig = new Point2f(bigW / 2f, bigH / 2f);

            var borderRgb = GetBorderColor(token, src);
            byte rb = (byte)((borderRgb >> 16) & 0xFF);
            byte gb = (byte)((borderRgb >> 8) & 0xFF);
            byte bb = (byte)(borderRgb & 0xFF);
            var bgScalar = new Scalar(bb, gb, rb); // OpenCV uses BGR order

            bgScalar = Scalar.All(0);

            int rws = src.Rows;
            int cls = src.Cols;
            var thr = EstimateBlackThreshold(src);
            int cornerSize = Math.Max(2, Math.Min(32, Math.Min(rws, cls) / 30));
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
                token.ThrowIfCancellationRequested();
                if (r.Width <= 0 || r.Height <= 0) continue;
                using var patch = new Mat(src, r);
                var mean = Cv2.Mean(patch);
                double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                if (brightness > thr * 1.0) { sb += mean.Val0; sg += mean.Val1; sr += mean.Val2; cnt++; }
            }
            if (cnt > 0) bgScalar = new Scalar(sb / cnt, sg / cnt, sr / cnt);

            using var big = new Mat(new OpenCvSharp.Size(bigW, bigH), MatType.CV_8UC3, bgScalar);
            int offX = (bigW - src.Width) / 2;
            int offY = (bigH - src.Height) / 2;
            src.CopyTo(new Mat(big, new Rect(offX, offY, src.Width, src.Height)));

            var M = Cv2.GetRotationMatrix2D(centerBig, rotation, 1.0);
            using var rotatedBig = new Mat();

            token.ThrowIfCancellationRequested();
           
            Cv2.WarpAffine(big, rotatedBig, M, new OpenCvSharp.Size(bigW, bigH), InterpolationFlags.Linear, BorderTypes.Constant, bgScalar);

            // вычисляем маску на rotatedBig (CV_8UC1)
            using var mask = BinarizeToMask(rotatedBig);
            if (mask.Empty())
            {
                return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, centerBig);
            }

            token.ThrowIfCancellationRequested();

            using var nonZeroMat = new Mat();
            Cv2.FindNonZero(mask, nonZeroMat);
            if (nonZeroMat.Empty())
            {
                return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, centerBig);
            }

            // конвертируем в Point[]
            OpenCvSharp.Point[] nzPoints;
            int rows = nonZeroMat.Rows;
            nzPoints = new OpenCvSharp.Point[rows];
            for (int i = 0; i < rows; i++)
            {
                var v = nonZeroMat.At<Vec2i>(i, 0);
                nzPoints[i] = new OpenCvSharp.Point(v.Item0, v.Item1);
            }

            if (nzPoints == null || nzPoints.Length == 0)
            {
                return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, centerBig);
            }

            // bounding rect должен принимть Point[] — так безопаснее
            var contentRect = Cv2.BoundingRect(nzPoints);
            var contentCenter = new Point2f(contentRect.X + contentRect.Width / 2f, contentRect.Y + contentRect.Height / 2f);

            // кадрируем / дополняем до исходного размера, центрируя по содержимому
            //return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, contentCenter);
            var result = rotatedBig.Clone();
            return result;
        }

        private static byte EstimateBlackThreshold(Mat img, int marginPercent = 10, double shiftFactor = 0.25)
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

        // --- вспомогательные методы ---

        private static Mat EnsureBgr(Mat src)
        {
            if (src == null) return null;
            if (src.Channels() == 3)
                return src.Clone();
            if (src.Channels() == 1)
            {
                var bgr = new Mat();
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
                return bgr;
            }
            if (src.Channels() == 4)
            {
                var bgr = new Mat();
                Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
                return bgr;
            }
            // fallback: попытка приведения через CVT
            var fallback = new Mat();
            Cv2.CvtColor(src, fallback, ColorConversionCodes.BGR2GRAY); // просто чтобы не падало
            Cv2.CvtColor(fallback, fallback, ColorConversionCodes.GRAY2BGR);
            return fallback;
        }


        private static Mat BinarizeToMask(Mat src)
        {
            // возвращаем CV_8UC1 маску (0/255)
            Mat gray = new Mat();
            if (src.Channels() == 3) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4) Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
            else gray = src.Clone(); // 1 канал

            var bin = new Mat();
            Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

            var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel, iterations: 1);

            // gray может быть удалён вызывающим кодом, вернём bin
            return bin;
        }

        private static Mat CropOrPadToOriginal(Mat bigImg, int targetW, int targetH, Point2f keepCenter)
        {
            int x = (int)Math.Round(keepCenter.X - targetW / 2.0);
            int y = (int)Math.Round(keepCenter.Y - targetH / 2.0);

            if (x < 0) x = 0;
            if (y < 0) y = 0;
            if (x + targetW > bigImg.Width) x = Math.Max(0, bigImg.Width - targetW);
            if (y + targetH > bigImg.Height) y = Math.Max(0, bigImg.Height - targetH);

            if (bigImg.Width < targetW || bigImg.Height < targetH)
            {
                var outMat = new Mat(new OpenCvSharp.Size(targetW, targetH), MatType.CV_8UC3, Scalar.All(255));
                int dstX = Math.Max(0, (targetW - bigImg.Width) / 2);
                int dstY = Math.Max(0, (targetH - bigImg.Height) / 2);
                var dstRoi = new Rect(dstX, dstY, Math.Min(bigImg.Width, targetW), Math.Min(bigImg.Height, targetH));
                bigImg.CopyTo(new Mat(outMat, dstRoi));
                return outMat;
            }

            var roi = new Rect(x, y, targetW, targetH);
            return new Mat(bigImg, roi).Clone();
        }

        private static double GetSkewAngleByHough(CancellationToken token, Mat src, int cannyThresh1 = 50, int cannyThresh2 = 150, int houghThreshold = 80, int minLineLength = 100, int maxLineGap = 20)
        {
            using var gray = new Mat();

            token.ThrowIfCancellationRequested();
            if (src.Channels() == 3)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else
                src.CopyTo(gray);

            token.ThrowIfCancellationRequested();
            Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);

            var edges = new Mat();
            Cv2.Canny(gray, edges, cannyThresh1, cannyThresh2);

            token.ThrowIfCancellationRequested();
            LineSegmentPoint[] lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180.0, houghThreshold, minLineLength, maxLineGap);
            if (lines == null || lines.Length == 0) return double.NaN;

            var angles = lines.Select(l =>
            {
                double dx = l.P2.X - l.P1.X;
                double dy = l.P2.Y - l.P1.Y;
                double ang = Math.Atan2(dy, dx) * 180.0 / Math.PI; // -180..180
                if (ang > 90) ang -= 180;
                if (ang <= -90) ang += 180;
                return new { ang, dx, dy };
            }).ToArray();

            // сравнение по квадрату длины (избегаем Math.Sqrt для скорости)
            double minLenSq = (minLineLength * 0.5) * (minLineLength * 0.5);
            var longAngles = angles
                .Where(x => (x.dx * x.dx + x.dy * x.dy) >= minLenSq)
                .Select(x => x.ang)
                .ToArray();

            var useAngles = longAngles.Length > 0 ? longAngles : angles.Select(x => x.ang).ToArray();
            if (useAngles.Length == 0) return double.NaN;

            token.ThrowIfCancellationRequested();
            Array.Sort(useAngles);
            double median = useAngles[useAngles.Length / 2];
            return median;
        }


        private static double GetSkewAngleByProjection(CancellationToken token, Mat src, double minAngle = -15, double maxAngle = 15, double coarseStep = 1.0, double refineStep = 0.2)
        {
            // метод проекций: для каждого угла вычисляем variance / entropy горизонтального проекционного профиля,
            // выберем угол с наибольшей "пиковостью" (max variance)
            using var mask = BinarizeToMask(src);
            if (mask.Empty()) return double.NaN;

            double best = double.NaN;
            double bestScore = double.MinValue;

            for (double a = minAngle; a <= maxAngle; a += coarseStep)
            {
                token.ThrowIfCancellationRequested();
                double score = ProjectionScore(token, mask, a);
                if (score > bestScore) { bestScore = score; best = a; }
            }

            // refine вокруг best
            double start = Math.Max(minAngle, best - coarseStep);
            double end = Math.Min(maxAngle, best + coarseStep);
            for (double a = start; a <= end; a += refineStep)
            {
                token.ThrowIfCancellationRequested();
                double score = ProjectionScore(token, mask, a);
                if (score > bestScore) { bestScore = score; best = a; }
            }

            return best;
        }

        private static double ProjectionScore(CancellationToken token, Mat mask, double angle)
        {
            double rotation = -angle;
            double rad = rotation * Math.PI / 180.0;
            double absCos = Math.Abs(Math.Cos(rad));
            double absSin = Math.Abs(Math.Sin(rad));
            int newW = (int)Math.Round(mask.Width * absCos + mask.Height * absSin);
            int newH = (int)Math.Round(mask.Width * absSin + mask.Height * absCos);

            using var big = new Mat(new OpenCvSharp.Size(newW, newH), MatType.CV_8UC1, Scalar.All(0));
            int offX = (newW - mask.Width) / 2;
            int offY = (newH - mask.Height) / 2;
            var roi = new Rect(offX, offY, mask.Width, mask.Height);
            mask.CopyTo(new Mat(big, roi));

            var M = Cv2.GetRotationMatrix2D(new Point2f(newW / 2f, newH / 2f), rotation, 1.0);
            using var rotated = new Mat();
            Cv2.WarpAffine(big, rotated, M, new OpenCvSharp.Size(newW, newH), InterpolationFlags.Nearest, BorderTypes.Constant, Scalar.All(0));

            // горизонтальная проекция: для каждой строки считаем количество ненулевых пикселей
            var proj = new int[rotated.Rows];
            for (int y = 0; y < rotated.Rows; y++)
            {
                token.ThrowIfCancellationRequested();
                using var row = rotated.Row(y);               // временный SubMat
                proj[y] = Cv2.CountNonZero(row);             // native, быстро
            }

            double mean = proj.Average();
            double var = proj.Select(v => (v - mean) * (v - mean)).Average();
            return var;
        }


        private static double GetSkewAngleByPCA(CancellationToken token, Mat src)
        {
            using var mask = BinarizeToMask(src);
            if (mask.Empty()) return double.NaN;

            using var nonZeroMat = new Mat();
            Cv2.FindNonZero(mask, nonZeroMat); // <-- правильно: void, записывает результат в nonZeroMat
            if (nonZeroMat.Empty()) return double.NaN;

            // Конвертируем nonZeroMat -> Point[]
            OpenCvSharp.Point[] nzPoints;
            int rows = nonZeroMat.Rows;
            nzPoints = new OpenCvSharp.Point[rows];
            for (int i = 0; i < rows; i++)
            {
                token.ThrowIfCancellationRequested();
                // Попробуем считать как Vec2i (int,int). В зависимости от версии тип может называться по-разному,
                // но в большинстве сборок At<Vec2i> работает.
                var v = nonZeroMat.At<Vec2i>(i, 0);
                nzPoints[i] = new OpenCvSharp.Point(v.Item0, v.Item1);
            }

            if (nzPoints == null || nzPoints.Length < 50) // слишком мало точек — ненадёжно
                return double.NaN;

            int N = nzPoints.Length;
            using var data = new Mat(N, 2, MatType.CV_32F);

            // Заполняем матрицу (x,y) в float
            for (int i = 0; i < N; i++)
            {
                token.ThrowIfCancellationRequested();
                data.Set(i, 0, (float)nzPoints[i].X);
                data.Set(i, 1, (float)nzPoints[i].Y);
            }

            using var mean = new Mat();
            using var eigenvectors = new Mat();
            Cv2.PCACompute(data, mean, eigenvectors, maxComponents: 1);

            // eigenvectors: 1x2 (principal component)
            float vx = eigenvectors.At<float>(0, 0);
            float vy = eigenvectors.At<float>(0, 1);

            double angle = Math.Atan2(vy, vx) * 180.0 / Math.PI; // угол в градусах
                                                                 // нормализуем в -90..90
            if (angle > 90) angle -= 180;
            if (angle <= -90) angle += 180;
            return angle;
        }

        private static int GetBorderColor(CancellationToken token, Mat src)
        {
            if (src == null || src.Empty())
                return 0xFFFFFF; // default white

            // Ensure BGR (we'll work with a copy to be safe)
            using var img = EnsureBgr(src);
            if (img == null || img.Empty()) return 0xFFFFFF;

            int h = img.Rows, w = img.Cols;
            if (h == 0 || w == 0) return 0xFFFFFF;

            // border thickness as percent of smaller side (3%)
            int thickness = Math.Max(1, (int)Math.Round(Math.Min(h, w) * 0.01));
            const int maxSamples = 12000;

            // estimate border pixels and choose step to limit samples
            long approxBorderPixels = 2L * thickness * w + 2L * thickness * h;
            int step = 1;
            if (approxBorderPixels > maxSamples)
            {
                step = Math.Max(1, (int)Math.Ceiling(Math.Sqrt((double)approxBorderPixels / maxSamples)));
            }

            // collect B,G,R floats sequentially
            var samples = new List<float>();
            for (int y = 0; y < h; y += step)
            {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < w; x += step)
                {
                    if (x < thickness || x >= w - thickness || y < thickness || y >= h - thickness)
                    {
                        var v = img.At<Vec3b>(y, x);
                        samples.Add(v.Item0); // B
                        samples.Add(v.Item1); // G
                        samples.Add(v.Item2); // R
                        if (samples.Count / 3 >= maxSamples) goto SamplesCollected;
                    }
                }
            }
        SamplesCollected:
            int n = samples.Count / 3;
            if (n == 0) return 0xFFFFFF;

            // compute per-channel mean & stddev
            double sumB = 0, sumG = 0, sumR = 0;
            for (int i = 0; i < n; i++)
            {
                token.ThrowIfCancellationRequested();
                sumB += samples[i * 3 + 0];
                sumG += samples[i * 3 + 1];
                sumR += samples[i * 3 + 2];
            }
            double meanB = sumB / n, meanG = sumG / n, meanR = sumR / n;

            double varB = 0, varG = 0, varR = 0;
            for (int i = 0; i < n; i++)
            {
                token.ThrowIfCancellationRequested();
                double b = samples[i * 3 + 0] - meanB;
                double g = samples[i * 3 + 1] - meanG;
                double r = samples[i * 3 + 2] - meanR;
                varB += b * b;
                varG += g * g;
                varR += r * r;
            }
            double stdB = Math.Sqrt(varB / n);
            double stdG = Math.Sqrt(varG / n);
            double stdR = Math.Sqrt(varR / n);
            double avgStd = (stdB + stdG + stdR) / 3.0;

            // If border fairly uniform or too few samples -> return mean
            const double uniformThreshold = 10.0; // tweakable
            if (avgStd < uniformThreshold || n < 50)
            {
                int r = ClampToByte((int)Math.Round(meanR));
                int g = ClampToByte((int)Math.Round(meanG));
                int b = ClampToByte((int)Math.Round(meanB));
                return PackRgb(r, g, b);
            }

            // Otherwise use KMeans (k=2) to find dominant border color
            using (var samplesMat = new Mat(n, 3, MatType.CV_32F))
            using (var labels = new Mat())
            using (var centers = new Mat())
            {
                for (int i = 0; i < n; i++)
                {
                    samplesMat.Set(i, 0, samples[i * 3 + 0]);
                    samplesMat.Set(i, 1, samples[i * 3 + 1]);
                    samplesMat.Set(i, 2, samples[i * 3 + 2]);
                }

                var criteria = TermCriteria.Both(10, 1.0);
                //var criteria = new TermCriteria(OpenCvSharp.CriteriaType.Eps | OpenCvSharp.CriteriaType.MaxIter, 10, 1.0);
                Cv2.Kmeans(samplesMat, 2, labels, criteria, attempts: 3, flags: KMeansFlags.RandomCenters, centers: centers);

                // count labels
                int[] counts = new int[2];
                for (int i = 0; i < labels.Rows; i++)
                {
                    int lbl = labels.At<int>(i, 0);
                    if (lbl >= 0 && lbl < counts.Length) counts[lbl]++;
                }

                int bestIdx = counts[0] >= counts[1] ? 0 : 1;
                float centerB = centers.At<float>(bestIdx, 0);
                float centerG = centers.At<float>(bestIdx, 1);
                float centerR = centers.At<float>(bestIdx, 2);

                int rb = ClampToByte((int)Math.Round(centerR));
                int gb = ClampToByte((int)Math.Round(centerG));
                int bb = ClampToByte((int)Math.Round(centerB));
                return PackRgb(rb, gb, bb);
            }

            // local helpers
            static int ClampToByte(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);
            static int PackRgb(int r, int g, int b) => (r << 16) | (g << 8) | b;
        }

    }
}
