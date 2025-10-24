// NuGet: OpenCvSharp4, OpenCvSharp4.runtime.win (или другой runtime), Tesseract

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using OpenCvSharp;
using OpenCvSharp.Dnn;
using Tesseract;

// Алиасы, чтобы не было ambiguous:
using OCRect = OpenCvSharp.Rect;
using OCMat = OpenCvSharp.Mat;
using OCSize = OpenCvSharp.Size;
using OCPoint = OpenCvSharp.Point;

public class TextAwareCropper
{
    private readonly string _eastModelPath;   // путь к frozen_east_text_detection.pb
    private readonly string _tessDataPath;    // папка tessdata с *.traineddata
    private readonly string _tessLang;        // "eng" или "rus+eng" и т.д.

    public TextAwareCropper(string eastModelPath, string tessDataPath, string tessLang = "eng")
    {
        _eastModelPath = eastModelPath;
        _tessDataPath = tessDataPath;
        _tessLang = tessLang;
    }

    private static List<OCRect> MergeAndInflateBoxes(OCMat proc, List<OCRect> boxes, int paddingPx = 12, double relInflate = 0.15, int dilateIterations = 2)
    {
        var outRects = new List<OCRect>();
        if (proc == null || proc.Empty() || boxes == null || boxes.Count == 0) return outRects;

        int W = proc.Width, H = proc.Height;
        using var mask = new Mat(new OCSize(W, H), MatType.CV_8UC1, Scalar.All(0));

        // нарисовать каждый бокс расширенным на max(paddingPx, relInflate*maxSide)
        foreach (var b in boxes)
        {
            int mw = Math.Max(0, b.Width);
            int mh = Math.Max(0, b.Height);
            int extra = (int)Math.Round(Math.Max(paddingPx, Math.Max(mw, mh) * relInflate));
            int x = Math.Max(0, b.X - extra);
            int y = Math.Max(0, b.Y - extra);
            int w = Math.Min(W - x, b.Width + extra * 2);
            int h = Math.Min(H - y, b.Height + extra * 2);
            if (w <= 0 || h <= 0) continue;
            Cv2.Rectangle(mask, new OCRect(x, y, w, h), Scalar.All(255), thickness: -1);
        }

        // dilate чтобы сцепить близкие bbox'ы (ядро зависит от размера изображения)
        int kSize = Math.Max(3, Math.Min(Math.Max(W, H) / 200, 31)); // ~ proportional
        var ker = Cv2.GetStructuringElement(MorphShapes.Rect, new OCSize(kSize, kSize));
        Cv2.Dilate(mask, mask, ker, iterations: dilateIterations);

        // find contours on mask
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours == null || contours.Length == 0) return outRects;

        foreach (var c in contours)
        {
            var r = Cv2.BoundingRect(c);
            // optional: filter tiny ones (если надо)
            if (r.Width <= 2 || r.Height <= 2) continue;
            outRects.Add(r);
        }

        return outRects;
    }

    public OCMat CropKeepingText(OCMat orig,
        int eastInputWidth = 704, int eastInputHeight = 704,
        float eastScoreThreshold = 0.5f,
        float eastNmsThreshold = 0.4f,
        int tesseractMinConfidence = 5, // 0..100
        int paddingPx = 20,
        int downscaleMaxWidth = 1600)
    {
        if (orig == null || orig.Empty()) return orig;

        // 0) downscale for speed (remember scale)
        double scale = 1.0;
        OCMat proc = orig;
        OCMat resized = null;
        if (downscaleMaxWidth > 0 && orig.Width > downscaleMaxWidth)
        {
            scale = downscaleMaxWidth / (double)orig.Width;
            resized = new OCMat();
            Cv2.Resize(orig, resized, new OCSize((int)(orig.Width * scale), (int)(orig.Height * scale)), 0, 0, InterpolationFlags.Area);
            proc = resized;
        }

        // 1) run EAST detector
        using var net = CvDnn.ReadNet(_eastModelPath);
        net.SetPreferableBackend(Backend.OPENCV);
        net.SetPreferableTarget(Target.CPU);

        using var blob = CvDnn.BlobFromImage(proc, 1.0, new OCSize(eastInputWidth, eastInputHeight),
                                             new Scalar(123.68, 116.78, 103.94), swapRB: true, crop: false);
        net.SetInput(blob);

        // EAST output layers
        const string kScores = "feature_fusion/Conv_7/Sigmoid";
        const string kGeom = "feature_fusion/concat_3";

        // вместо: var outs = net.Forward(outputNames);
        using var scores = net.Forward(kScores);
        using var geometry = net.Forward(kGeom);

        // дальше как было:
        var rects = DecodeEAST(scores, geometry,
                               eastScoreThreshold, eastInputWidth, eastInputHeight,
                               proc.Width, proc.Height);

        rects = NonMaxSuppression(rects, eastNmsThreshold);



        // 2) Initialize Tesseract engine (OCR только по кандидатам)
        var confirmedBoxes = new List<OCRect>();
        using (var engine = new TesseractEngine(_tessDataPath, _tessLang, EngineMode.Default))
        {
            foreach (var r in rects)
            {
                // padding в координатах proc
                var padded = new OCRect(
                    Math.Max(0, r.X - paddingPx),
                    Math.Max(0, r.Y - paddingPx),
                    Math.Min(proc.Width - Math.Max(0, r.X - paddingPx), r.Width + paddingPx * 2),
                    Math.Min(proc.Height - Math.Max(0, r.Y - paddingPx), r.Height + paddingPx * 2)
                );
                if (padded.Width <= 3 || padded.Height <= 3) continue;

                using var roi = new OCMat(proc, padded);
                using var gray = new OCMat();
                Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
                Cv2.Threshold(gray, gray, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

                // Mat -> Pix (без System.Drawing)
                using var pix = MatToPix(gray);
                using var page = engine.Process(pix, PageSegMode.Auto);
                string text = page.GetText() ?? string.Empty;
                float conf = page.GetMeanConfidence() * 100.0f;


                if (conf >= tesseractMinConfidence && Regex.IsMatch(text, @"\p{L}"))
                {
                    // padded — уже в coords proc (где мы делаем детекцию/OCR). Сохраним именно их.
                    confirmedBoxes.Add(padded);
                }
            }
        }

        // --- вместо прямого union: сначала сливаем и "инфлейтим" боксы в proc coords ---
        // confirmedBoxes — в координатах proc
        // Параметры: здесь разумные стартовые значения; подберите при необходимости
        int mergePaddingPx = Math.Min(20, paddingPx); // минимальный padding для merge (proc coords)
        double relInflate = 0.15;                     // inflate по размеру бокса (15%)
        int dilateIters = 2;

        

        // получаем объединённые прямоугольники в координатах proc
        var mergedProcBoxes = MergeAndInflateBoxes(proc, confirmedBoxes, paddingPx: mergePaddingPx, relInflate: relInflate, dilateIterations: dilateIters);

        // (опционально) debug — нарисовать mergedProcBoxes на proc и сохранить
        //Cv2.ImWrite("dbg_proc_merged_boxes.png", DrawBoxesOverlay(proc, mergedProcBoxes));

        // освобождаем временный resized (если был)
        resized?.Dispose();

        if (mergedProcBoxes == null || mergedProcBoxes.Count == 0)
            return orig.Clone();

        // переводим объединённые боксы из proc coords -> orig coords (учитываем scale)
        var confirmedOrigBoxes = new List<OCRect>(mergedProcBoxes.Count);
        foreach (var b in mergedProcBoxes)
        {
            int x0 = (int)Math.Round(b.X / scale);
            int y0 = (int)Math.Round(b.Y / scale);
            int w0 = (int)Math.Round(b.Width / scale);
            int h0 = (int)Math.Round(b.Height / scale);

            // clamp
            if (x0 < 0) { w0 += x0; x0 = 0; }
            if (y0 < 0) { h0 += y0; y0 = 0; }
            if (x0 + w0 > orig.Width) w0 = orig.Width - x0;
            if (y0 + h0 > orig.Height) h0 = orig.Height - y0;
            if (w0 <= 0 || h0 <= 0) continue;

            confirmedOrigBoxes.Add(new OCRect(x0, y0, w0, h0));
        }

        if (confirmedOrigBoxes.Count == 0)
            return orig.Clone();

        // финальный union по оригиналу — получаем один финальный ROI (или можно возвращать список блоков)
        int minX = confirmedOrigBoxes.Min(r => r.X);
        int minY = confirmedOrigBoxes.Min(r => r.Y);
        int maxX = confirmedOrigBoxes.Max(r => r.X + r.Width);
        int maxY = confirmedOrigBoxes.Max(r => r.Y + r.Height);

        int finalMargin = 10;
        minX = Math.Max(0, minX - finalMargin);
        minY = Math.Max(0, minY - finalMargin);
        maxX = Math.Min(orig.Width, maxX + finalMargin);
        maxY = Math.Min(orig.Height, maxY + finalMargin);

        var finalRoi = new OCRect(minX, minY, maxX - minX, maxY - minY);
        if (finalRoi.Width <= 0 || finalRoi.Height <= 0) return orig.Clone();

        return new OCMat(orig, finalRoi).Clone();


    }

    private static double IntersectionOverUnion(OCRect a, OCRect b)
    {
        if (a.Width <= 0 || a.Height <= 0 || b.Width <= 0 || b.Height <= 0)
            return 0.0;

        int x1 = Math.Max(a.Left, b.Left);
        int y1 = Math.Max(a.Top, b.Top);
        int x2 = Math.Min(a.Right, b.Right);
        int y2 = Math.Min(a.Bottom, b.Bottom);

        int interW = x2 - x1;
        int interH = y2 - y1;
        if (interW <= 0 || interH <= 0)
            return 0.0;

        double interArea = (double)interW * interH;
        double areaA = (double)a.Width * a.Height;
        double areaB = (double)b.Width * b.Height;
        double union = areaA + areaB - interArea;
        if (union <= 0) return 0.0;
        return interArea / union;
    }

    /// <summary>
    /// EAST decode: считываем 4D blob через At<float>(n,c,y,x>, без GetArray/unsafe.
    /// Возвращает прямоугольные (axis-aligned) боксы в координатах изображения proc.
    /// </summary>
    private static List<OCRect> DecodeEAST(OCMat scoreMat, OCMat geoMat, float scoreThresh,
                                           int inputW, int inputH, int outW, int outH)
    {
        var rects = new List<OCRect>();

        // Размер карты признаков (обычно input/4)
        int H = scoreMat.Size(2);
        int W = scoreMat.Size(3);

        float xRatio = outW / (float)inputW;
        float yRatio = outH / (float)inputH;

        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                float score = scoreMat.At<float>(0, 0, y, x);
                if (score < scoreThresh) continue;

                float d0 = geoMat.At<float>(0, 0, y, x);
                float d1 = geoMat.At<float>(0, 1, y, x);
                float d2 = geoMat.At<float>(0, 2, y, x);
                float d3 = geoMat.At<float>(0, 3, y, x);
                float angle = geoMat.At<float>(0, 4, y, x);

                // координаты центра (stride = 4)
                float cx = x * 4.0f + 2.0f;
                float cy = y * 4.0f + 2.0f;

                // Простейшая осевая аппроксимация (без поворота)
                float xMin = cx - d3;
                float xMax = cx + d1;
                float yMin = cy - d0;
                float yMax = cy + d2;

                int rx = (int)Math.Round(xMin * xRatio);
                int ry = (int)Math.Round(yMin * yRatio);
                int rw = (int)Math.Round((xMax - xMin) * xRatio);
                int rh = (int)Math.Round((yMax - yMin) * yRatio);

                if (rw <= 1 || rh <= 1) continue;

                // clip
                if (rx < 0) { rw += rx; rx = 0; }
                if (ry < 0) { rh += ry; ry = 0; }
                if (rx + rw > outW) rw = outW - rx;
                if (ry + rh > outH) rh = outH - ry;
                if (rw <= 1 || rh <= 1) continue;

                rects.Add(new OCRect(rx, ry, rw, rh));
            }
        }
        return rects;
    }

    /// <summary>
    /// Простая NMS по IoU для прямоугольников.
    /// </summary>
    private static List<OCRect> NonMaxSuppression(List<OCRect> rects, float nmsThreshold = 0.4f)
    {
        if (rects == null || rects.Count == 0) return new List<OCRect>();

        // сортируем по площади (крупные — первее)
        var order = Enumerable.Range(0, rects.Count)
                              .OrderByDescending(i => rects[i].Width * rects[i].Height)
                              .ToList();

        var picked = new List<int>();

        while (order.Count > 0)
        {
            int i = order[0];
            picked.Add(i);

            var keep = new List<int>();
            for (int j = 1; j < order.Count; j++)
            {
                int k = order[j];
                float xx1 = Math.Max(rects[i].Left, rects[k].Left);
                float yy1 = Math.Max(rects[i].Top, rects[k].Top);
                float xx2 = Math.Min(rects[i].Right, rects[k].Right);
                float yy2 = Math.Min(rects[i].Bottom, rects[k].Bottom);

                float w = Math.Max(0, xx2 - xx1);
                float h = Math.Max(0, yy2 - yy1);
                float inter = w * h;
                float areaI = rects[i].Width * rects[i].Height;
                float areaK = rects[k].Width * rects[k].Height;
                float ovr = inter / (areaI + areaK - inter);

                if (ovr <= nmsThreshold) keep.Add(k);
            }

            order = keep;
        }

        return picked.Select(idx => rects[idx]).ToList();
    }

    /// <summary>
    /// Конвертация OpenCvSharp.Mat → Tesseract.Pix без System.Drawing.
    /// </summary>
    private static Pix MatToPix(OCMat mat)
    {
        // гарантируем 8-bit 1ch/3ch
        OCMat src = mat;
        OCMat bgr = null;
        try
        {
            if (mat.Channels() == 1)
            {
                // ok
            }
            else if (mat.Channels() == 3)
            {
                bgr = mat;
            }
            else if (mat.Channels() == 4)
            {
                bgr = new OCMat();
                Cv2.CvtColor(mat, bgr, ColorConversionCodes.BGRA2BGR);
                src = bgr;
            }

            // Кодируем в PNG в память, затем Pix.LoadFromMemory
            Cv2.ImEncode(".png", src, out var buf);
            try
            {
                return Pix.LoadFromMemory(buf.ToArray());
            }
            finally
            {
                //buf.Dispose();
            }
        }
        finally
        {
            if (bgr != null && !ReferenceEquals(bgr, mat)) bgr.Dispose();
        }
    }

    private static List<OCRect> DetectHandwrittenCandidates(OCMat proc, double minAreaFraction = 0.0005)
    {
        var res = new List<OCRect>();
        if (proc == null || proc.Empty()) return res;

        // 1) gray
        using var gray = new OCMat();
        if (proc.Channels() == 3) Cv2.CvtColor(proc, gray, ColorConversionCodes.BGR2GRAY);
        else if (proc.Channels() == 4) Cv2.CvtColor(proc, gray, ColorConversionCodes.BGRA2GRAY);
        else proc.CopyTo(gray);

        // 2) gentle blur to connect handwriting strokes
        Cv2.GaussianBlur(gray, gray, new OCSize(5, 5), 0);

        // 3) adaptive threshold — больших blockSize, чтобы соединять тонкие штрихи
        using var mask = new OCMat();
        Cv2.AdaptiveThreshold(gray, mask, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 51, 8);



        // 4) morphology: close + dilate чтобы утолщить штрихи (поможет объединить слова/заметки)
        var kernClose = Cv2.GetStructuringElement(MorphShapes.Rect, new OCSize(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernClose, iterations: 1);
        var kernDil = Cv2.GetStructuringElement(MorphShapes.Rect, new OCSize(7, 3)); // горизонтально-тянущее
        Cv2.Dilate(mask, mask, kernDil, iterations: 2);



        // 5) connected components / contours
        Cv2.FindContours(mask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        if (contours == null || contours.Length == 0) return res;

        int imgArea = mask.Width * mask.Height;
        double minArea = Math.Max(10, imgArea * minAreaFraction); // минимум пикселей

        foreach (var c in contours)
        {
            var r = Cv2.BoundingRect(c);
            int area = r.Width * r.Height;
            if (area < minArea) continue;

            // фильтрация по соотношению сторон: рукописные заметки часто не очень узкие-полоски
            double ar = r.Width / (double)Math.Max(1, r.Height);
            if (ar < 0.05 && r.Height > 50) continue; // очень тонкая вертикальная линия — пропускаем
            if (ar > 20 && r.Width > 200) { /* сильно широкая линия — всё же можем принять */ }

            // вычислим плотность штрихов внутри (чтобы отсеять большие чистые участки)
            var roi = new OCMat(mask, r);
            double strokeDensity = ComputeStrokeDensity(roi); // 0..1
            roi.Dispose();

            if (strokeDensity < 0.001) continue; // почти пусто — не текст

            // Обрезаем слишком большие / полностью занимающие картинку — допускаем, но можно фильтровать
            res.Add(r);
        }

        return res;
    }

    private static double ComputeStrokeDensity(OCMat roi)
    {
        if (roi == null || roi.Empty()) return 0.0;
        // ожидаем бинарную маску (0/255). Если не бинарная — бинаризуем.
        OCMat bin = roi;
        OCMat tmp = null;
        if (roi.Channels() != 1)
        {
            tmp = new OCMat();
            Cv2.CvtColor(roi, tmp, ColorConversionCodes.BGR2GRAY);
            bin = tmp;
            Cv2.Threshold(bin, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
        }

        int nonZero = Cv2.CountNonZero(bin);
        double area = bin.Width * (double)bin.Height;

        tmp?.Dispose();
        return area <= 0 ? 0.0 : (nonZero / area);
    }
}
