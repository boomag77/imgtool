using OpenCvSharp;
using System;
using System.Linq;

public static class Cropper
{
    /// <summary>
    /// Автокроп, устойчивый для смешанного контента: печатный + рукописный.
    /// Возвращает cropped Mat (clone). Параметры можно тонко подбирать.
    /// </summary>
    public static Mat AutoCropMixedText(Mat orig,
        int downscaleMaxWidth = 1200,
        // параметры для "печатного" режима
        int printedBlockSize = 31, int printedC = 10,
        // параметры для "рукописного" режима
        int handBlur = 3, int handBlockSize = 51, int handC = 10, int handDilateIter = 2,
        // параметры для edges/lines
        int canny1 = 50, int canny2 = 150, int edgeDilateIter = 1,
        // фильтрация и margin
        double minComponentAreaFraction = 0.0003, int marginPx = 20)
    {
        if (orig == null || orig.Empty()) return orig;

        // 1) приведение к BGR и downscale для анализа
        Mat srcBgr = orig;
        bool needDisposeSrcBgr = false;
        if (orig.Channels() == 1)
        {
            srcBgr = new Mat();
            Cv2.CvtColor(orig, srcBgr, ColorConversionCodes.GRAY2BGR);
            needDisposeSrcBgr = true;
        }
        else if (orig.Channels() == 4)
        {
            srcBgr = new Mat();
            Cv2.CvtColor(orig, srcBgr, ColorConversionCodes.BGRA2BGR);
            needDisposeSrcBgr = true;
        }

        double scale = 1.0;
        Mat proc = srcBgr;
        Mat resized = null;
        if (downscaleMaxWidth > 0 && srcBgr.Width > downscaleMaxWidth)
        {
            scale = downscaleMaxWidth / (double)srcBgr.Width;
            resized = new Mat();
            Cv2.Resize(srcBgr, resized, new OpenCvSharp.Size((int)(srcBgr.Width * scale), (int)(srcBgr.Height * scale)), 0, 0, InterpolationFlags.Area);
            proc = resized;
        }

        // 2) gray
        using var gray = new Mat();
        Cv2.CvtColor(proc, gray, ColorConversionCodes.BGR2GRAY);

        // --- MASK 1: printed text (adaptive small block) ---
        using var maskPrinted = new Mat();
        Cv2.AdaptiveThreshold(gray, maskPrinted, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, printedBlockSize, printedC);
        var kernSmall = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        Cv2.MorphologyEx(maskPrinted, maskPrinted, MorphTypes.Close, kernSmall, iterations: 1);

        // --- MASK 2: handwriting (blur -> adaptive with bigger block -> dilate) ---
        using var blurHand = new Mat();
        Cv2.GaussianBlur(gray, blurHand, new OpenCvSharp.Size(handBlur, handBlur), 0);
        using var maskHand = new Mat();
        // больший blockSize и возможно другой C помогут связать штрихи
        Cv2.AdaptiveThreshold(blurHand, maskHand, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, handBlockSize, handC);
        var kernHand = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        // несколько итераций dilate, чтобы утолщать рукописные штрихи
        if (handDilateIter > 0) Cv2.Dilate(maskHand, maskHand, kernHand, iterations: handDilateIter);

        // --- MASK 3: edges/lines (Canny + close) ---
        using var edges = new Mat();
        Cv2.Canny(gray, edges, canny1, canny2);
        var kernEdge = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
        if (edgeDilateIter > 0) Cv2.Dilate(edges, edges, kernEdge, iterations: edgeDilateIter);

        // --- combine masks ---
        using var combined = new Mat();
        Cv2.BitwiseOr(maskPrinted, maskHand, combined);
        Cv2.BitwiseOr(combined, edges, combined);

        // небольшой close, чтобы объединить близкие компоненты
        var kernClose = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(5, 5));
        Cv2.MorphologyEx(combined, combined, MorphTypes.Close, kernClose, iterations: 1);

        // 3) connected components
        Mat labels = new Mat();
        Mat stats = new Mat();
        Mat centroids = new Mat();
        int nLabels = Cv2.ConnectedComponentsWithStats(combined, labels, stats, centroids);
        int imgArea = combined.Width * combined.Height;
        double minArea = Math.Max(1, imgArea * minComponentAreaFraction);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        bool any = false;
        for (int i = 1; i < nLabels; i++) // 0 - фон
        {
            int left = stats.At<int>(i, 0);
            int top = stats.At<int>(i, 1);
            int width = stats.At<int>(i, 2);
            int height = stats.At<int>(i, 3);
            int area = stats.At<int>(i, 4);

            if (area < minArea) continue;


            minX = Math.Min(minX, left);
            minY = Math.Min(minY, top);
            maxX = Math.Max(maxX, left + width);
            maxY = Math.Max(maxY, top + height);
            any = true;
        }

        // fallback: если ничего не найдено — попробуем FindContours и взять top contours
        if (!any)
        {
            Cv2.FindContours(combined, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours != null && contours.Length > 0)
            {
                var ordered = contours.OrderByDescending(c => Math.Abs(Cv2.ContourArea(c))).Take(5);
                foreach (var c in ordered)
                {
                    var r = Cv2.BoundingRect(c);
                    if (r.Width * r.Height < minArea) continue;
                    minX = Math.Min(minX, r.X);
                    minY = Math.Min(minY, r.Y);
                    maxX = Math.Max(maxX, r.X + r.Width);
                    maxY = Math.Max(maxY, r.Y + r.Height);
                    any = true;
                }
            }
        }

        // если всё ещё ничего — возвращаем оригинал
        if (!any)
        {
            labels.Dispose(); stats.Dispose(); centroids.Dispose();
            resized?.Dispose();
            if (needDisposeSrcBgr) srcBgr.Dispose();
            return orig.Clone();
        }

        // 4) expand bbox by margin (в координатах proc), затем трансформируем в оригинальные
        minX = Math.Max(0, minX - marginPx);
        minY = Math.Max(0, minY - marginPx);
        maxX = Math.Min(combined.Width, maxX + marginPx);
        maxY = Math.Min(combined.Height, maxY + marginPx);

        int origMinX = (int)Math.Round(minX / scale);
        int origMinY = (int)Math.Round(minY / scale);
        int origMaxX = (int)Math.Round(maxX / scale);
        int origMaxY = (int)Math.Round(maxY / scale);

        // margin в оригинале тоже небольшая дополнительная защита
        origMinX = Math.Max(0, origMinX - marginPx);
        origMinY = Math.Max(0, origMinY - marginPx);
        origMaxX = Math.Min(orig.Width, origMaxX + marginPx);
        origMaxY = Math.Min(orig.Height, origMaxY + marginPx);

        int newW = origMaxX - origMinX;
        int newH = origMaxY - origMinY;
        if (newW <= 0 || newH <= 0)
        {
            labels.Dispose(); stats.Dispose(); centroids.Dispose();
            resized?.Dispose();
            if (needDisposeSrcBgr) srcBgr.Dispose();
            return orig.Clone();
        }

        var roi = new Rect(origMinX, origMinY, newW, newH);
        Mat cropped = new Mat(orig, roi).Clone();

        // cleanup
        labels.Dispose(); stats.Dispose(); centroids.Dispose();
        resized?.Dispose();
        if (needDisposeSrcBgr) srcBgr.Dispose();

        return cropped;
    }
}
