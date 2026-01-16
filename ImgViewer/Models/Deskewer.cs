using OpenCvSharp;
using Point = OpenCvSharp.Point;

using System.Diagnostics;
using System.IO;
using System.Linq;

namespace ImgViewer.Models;


public static class Deskewer
{
    public enum DeskewMethod
    {
        Auto,
        ByBorders,
        Hough,
        Projection,
        PCA,
        Moments,
        Perspective
    }
    private static double GetSkewAngleByBorders(CancellationToken token,
                                                Mat src,
                                                out double confidence,
                                                int cannyThresh1 = 50,
                                                int cannyThresh2 = 150,
                                                int morphKernel = 5,
                                                double minAreaFraction = 0.2)
    {
        confidence = 0.0;
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
        double areaFrac = maxArea / imageArea;

        if (areaFrac < minAreaFraction)
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

        double rectLike = (approx.Length == 4) ? 1.0 : 0.7;

        // чем ближе areaFrac к 0.55, тем увереннее (под документы)
        double lo = minAreaFraction;         // обычно 0.2
        double hi = Math.Max(lo + 0.05, 0.55);
        confidence = Clamp01((areaFrac - lo) / (hi - lo)) * rectLike;

        return angle;
    }

    public struct Parameters
    {
        public DeskewMethod Method { get; set; }
        public bool byBorders { get; set; }
        public int cTresh1 { get; set; }
        public int cTresh2 { get; set; }
        public int morphKernel { get; set; }
        public int perspectiveStrength { get; set; }

        public int houghTreshold { get; set; }
        public int minLineLength { get; set; }
        public int maxLineGap { get; set; }
        public double projMinAngle { get; set; }
        public double projMaxAngle { get; set; }
        public double projCoarseStep { get; set; }
        public double projRefineStep { get; set; }

    }


    public static Mat Deskew(CancellationToken token, DeskewMethod method, Mat orig, int cTresh1, int cTresh2, int morphK, int minLL, int houghTresh, int maxLineGap, double projMinAngle, double projMaxAngle, double projCoarseStep, double projRefineStep, int perspectiveStrength)
    {
        if (orig == null || orig.Empty()) return orig;

        // работаем с копией
        var src0 = orig; // do not dispose caller-owned Mat
                               // приводим src к CV_8UC3 (BGR) — это безопаснее для дальнейшей обработки/копирования
        using var src = EnsureBgr(src0);

        using var signMask = BinarizeToMask(src); // один раз на весь Deskew
        bool canFixSign = signMask != null && !signMask.Empty();

        double finalAngle = double.NaN;
        if (method == DeskewMethod.ByBorders)
        {
            double borderRawAngle = GetSkewAngleByBorders(token, src,
                out double confidence,
                cannyThresh1: cTresh1,
                cannyThresh2: cTresh2,
                morphKernel: morphK,
                minAreaFraction: 0.2);
            double borderAngle = NormalizeAngle(borderRawAngle);
            if (canFixSign) borderAngle = FixSignByProjection(token, signMask, borderAngle);
            borderAngle = NormalizeAngle(borderAngle);
            Debug.WriteLine($"Deskew: angle by Borders = {borderAngle:F3}");

            finalAngle = borderAngle;
            if (double.IsNaN(borderAngle))
            {
                return src.Clone();
            }
        }
        else if (method == DeskewMethod.Hough)
        {
            double houghRawAngle = GetSkewAngleByHough(token,
                                                        src,
                                                        out double confidence,
                                                        cannyThresh1: cTresh1,
                                                        cannyThresh2: cTresh2,
                                                        houghThreshold: houghTresh,
                                                        minLineLength: minLL,
                                                        maxLineGap: maxLineGap);
            double houghAngle = NormalizeAngle(houghRawAngle);
            if (canFixSign) houghAngle = FixSignByProjection(token, signMask, houghAngle);
            houghAngle = NormalizeAngle(houghAngle);
            Debug.WriteLine($"Deskew: angle by Hough = {houghAngle:F3}");
            finalAngle = houghAngle;
            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
                return src.Clone();
        }
        else if (method == DeskewMethod.Projection)
        {
            double projRawAngle = GetSkewAngleByProjection(token, src, out double confidence, minAngle: projMinAngle, maxAngle: projMaxAngle, coarseStep: projCoarseStep, refineStep: projRefineStep);
            double projAngle = NormalizeAngle(projRawAngle);
            if (canFixSign) projAngle = FixSignByProjection(token, signMask, projAngle);
            projAngle = NormalizeAngle(projAngle);
            Debug.WriteLine($"Deskew: angle by Projection = {projAngle:F3}");
            finalAngle = projAngle;
            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
                return src.Clone();
        }
        else if (method == DeskewMethod.PCA)
        {
            double pcaRawAngle = GetSkewAngleByPCA(token, src, out double confidence);
            double pcaAngle = NormalizeAngle(pcaRawAngle);
            if (canFixSign) pcaAngle = FixSignByProjection(token, signMask, pcaAngle);
            pcaAngle = NormalizeAngle(pcaAngle);
            Debug.WriteLine($"Deskew: angle by PCA = {pcaAngle:F3}");
            finalAngle = pcaAngle;
            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
                return src.Clone();
        }
        else if (method == DeskewMethod.Moments)
        {
            double momentsRawAngle = GetSkewAngleByMoments(token, signMask, out double confidence);
            double momentsAngle = NormalizeAngle(momentsRawAngle);
            if (canFixSign) momentsAngle = FixSignByProjection(token, signMask, momentsAngle);
            momentsAngle = NormalizeAngle(momentsAngle);
            Debug.WriteLine($"Deskew: angle by Moments = {momentsAngle:F3}");
            finalAngle = momentsAngle;
            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
                return src.Clone();
        }
        else if (method == DeskewMethod.Perspective)
        {
            var warped = TryPerspectiveCorrect(token, src, morphK, perspectiveStrength);
            return warped ?? src.Clone();
        }
else
        {

            var cands = new List<AngleCandidate>(5);
            // 1) candidate angles
            double houghRawAngle = GetSkewAngleByHough(token, src,
                                                        out double houghConfidence, 
                                                        cannyThresh1: cTresh1,
                                                        cannyThresh2: cTresh2,
                                                        houghThreshold: houghTresh,
                                                        minLineLength: minLL,
                                                        maxLineGap: maxLineGap);
            
            double houghAngle = NormalizeAngle(houghRawAngle);
            if (canFixSign) houghAngle = FixSignByProjection(token, signMask, houghAngle);
            houghAngle = NormalizeAngle(houghAngle);
            Debug.WriteLine($"Deskew: angle by Hough = {houghAngle:F3}");
            TryAddCandidate(cands, DeskewMethod.Hough, houghAngle, houghConfidence);

            double projRawAngle = GetSkewAngleByProjection(token, src,
                                                        out double projConfidence,
                                                        minAngle: projMinAngle,
                                                        maxAngle: projMaxAngle,
                                                        coarseStep: projCoarseStep,
                                                        refineStep: projRefineStep);
            double projAngle = NormalizeAngle(projRawAngle);
            if (canFixSign) projAngle = FixSignByProjection(token, signMask, projAngle);
            projAngle = NormalizeAngle(projAngle);
            Debug.WriteLine($"Deskew: angle by Projection = {projAngle:F3}");
            TryAddCandidate(cands, DeskewMethod.Projection, projAngle, projConfidence);


            double pcaRawAngle = GetSkewAngleByPCA(token, src, out double pcaConfidence);
            double pcaAngle = NormalizeAngle(pcaRawAngle);
            if (canFixSign) pcaAngle = FixSignByProjection(token, signMask, pcaAngle);
            pcaAngle = NormalizeAngle(pcaAngle);
            Debug.WriteLine($"Deskew: angle by PCA = {pcaAngle:F3}");
            //TryAddCandidate(cands, DeskewMethod.PCA, pcaAngle, pcaConfidence);

            double momentsRawAngle = GetSkewAngleByMoments(token, signMask, out double momentsConfidence);
            double momentsAngle = NormalizeAngle(momentsRawAngle);
            if (canFixSign) momentsAngle = FixSignByProjection(token, signMask, momentsAngle);
            momentsAngle = NormalizeAngle(momentsAngle);
            Debug.WriteLine($"Deskew: angle by Moments = {momentsAngle:F3}");
            //TryAddCandidate(cands, DeskewMethod.Moments, momentsAngle, momentsConfidence);

            double borderRawAngle = GetSkewAngleByBorders(token, src,
                    out double bordersConfidence,
                    cannyThresh1: cTresh1,
                    cannyThresh2: cTresh2,
                    morphKernel: morphK,
                    minAreaFraction: 0.2);

            double borderAngle = NormalizeAngle(borderRawAngle);
            if (canFixSign) borderAngle = FixSignByProjection(token, signMask, borderAngle);
            borderAngle = NormalizeAngle(borderAngle);

            Debug.WriteLine($"Deskew: angle by Borders = {borderAngle:F3}");
            //TryAddCandidate(cands, DeskewMethod.ByBorders, borderAngle, bordersConfidence);

            //double finalAngle = double.NaN;
            //if (!double.IsNaN(houghAngle)) finalAngle = houghAngle;
            //if (double.IsNaN(finalAngle) || (finalAngle == 0 && !double.IsNaN(pcaAngle))) finalAngle = pcaAngle;
            //if (double.IsNaN(finalAngle) && !double.IsNaN(momentsAngle)) finalAngle = momentsAngle;
            //if (double.IsNaN(finalAngle)) finalAngle = projAngle;


            // 3) голосование (consensus)
            finalAngle = PickFinalAngleByConsensus(cands, tolDeg: 1.5, out string consensusDbg);
            Debug.WriteLine(consensusDbg);

            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
            {
                Debug.WriteLine($"Deskew: angle is zero or NaN ({finalAngle}), skipping rotation.");
return src.Clone();
            }

            Debug.WriteLine($"Deskew: angle selected = {finalAngle:F3}");


            if (double.IsNaN(finalAngle) || Math.Abs(finalAngle) < 0.005)
            {
                Debug.WriteLine($"Deskew: angle is zero or NaN ({finalAngle}), skipping rotation.");
return src.Clone(); // возвращаем копию BGR
            }
            else
            {
                Debug.WriteLine($"Deskew: angle selected = {finalAngle:F3}");
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

        //bgScalar = Scalar.All(0);

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
        var srcRect = new Rect(offX, offY, src.Width, src.Height);
        src.CopyTo(new Mat(big, srcRect));
        FeatherEdges(big, srcRect, bgScalar, featherPx: 6);

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

        //using var nonZeroMat = new Mat();
        //Cv2.FindNonZero(mask, nonZeroMat);
        //if (nonZeroMat.Empty())
        //{
        //    return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, centerBig);
        //}

        //конвертируем в Point[]
        //Point[] nzPoints;
        //int rows = nonZeroMat.Rows;
        //nzPoints = new Point[rows];
        //for (int i = 0; i < rows; i++)
        //{
        //    var v = nonZeroMat.At<Vec2i>(i, 0);
        //    nzPoints[i] = new Point(v.Item0, v.Item1);
        //}

        //if (nzPoints == null || nzPoints.Length == 0)
        //{
        //    return CropOrPadToOriginal(rotatedBig, orig.Width, orig.Height, centerBig);
        //}

        // bounding rect должен принимть Point[] — так безопаснее
        //var contentRect = Cv2.BoundingRect(nonZeroMat);
        //var contentCenter = new Point2f(contentRect.X + contentRect.Width / 2f, contentRect.Y + contentRect.Height / 2f);

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
    private static Mat? TryPerspectiveCorrect(CancellationToken token, Mat src, int morphKernel, int strength)
    {
        if (src == null || src.Empty()) return null;

        token.ThrowIfCancellationRequested();
        using var gray = new Mat();
        if (src.Channels() == 3)
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else if (src.Channels() == 4)
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
        else
            src.CopyTo(gray);

        int k = Math.Max(3, morphKernel);
        if (k % 2 == 0) k += 1;
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(k, k));

        // Edge-based contour is more reliable for keystone distortions.
        using var edges = new Mat();
        Cv2.GaussianBlur(gray, gray, new OpenCvSharp.Size(3, 3), 0);
        int s = Math.Max(0, Math.Min(10, strength));
        int cannyLow = Math.Max(10, 80 - s * 6);
        int cannyHigh = Math.Max(30, 200 - s * 10);
        Cv2.Canny(gray, edges, cannyLow, cannyHigh);
        int dilateIter = 1 + s / 4;
        Cv2.Dilate(edges, edges, kernel, iterations: dilateIter);
        Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel, iterations: 2);

        token.ThrowIfCancellationRequested();
        Cv2.FindContours(edges, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

        // Fallback to binarized mask if edges fail.
        if (contours == null || contours.Length == 0)
        {
            using var bin = new Mat();
            Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.BinaryInv);
            Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel, iterations: 2);
            Cv2.FindContours(bin, out contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours == null || contours.Length == 0) return null;
        }

        double maxArea = 0;
        int maxIdx = -1;
        for (int i = 0; i < contours.Length; i++)
        {
            double area = Cv2.ContourArea(contours[i]);
            if (area > maxArea)
            {
                maxArea = area;
                maxIdx = i;
            }
        }
        if (maxIdx < 0) return null;

        double imageArea = src.Width * (double)src.Height;
        double minAreaFrac = Math.Max(0.02, Math.Min(0.12, 0.12 - 0.01 * s));
        if (maxArea < imageArea * minAreaFrac)
            return null;

        var contour = contours[maxIdx];
        var hull = Cv2.ConvexHull(contour);
        var approx = Cv2.ApproxPolyDP(hull, Cv2.ArcLength(hull, true) * 0.02, true);

        Point2f[] quad;
        if (approx.Length == 4)
        {
            quad = approx.Select(p => new Point2f(p.X, p.Y)).ToArray();
        }
        else
        {
            var rr = Cv2.MinAreaRect(hull);
            quad = rr.Points();
        }

        var ordered = OrderQuad(quad);
        var (dstW, dstH) = EstimateQuadSize(ordered);
        if (dstW < 10 || dstH < 10) return null;

        var dstQuad = new[]
        {
            new Point2f(0, 0),
            new Point2f(dstW - 1, 0),
            new Point2f(dstW - 1, dstH - 1),
            new Point2f(0, dstH - 1)
        };

        using var M = Cv2.GetPerspectiveTransform(ordered, dstQuad);
        var dst = new Mat();
        Cv2.WarpPerspective(src, dst, M, new OpenCvSharp.Size(dstW, dstH), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));
        return dst;
    }


    private static Point2f[] OrderQuad(Point2f[] pts)
    {
        if (pts.Length != 4)
            throw new ArgumentException("Expected 4 points.", nameof(pts));

        var ordered = new Point2f[4];
        var sums = pts.Select(p => p.X + p.Y).ToArray();
        var diffs = pts.Select(p => p.X - p.Y).ToArray();

        int tl = Array.IndexOf(sums, sums.Min());
        int br = Array.IndexOf(sums, sums.Max());
        int tr = Array.IndexOf(diffs, diffs.Max());
        int bl = Array.IndexOf(diffs, diffs.Min());

        ordered[0] = pts[tl];
        ordered[1] = pts[tr];
        ordered[2] = pts[br];
        ordered[3] = pts[bl];
        return ordered;
    }

    private static (int width, int height) EstimateQuadSize(Point2f[] quad)
    {
        double w1 = Distance(quad[0], quad[1]);
        double w2 = Distance(quad[3], quad[2]);
        double h1 = Distance(quad[0], quad[3]);
        double h2 = Distance(quad[1], quad[2]);

        int width = (int)Math.Round(Math.Max(w1, w2));
        int height = (int)Math.Round(Math.Max(h1, h2));
        return (width, height);
    }

    private static double Distance(Point2f a, Point2f b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double GetSkewAngleByMoments(CancellationToken token,
                                                Mat mask,
                                                out double confidence)
    {
        confidence = 0.0;
        token.ThrowIfCancellationRequested();

        // binaryImage:true => OpenCV считает non-zero как 1 (не важно 1 или 255)
        var m = Cv2.Moments(mask, binaryImage: true);

        // если слишком мало пикселей (маска пустая) — не доверяем углу
        if (m.M00 < 50) return double.NaN;

        // orientation of second central moments:
        // theta = 0.5 * atan2(2*mu11, mu20 - mu02)
        double mu11 = m.Mu11;
        double mu20 = m.Mu20;
        double mu02 = m.Mu02;

        double angleRad = 0.5 * Math.Atan2(2.0 * mu11, (mu20 - mu02));
        double angle = angleRad * 180.0 / Math.PI;

        double strength = Math.Abs(mu20 - mu02) / (mu20 + mu02 + 1e-9); // 0..1
        double cStrength = (strength - 0.05) / 0.25;                    // ниже 0.05 — шум
        double cPixels = (m.M00 - 200) / 5000.0;                      // “сколько данных”

        confidence = Clamp01(cStrength) * Clamp01(cPixels);


        // normalize to [-90, 90)
        if (angle > 90) angle -= 180;
        if (angle <= -90) angle += 180;


        return angle;
    }


    private static Mat BinarizeToMask(Mat src)
    {
        // возвращаем CV_8UC1 маску (0/255)
        var gray = new Mat();
        if (src.Channels() == 3) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else if (src.Channels() == 4) Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
        else gray = src.Clone(); // 1 канал

        var bin = new Mat();
        Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.BinaryInv);

        var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel, iterations: 1);

        // gray может быть удалён вызывающим кодом, вернём bin
        return bin;
    }

    private static void FeatherEdges(Mat img, Rect roi, Scalar bg, int featherPx)
    {
        if (featherPx <= 0)
            return;
        if (img.Type() != MatType.CV_8UC3)
            return;

        int x0 = roi.X;
        int y0 = roi.Y;
        int x1 = roi.X + roi.Width - 1;
        int y1 = roi.Y + roi.Height - 1;
        if (x0 < 0 || y0 < 0 || x1 >= img.Cols || y1 >= img.Rows)
            return;

        byte bb = (byte)ClampByte((int)Math.Round(bg.Val0));
        byte bgG = (byte)ClampByte((int)Math.Round(bg.Val1));
        byte br = (byte)ClampByte((int)Math.Round(bg.Val2));

        var idx = img.GetGenericIndexer<Vec3b>();
        for (int y = y0; y <= y1; y++)
        {
            int dy = Math.Min(y - y0, y1 - y);
            for (int x = x0; x <= x1; x++)
            {
                int dx = Math.Min(x - x0, x1 - x);
                int d = Math.Min(dx, dy);
                if (d >= featherPx)
                    continue;

                double a = (d + 1.0) / (featherPx + 1.0);
                var v = idx[y, x];
                v.Item0 = (byte)ClampByte((int)Math.Round(v.Item0 * a + bb * (1.0 - a)));
                v.Item1 = (byte)ClampByte((int)Math.Round(v.Item1 * a + bgG * (1.0 - a)));
                v.Item2 = (byte)ClampByte((int)Math.Round(v.Item2 * a + br * (1.0 - a)));
                idx[y, x] = v;
            }
        }
    }

    private static int ClampByte(int v) => v < 0 ? 0 : (v > 255 ? 255 : v);

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

    private static double GetSkewAngleByHough(CancellationToken token,
                                                Mat src,
                                                out double confidence,
                                                int cannyThresh1 = 50,
                                                int cannyThresh2 = 150,
                                                int houghThreshold = 80,
                                                int minLineLength = 100,
                                                int maxLineGap = 20)
    {
        confidence = 0.0;
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

        int n = useAngles.Length;
        double devSum = 0;
        for (int k = 0; k < n; k++)
            devSum += Math.Abs(useAngles[k] - median);

        double mad = devSum / n; // “разброс” в градусах (чем меньше, тем лучше)

        double cCount = Math.Log(1 + n) / Math.Log(1 + 30); // насыщается к ~30
        double cSpread = (3.0 - mad) / 3.0;                 // mad<=3° хорошо

        confidence = Clamp01(cCount) * Clamp01(cSpread);


        return median;
    }


    private static double GetSkewAngleByProjection(CancellationToken token,
                                                    Mat src,
                                                    out double confidence,
                                                    double minAngle = -15,
                                                    double maxAngle = 15,
                                                    double coarseStep = 1.0,
                                                    double refineStep = 0.2)
    {
        confidence = 0.0;
        // метод проекций: для каждого угла вычисляем variance / entropy горизонтального проекционного профиля,
        // выберем угол с наибольшей "пиковостью" (max variance)
        using var mask = BinarizeToMask(src);
        if (mask.Empty()) return double.NaN;

        double best = double.NaN;
        double bestScore = double.MinValue;
        double secondBestScore = double.MinValue;

        for (double a = minAngle; a <= maxAngle; a += coarseStep)
        {
            token.ThrowIfCancellationRequested();
            double score = ProjectionScore(token, mask, a);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                best = a;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        // refine вокруг best
        double start = Math.Max(minAngle, best - coarseStep);
        double end = Math.Min(maxAngle, best + coarseStep);
        for (double a = start; a <= end; a += refineStep)
        {
            token.ThrowIfCancellationRequested();
            double score = ProjectionScore(token, mask, a);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                best = a;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        if (secondBestScore > double.MinValue / 2)
        {
            double peak = (bestScore - secondBestScore) / (Math.Abs(bestScore) + 1e-9);
            confidence = Clamp01(peak / 0.25); // 0.25 = “достаточно острый пик”
        }
        else
        {
            confidence = 0.15; // fallback, если второго кандидата почти не было
        }


        return best;
    }

    private static double ProjectionScore(CancellationToken token, Mat mask, double angle)
    {
        double rotation = angle;
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


    private static double GetSkewAngleByPCA(CancellationToken token, Mat src, out double confidence)
    {
        confidence = 0.0;
        using var mask = BinarizeToMask(src);
        if (mask.Empty()) return double.NaN;

        using var nonZeroMat = new Mat();
        Cv2.FindNonZero(mask, nonZeroMat);
        if (nonZeroMat.Empty()) return double.NaN;

        token.ThrowIfCancellationRequested();

        // nonZeroMat is Nx1 with 2 channels (x,y) int32
        int n = nonZeroMat.Rows;
        if (n < 50) return double.NaN;

        // reshape to Nx2, 1-channel (still int32)
        using var data32s = nonZeroMat.Reshape(1, n); // Nx2, CV_32SC1

        // convert to float in one native call
        using var data32f = new Mat();
        data32s.ConvertTo(data32f, MatType.CV_32F);

        using var mean = new Mat();
        using var eigenvectors = new Mat();
        Cv2.PCACompute(data32f, mean, eigenvectors, maxComponents: 1);

        // eigenvectors: 1x2
        float vx = eigenvectors.At<float>(0, 0);
        float vy = eigenvectors.At<float>(0, 1);

        double angle = Math.Atan2(vy, vx) * 180.0 / Math.PI;
        //angle = -angle;
        if (angle > 90) angle -= 180;
        if (angle <= -90) angle += 180;

        double cN = Math.Log(1 + n) / Math.Log(1 + 20000);   // насыщение по количеству точек
        double cA = (25.0 - Math.Abs(angle)) / 25.0;         // большие углы реже, чуть штрафуем

        confidence = 0.35 * Clamp01(cN) * (0.5 + 0.5 * Clamp01(cA)); // PCA всегда “легкий вес”


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

    private static double NormalizeAngle(double angle, double zeroEps = 0.005)
    {
        // 1) отсекаем мусор
        if (double.IsNaN(angle) || double.IsInfinity(angle))
            return double.NaN;

        // 2) слишком маленький угол считаем "нет поворота"
        if (Math.Abs(angle) < zeroEps)
            return 0.0;

        // 3) нормализация в (-90..90]
        while (angle > 90) angle -= 180;
        while (angle <= -90) angle += 180;

        // 4) приводим к диапазону [-45..45], как у тебя уже сделано в конце
        if (angle > 45) angle -= 90;
        if (angle < -45) angle += 90;

        return angle;
    }

    private static double FixSignByProjection(CancellationToken token, Mat mask, double angle, double minAbsAngle = 0.3)
    {
        // Смысл: знак часто "плавает" у Hough/PCA/Moments/Borders.
        // Проверяем, какой знак даёт лучшее качество проекции.

        if (double.IsNaN(angle) || double.IsInfinity(angle))
            return double.NaN;

        double a = Math.Abs(angle);
        if (a < minAbsAngle) // чтобы не тратить время на микроскопические углы
            return angle;

        // Важно: ProjectionScore уже использует rotation = angle (мы исправили выше),
        // значит мы можем сравнивать "как есть".
        double sPlus = ProjectionScore(token, mask, +a);
        double sMinus = ProjectionScore(token, mask, -a);

        return (sMinus > sPlus) ? -a : +a;
    }

    private readonly struct AngleCandidate
    {
        public readonly DeskewMethod Method;
        public readonly double Angle;
        public readonly double Weight; // пока = 1.0, confidence добавим на следующем шаге

        public AngleCandidate(DeskewMethod method, double angle, double weight)
        {
            Method = method;
            Angle = angle;
            Weight = weight;
        }
    }

    private static int MethodPriority(DeskewMethod m) => m switch
    {
        DeskewMethod.ByBorders => 2,
        DeskewMethod.Hough => 1,
        DeskewMethod.Projection => 0,
        DeskewMethod.Moments => 99,
        DeskewMethod.PCA => 99,
        _ => 99
    };

    private static void TryAddCandidate(List<AngleCandidate> list, DeskewMethod method, double angle, double weight)
    {
        if (double.IsNaN(angle) || double.IsInfinity(angle))
            return;

        // нули можно либо оставлять, либо выкидывать.
        // я предлагаю выкидывать "почти ноль", чтобы не мешал голосованию:
        if (Math.Abs(angle) < 0.005)
            return;

        list.Add(new AngleCandidate(method, angle, weight));
    }

    /// <summary>
    /// Выбираем итоговый угол через "consensus":
    /// 1) сортируем по углу
    /// 2) группируем в кластеры, где соседние углы отличаются <= tolDeg
    /// 3) выбираем лучший кластер по:
    ///    - max support (сколько методов согласны)
    ///    - затем max суммарный Weight
    ///    - затем лучший priority (наличие более приоритетного метода)
    ///    - затем меньшая ширина кластера
    /// 4) итог угла = weighted mean внутри кластера
    /// </summary>
    private static double PickFinalAngleByConsensus(List<AngleCandidate> cands, double tolDeg, out string debug)
    {
        debug = "Deskew: consensus: no candidates";
        if (cands == null || cands.Count == 0)
            return double.NaN;

        // сортируем по углу
        cands.Sort((a, b) => a.Angle.CompareTo(b.Angle));

        int bestSupport = -1;
        double bestWeightSum = double.NegativeInfinity;
        int bestPriority = int.MaxValue;
        double bestWidth = double.PositiveInfinity;
        double bestAngle = double.NaN;

        int i = 0;
        while (i < cands.Count)
        {
            // начинаем кластер
            int j = i;
            int support = 0;
            double wSum = 0;
            double aSum = 0;

            double clusterMin = cands[i].Angle;
            double clusterMax = cands[i].Angle;

            int clusterBestPriority = int.MaxValue;

            // критерий кластера: соседние элементы отличаются <= tolDeg
            // (для отсортированного списка это надёжно)
            while (j < cands.Count)
            {
                if (j > i)
                {
                    double prev = cands[j - 1].Angle;
                    double cur = cands[j].Angle;
                    if (Math.Abs(cur - prev) > tolDeg)
                        break;
                }

                var c = cands[j];
                support++;

                double w = c.Weight <= 0 ? 1.0 : c.Weight;
                wSum += w;
                aSum += c.Angle * w;

                clusterMin = Math.Min(clusterMin, c.Angle);
                clusterMax = Math.Max(clusterMax, c.Angle);

                int p = MethodPriority(c.Method);
                if (p < clusterBestPriority) clusterBestPriority = p;

                j++;
            }

            double mean = (wSum > 0) ? (aSum / wSum) : double.NaN;
            double width = clusterMax - clusterMin;

            bool better =
                (support > bestSupport) ||
                (support == bestSupport && wSum > bestWeightSum) ||
                (support == bestSupport && Math.Abs(wSum - bestWeightSum) < 1e-9 && clusterBestPriority < bestPriority) ||
                (support == bestSupport && Math.Abs(wSum - bestWeightSum) < 1e-9 && clusterBestPriority == bestPriority && width < bestWidth);

            if (better)
            {
                bestSupport = support;
                bestWeightSum = wSum;
                bestPriority = clusterBestPriority;
                bestWidth = width;
                bestAngle = mean;
            }

            i = j;
        }

        debug = $"Deskew: consensus selected={bestAngle:F3} support={bestSupport} wSum={bestWeightSum:F2} tol={tolDeg:F2}";
        return bestAngle;
    }

    private static double Clamp01(double x) => x < 0 ? 0 : (x > 1 ? 1 : x);

    private static double Median(double[] arr)
    {
        if (arr == null || arr.Length == 0) return 0;
        var tmp = (double[])arr.Clone();
        Array.Sort(tmp);
        int mid = tmp.Length / 2;
        return (tmp.Length % 2 == 1) ? tmp[mid] : 0.5 * (tmp[mid - 1] + tmp[mid]);
    }


}
