// NuGet: OpenCvSharp4, OpenCvSharp4.runtime.win (или другой runtime), Tesseract

using OpenCvSharp;
using OpenCvSharp.Dnn;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Tesseract;
using OCMat = OpenCvSharp.Mat;
// Алиасы, чтобы не было ambiguous:
using OCRect = OpenCvSharp.Rect;
using OCSize = OpenCvSharp.Size;

public class TextAwareCropper
{
    private readonly string _eastModelPath;   // путь к frozen_east_text_detection.pb
    private readonly string _tessDataPath;    // папка tessdata с *.traineddata
    private readonly string _tessLang;        // "eng" или "rus+eng" и т.д.
    private CancellationToken _token;

    public TextAwareCropper(CancellationToken token, string eastModelPath, string tessDataPath, string tessLang = "eng")
    {
        _token = token;
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

    public OCMat ShowDetectedAreas(OCMat orig,
    int eastInputWidth = 1280, int eastInputHeight = 1280,
    float eastScoreThreshold = 0.45f,
    float eastNmsThreshold = 0.45f,
    int tesseractMinConfidence = 50,
    int paddingPx = 20,
    int downscaleMaxWidth = 1600)
    {
        if (orig == null || orig.Empty()) return orig;
        _token.ThrowIfCancellationRequested();
        OCMat resized = null;

        try
        {
            // 0) downscale (как в CropKeepingText)
            double scale = 1.0;
            OCMat proc = orig;
            if (downscaleMaxWidth > 0 && orig.Width > downscaleMaxWidth)
            {
                scale = downscaleMaxWidth / (double)orig.Width;
                resized = new OCMat();
                Cv2.Resize(orig, resized, new OCSize((int)(orig.Width * scale), (int)(orig.Height * scale)), 0, 0, InterpolationFlags.Area);
                proc = resized;
            }

            // 1) EAST detector
            using var net = CvDnn.ReadNet(_eastModelPath);
            net.SetPreferableBackend(Backend.OPENCV);
            net.SetPreferableTarget(Target.CPU);

            using var blob = CvDnn.BlobFromImage(proc, 1.0, new OCSize(eastInputWidth, eastInputHeight),
                                                 new Scalar(123.68, 116.78, 103.94), swapRB: true, crop: false);
            net.SetInput(blob);

            const string kScores = "feature_fusion/Conv_7/Sigmoid";
            const string kGeom = "feature_fusion/concat_3";

            using var scores = net.Forward(kScores);
            using var geometry = net.Forward(kGeom);

            Debug.WriteLine("score Size: " + scores.Size());
            Debug.WriteLine("geometry Size: " + geometry.Size());
            Debug.WriteLine("Layers names: " + net.GetUnconnectedOutLayersNames());

            var rects = DecodeEAST(scores, geometry, eastScoreThreshold, eastInputWidth, eastInputHeight, proc.Width, proc.Height);
            rects = NonMaxSuppression(rects, eastNmsThreshold);

            // объединяем кандидатов: EAST + (опционально) handwriting + stamp
            var textCandidates = new List<OpenCvSharp.Rect>(rects);

            _token.ThrowIfCancellationRequested();

            // Добавим handwritten и stamp кандидаты (рекомендуется включать, если нужны печати/записи)
            var hwCandidates = DetectHandwrittenCandidates(proc, minAreaFraction: 0.0002);
            if (hwCandidates != null && hwCandidates.Count > 0) textCandidates.AddRange(hwCandidates);

            var stampCandidates = DetectStampCandidates(proc, minAreaFraction: 0.00002);
            if (stampCandidates != null && stampCandidates.Count > 0) textCandidates.AddRange(stampCandidates);

            if (textCandidates == null || textCandidates.Count == 0)
            {
                resized?.Dispose();
                return orig.Clone();
            }

            // ПАРАМЕТРЫ OCR-first
            int ocrPadding = Math.Min(60, Math.Max(20, paddingPx / 2)); // небольшой padding для OCR
            int targetCharHeight = 48;    // апскейлим до этой высоты для OCR - 48
            double minAlnumRatio = 0.50;  // доля букв/цифр
            int minAlnumCount = 1;
            int minTessConf = Math.Max(30, Math.Min(60, tesseractMinConfidence)); // допустимый диапазон

            var confirmedProcBoxes = new List<OpenCvSharp.Rect>();

            // 2) OCR по каждому кандидату (малые ROI — апскейлим), фильтрация — только подтверждённые идут дальше
            using (var engine = new TesseractEngine(_tessDataPath, _tessLang, EngineMode.Default))
            {
                // НЕ ставим глобальный whitelist — используем его только для цифровых ROI (page numbers)
                foreach (var cand in textCandidates)
                {
                    _token.ThrowIfCancellationRequested();
                    // маленький padded ROI вокруг кандидата (proc coords)
                    int px = Math.Max(0, cand.X - ocrPadding);
                    int py = Math.Max(0, cand.Y - ocrPadding);
                    int pw = Math.Min(proc.Width - px, cand.Width + ocrPadding * 2);
                    int ph = Math.Min(proc.Height - py, cand.Height + ocrPadding * 2);
                    if (pw <= 3 || ph <= 3) continue;

                    var roiRect = new OpenCvSharp.Rect(px, py, pw, ph);

                    using var roi = new OCMat(proc, roiRect);

                    // preproc: gray
                    using var gray = new OCMat();
                    if (roi.Channels() == 3) Cv2.CvtColor(roi, gray, ColorConversionCodes.BGR2GRAY);
                    else if (roi.Channels() == 4) Cv2.CvtColor(roi, gray, ColorConversionCodes.BGRA2GRAY);
                    else roi.CopyTo(gray);

                    // апскейлим, если маленький (для повышения качества OCR)
                    OCMat grayForOcr = gray;
                    if (gray.Height < targetCharHeight)
                    {
                        double f = targetCharHeight / (double)Math.Max(1, gray.Height);
                        var up = new OCMat();
                        Cv2.Resize(gray, up, new OCSize((int)(gray.Width * f), (int)(gray.Height * f)), 0, 0, InterpolationFlags.Linear);
                        grayForOcr = up;
                    }

                    // сглаживание + adaptive threshold (обычно лучше для старых сканов)
                    Cv2.GaussianBlur(grayForOcr, grayForOcr, new OCSize(3, 3), 0);
                    Cv2.AdaptiveThreshold(grayForOcr, grayForOcr, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.Binary, 41, 30);

                    // PSM heuristics
                    PageSegMode psm = PageSegMode.Auto;
                    if (roiRect.Width < roiRect.Height * 2) psm = PageSegMode.SingleWord;
                    else if (roiRect.Width > roiRect.Height * 6) psm = PageSegMode.SingleLine;

                    // detect likely page-number (near bottom & narrow)
                    bool nearBottom = (roiRect.Y + roiRect.Height) > proc.Height * 0.82;
                    bool narrow = roiRect.Width < proc.Width * 0.70;
                    bool useDigits = nearBottom && narrow;

                    string ocrText = string.Empty;
                    float conf = 0f;

                    if (useDigits)
                    {
                        // временный engine с whitelist цифр
                        using var engDigits = new TesseractEngine(_tessDataPath, _tessLang, EngineMode.Default);
                        //engDigits.SetVariable("tessedit_char_whitelist", "0123456789");
                        using var pix = MatToPix(grayForOcr);
                        using var page = engDigits.Process(pix, psm);
                        ocrText = page.GetText() ?? string.Empty;
                        //Debug.Write(ocrText);
                        conf = page.GetMeanConfidence() * 100.0f;
                    }
                    else
                    {
                        using var pix = MatToPix(grayForOcr);
                        using var page = engine.Process(pix, psm);
                        ocrText = page.GetText() ?? string.Empty;
                        //Debug.Write(ocrText);
                        conf = page.GetMeanConfidence() * 100.0f;
                    }

                    string normalized = ocrText.Trim();
                    if (!string.IsNullOrEmpty(normalized))
                    {
                        int alnumCount = normalized.Count(ch => char.IsLetterOrDigit(ch));
                        int visibleCount = normalized.Count(ch => !char.IsControl(ch) && !char.IsWhiteSpace(ch));
                        double alnumRatio = visibleCount == 0 ? 0.0 : alnumCount / (double)visibleCount;

                        bool passed = (conf >= minTessConf && alnumCount >= minAlnumCount && alnumRatio >= minAlnumRatio)
                                      || (conf >= 30 && alnumCount >= 1 && alnumRatio >= 0.9); // короткие цифры допускаем мягче

                        if (passed)
                        {
                            confirmedProcBoxes.Add(roiRect);
                        }
                    }

                    if (!ReferenceEquals(grayForOcr, gray))
                    {
                        grayForOcr.Dispose();
                    }
                } // foreach candidate
            } // using engine

            // Если ничего не прошло OCR — попробуем принять stampCandidates (visual only) чтобы не терять печати
            if (confirmedProcBoxes.Count == 0 && stampCandidates != null && stampCandidates.Count > 0)
            {
                confirmedProcBoxes.AddRange(stampCandidates);
            }

            if (confirmedProcBoxes.Count == 0)
            {
                resized?.Dispose();
                return orig.Clone();
            }
            _token.ThrowIfCancellationRequested();
            // 3) merge уже подтверждённых боксов (proc coords)
            int mergePaddingPx = Math.Min(80, Math.Max(20, paddingPx / 2));
            double relInflate = 0.4;
            int dilateIters = 3;
            var mergedProcBoxes = MergeAndInflateBoxes(proc, confirmedProcBoxes, paddingPx: mergePaddingPx, relInflate: relInflate, dilateIterations: dilateIters);

            //+++ дополнительно сливаем близкие боксы (без инфлейта)
            mergedProcBoxes = MergeNearbyRects(mergedProcBoxes, maxGapPx: 24, iouThresh: 0.12);


            resized?.Dispose();

            // 4) переводим в координаты orig и рисуем
            var outImg = orig.Clone();
            for (int i = 0; mergedProcBoxes != null && i < mergedProcBoxes.Count; i++)
            {
                _token.ThrowIfCancellationRequested();
                var b = mergedProcBoxes[i];

                int x0 = (int)System.Math.Round(b.X / scale);
                int y0 = (int)System.Math.Round(b.Y / scale);
                int w0 = (int)System.Math.Round(b.Width / scale);
                int h0 = (int)System.Math.Round(b.Height / scale);

                if (x0 < 0) { w0 += x0; x0 = 0; }
                if (y0 < 0) { h0 += y0; y0 = 0; }
                if (x0 + w0 > outImg.Width) w0 = outImg.Width - x0;
                if (y0 + h0 > outImg.Height) h0 = outImg.Height - y0;
                if (w0 <= 0 || h0 <= 0) continue;

                var rect = new OpenCvSharp.Rect(x0, y0, w0, h0);
                Cv2.Rectangle(outImg, rect, new Scalar(0, 255, 0), thickness: 2);

                // подпись индекса (без ambigous Math)
                string label = (i + 1).ToString();
                var font = HersheyFonts.HersheySimplex;
                double fontScale = System.Math.Max(0.4, System.Math.Min(0.9, System.Math.Min(rect.Width, rect.Height) / 180.0));
                Cv2.GetTextSize(label, font, fontScale, 1, out int baseLine);
                var textSize = Cv2.GetTextSize(label, font, fontScale, 1, out _);
                var textOrg = new OpenCvSharp.Point(rect.X + 4, rect.Y + 4 + System.Math.Abs(baseLine));
                int paddingX = 6, paddingY = 4;
                int bgX = System.Math.Max(0, textOrg.X - 2);
                int bgY = System.Math.Max(0, textOrg.Y - System.Math.Abs(baseLine) - 2);
                int bgW = System.Math.Min(outImg.Width - bgX, textSize.Width + paddingX);
                int bgH = System.Math.Min(outImg.Height - bgY, System.Math.Abs(baseLine) + paddingY);
                var textBg = new OpenCvSharp.Rect(bgX, bgY, System.Math.Max(1, bgW), System.Math.Max(1, bgH));
                Cv2.Rectangle(outImg, textBg, new Scalar(0, 255, 0), thickness: -1);
                Cv2.PutText(outImg, label, textOrg, font, fontScale, new Scalar(0, 0, 0), thickness: 1);
            }

            return outImg;
        }
        catch
        {
            resized?.Dispose();
            return orig.Clone();
        }
    }



    private static List<OpenCvSharp.Rect> MergeNearbyRects(List<OpenCvSharp.Rect> rects, int maxGapPx = 30, double iouThresh = 0.15)
    {
        if (rects == null || rects.Count == 0) return new List<OpenCvSharp.Rect>();

        // сортируем по левой координате (можно по площади)
        var list = rects.OrderBy(r => r.X).ToList();
        var merged = new List<OpenCvSharp.Rect>();

        bool[] used = new bool[list.Count];
        for (int i = 0; i < list.Count; i++)
        {
            if (used[i]) continue;
            var a = list[i];
            var cur = a;
            used[i] = true;

            // будем пробегать и объединять все, которые близко или пересекаются
            bool changed;
            do
            {
                changed = false;
                for (int j = 0; j < list.Count; j++)
                {
                    if (used[j]) continue;
                    var b = list[j];

                    // IoU
                    int ix1 = Math.Max(cur.X, b.X);
                    int iy1 = Math.Max(cur.Y, b.Y);
                    int ix2 = Math.Min(cur.Right, b.Right);
                    int iy2 = Math.Min(cur.Bottom, b.Bottom);
                    int iw = Math.Max(0, ix2 - ix1);
                    int ih = Math.Max(0, iy2 - iy1);
                    double inter = (double)(iw * ih);
                    double areaUnion = (double)(cur.Width * cur.Height + b.Width * b.Height) - inter;
                    double iou = areaUnion > 0 ? inter / areaUnion : 0.0;

                    // Distance between centers (alternative)
                    var cxA = cur.X + cur.Width / 2;
                    var cyA = cur.Y + cur.Height / 2;
                    var cxB = b.X + b.Width / 2;
                    var cyB = b.Y + b.Height / 2;
                    double centerDist = Math.Sqrt((cxA - cxB) * (cxA - cxB) + (cyA - cyB) * (cyA - cyB));

                    // gap by bounding boxes (horizontal/vertical)
                    int gapX = Math.Max(0, Math.Max(b.X - cur.Right, cur.X - b.Right));
                    int gapY = Math.Max(0, Math.Max(b.Y - cur.Bottom, cur.Y - b.Bottom));
                    int gap = Math.Max(gapX, gapY);

                    if (iou > iouThresh || gap <= maxGapPx || centerDist < Math.Max(cur.Width, cur.Height) * 0.6)
                    {
                        // merge cur and b
                        int nx = Math.Min(cur.X, b.X);
                        int ny = Math.Min(cur.Y, b.Y);
                        int nr = Math.Max(cur.Right, b.Right);
                        int nb = Math.Max(cur.Bottom, b.Bottom);
                        cur = new OpenCvSharp.Rect(nx, ny, nr - nx, nb - ny);
                        used[j] = true;
                        changed = true;
                    }
                }
            } while (changed);

            merged.Add(cur);
        }

        return merged;
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

        var textCandidates = new List<OpenCvSharp.Rect>(rects);

        // 1) Candidate handwritten regions (ваш метод уже в классе)
        var hwCandidates = DetectHandwrittenCandidates(proc, minAreaFraction: 0.0001);
        if (hwCandidates != null && hwCandidates.Count > 0)
            textCandidates.AddRange(hwCandidates);

        // 2) Candidate stamps/seals (новый метод, см. ниже)
        var stampCandidates = DetectStampCandidates(proc, minAreaFraction: 0.0001);
        if (stampCandidates != null && stampCandidates.Count > 0)
            textCandidates.AddRange(stampCandidates);



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
        int mergePaddingPx = Math.Min(60, Math.Max(10, paddingPx)); // минимальный padding для merge (proc coords)
        //int mergePaddingPx = paddingPx;
        double relInflate = 0.35;                     // inflate по размеру бокса (15%)
        int dilateIters = 4;



        // получаем объединённые прямоугольники в координатах proc
        //var mergedProcBoxes = MergeAndInflateBoxes(proc, confirmedBoxes, paddingPx: mergePaddingPx, relInflate: relInflate, dilateIterations: dilateIters);
        var mergedProcBoxes = MergeAndInflateBoxes(proc, textCandidates, paddingPx: mergePaddingPx, relInflate: relInflate, dilateIterations: dilateIters);

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

        //+++
        using (var roiView = new Mat(orig, finalRoi))
        {
            var (insetL, insetT, insetR, insetB) = ComputeBorderInsets(roiView, useHoughRefine: true); // изменяемый параметр true/false

            int nx = finalRoi.X + insetL;
            int ny = finalRoi.Y + insetT;
            int nw = finalRoi.Width - insetL - insetR;
            int nh = finalRoi.Height - insetT - insetB;

            // Если вдруг съели слишком много — откатываем частично
            if (nw < finalRoi.Width * 0.6 || nh < finalRoi.Height * 0.6)
            {
                // мягче режем (половину найденной рамки)
                insetL /= 2; insetR /= 2; insetT /= 2; insetB /= 2;
                nx = finalRoi.X + insetL;
                ny = finalRoi.Y + insetT;
                nw = finalRoi.Width - insetL - insetR;
                nh = finalRoi.Height - insetT - insetB;
            }

            if (nw > 0 && nh > 0)
                finalRoi = new OCRect(nx, ny, nw, nh);
        }

        return new OCMat(orig, finalRoi).Clone();


    }

    /// Бинаризуем в "чёрное=текст/рамка", "белое=фон", затем считаем долю чёрного в строках/столбцах.
    private static void MakeBlackMask(Mat src, out Mat bin)
    {
        using var gray = new Mat();
        if (src.Channels() == 3) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else if (src.Channels() == 4) Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
        else src.CopyTo(gray);

        // Лёгкое сглаживание, чтобы рамка стала сплошнее
        Cv2.GaussianBlur(gray, gray, new OCSize(3, 3), 0);
        bin = new Mat();
        // Otsu + BinaryInv: тёмное → 255, светлое → 0
        Cv2.Threshold(gray, bin, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.BinaryInv);
    }

    /// Поиск от края внутрь: находим полосу повышенной "черноты" (рамку), затем устойчивую «чистую» область.
    /// Возвращаем толщину рамки (в пикселях).
    private static int FindBorderThicknessFromEdge(Mat bin, bool vertical, bool fromStart,
                                               double blackFracThreshold = 0.10,
                                               int minBand = 3, int minClear = 6)
    {
        int W = bin.Width, H = bin.Height;
        int lenOuter = vertical ? H : W;
        int lenInner = vertical ? W : H;

        var frac = new double[lenOuter];

        for (int i = 0; i < lenOuter; i++)
        {
            OpenCvSharp.Rect lineRect = vertical ? new OpenCvSharp.Rect(0, i, W, 1) : new OpenCvSharp.Rect(i, 0, 1, H);
            using var line = new Mat(bin, lineRect);
            int nz = Cv2.CountNonZero(line);
            frac[i] = nz / (double)lenInner;
        }

        int start = fromStart ? 0 : lenOuter - 1;
        int step = fromStart ? 1 : -1;

        int iCur = start, bandCnt = 0;
        for (; iCur >= 0 && iCur < lenOuter; iCur += step)
        {
            bandCnt = (frac[iCur] >= blackFracThreshold) ? bandCnt + 1 : 0;
            if (bandCnt >= minBand) { iCur += step; break; }
        }
        if (bandCnt < minBand) return 0;

        int clearCnt = 0, endIdx = iCur;
        for (; iCur >= 0 && iCur < lenOuter; iCur += step)
        {
            clearCnt = (frac[iCur] < blackFracThreshold * 0.5) ? clearCnt + 1 : 0;
            if (clearCnt >= minClear) { endIdx = iCur - step * (minClear - 1); break; }
        }

        int thickness = fromStart ? Math.Max(0, endIdx) : Math.Max(0, (lenOuter - 1) - endIdx);
        int maxThick = Math.Max(8, lenOuter / 20);
        return Math.Min(thickness, maxThick);
    }


    /// Основной вычислитель инсеттов рамки. Плюс (опционально) уточнение длинными линиями Хафа.
    private static (int left, int top, int right, int bottom) ComputeBorderInsets(Mat src,
                                                                                  bool useHoughRefine = true)
    {
        MakeBlackMask(src, out var bin);
        using (bin)
        {
            int top = FindBorderThicknessFromEdge(bin, vertical: true, fromStart: true);
            int bottom = FindBorderThicknessFromEdge(bin, vertical: true, fromStart: false);
            int left = FindBorderThicknessFromEdge(bin, vertical: false, fromStart: true);
            int right = FindBorderThicknessFromEdge(bin, vertical: false, fromStart: false);

            if (useHoughRefine)
            {
                // Уточняем положением длинных линий возле краёв
                using var edges = new Mat();
                Cv2.Canny(bin, edges, 50, 150);
                var lines = Cv2.HoughLinesP(edges, 1, Math.PI / 180, threshold: 120,
                                            minLineLength: Math.Min(src.Width, src.Height) / 3,
                                            maxLineGap: 10);

                foreach (var l in lines)
                {
                    // Горизонтали
                    if (Math.Abs(l.P1.Y - l.P2.Y) <= 2)
                    {
                        int y = l.P1.Y;
                        if (y < src.Height / 4) top = Math.Max(top, y + 1);
                        if (y > (src.Height * 3) / 4) bottom = Math.Max(bottom, src.Height - y);
                    }
                    // Вертикали
                    if (Math.Abs(l.P1.X - l.P2.X) <= 2)
                    {
                        int x = l.P1.X;
                        if (x < src.Width / 4) left = Math.Max(left, x + 1);
                        if (x > (src.Width * 3) / 4) right = Math.Max(right, src.Width - x);
                    }
                }
            }

            // sanity clamp
            left = Math.Min(left, src.Width / 3);
            right = Math.Min(right, src.Width / 3);
            top = Math.Min(top, src.Height / 3);
            bottom = Math.Min(bottom, src.Height / 3);

            return (left, top, right, bottom);
        }
    }


    private static List<OCRect> DetectStampCandidates(OCMat proc, double minAreaFraction = 0.00005)
{
    var res = new List<OCRect>();
    if (proc == null || proc.Empty()) return res;

    // работаем в сером
    using var gray = new OCMat();
    if (proc.Channels() == 3) Cv2.CvtColor(proc, gray, ColorConversionCodes.BGR2GRAY);
    else if (proc.Channels() == 4) Cv2.CvtColor(proc, gray, ColorConversionCodes.BGRA2GRAY);
    else proc.CopyTo(gray);

    // немного усилим контуры: blur -> Canny
    Cv2.GaussianBlur(gray, gray, new OCSize(5, 5), 0);
    using var edges = new OCMat();
    Cv2.Canny(gray, edges, 50, 150);

    // закрыть дырки (close) чтобы контуры стали цельными
    var ker = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OCSize(9, 9));
    Cv2.MorphologyEx(edges, edges, MorphTypes.Close, ker, iterations: 2);

    // find contours
    Cv2.FindContours(edges, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
    if (contours == null || contours.Length == 0) return res;

    int imgArea = proc.Width * proc.Height;
    double minArea = Math.Max(30, imgArea * minAreaFraction);

    foreach (var c in contours)
    {
        double area = Cv2.ContourArea(c);
        if (area < minArea) continue;

        double perim = Cv2.ArcLength(c, true);
        if (perim <= 0) continue;
        double circularity = 4.0 * Math.PI * area / (perim * perim); // 0..1

        // bounding rect and aspect
        var r = Cv2.BoundingRect(c);
        double ar = r.Width / (double)Math.Max(1, r.Height);

        // compute stroke density inside bounding box on a closed-thresholded image
        using var roi = new OCMat(proc, r);
        using var tmp = new OCMat();
        if (roi.Channels() > 1) Cv2.CvtColor(roi, tmp, ColorConversionCodes.BGR2GRAY);
        else roi.CopyTo(tmp);

        Cv2.Threshold(tmp, tmp, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);
        double nonZero = Cv2.CountNonZero(tmp);
        double density = nonZero / (r.Width * (double)r.Height);

        // heuristics: stamps часто круговые/эллиптические с достаточно плотной штриховкой
        bool likelyStamp = (circularity > 0.35 && density > 0.005 && r.Width > 20 && r.Height > 20)
                           || (density > 0.02 && (ar > 0.3 && ar < 3.0) && area > imgArea * 0.0002);

        if (likelyStamp)
        {
            // немного расширим bbox
            int extra = (int)Math.Round(Math.Max(6, Math.Min(r.Width, r.Height) * 0.08));
            int x = Math.Max(0, r.X - extra);
            int y = Math.Max(0, r.Y - extra);
            int w = Math.Min(proc.Width - x, r.Width + extra * 2);
            int h = Math.Min(proc.Height - y, r.Height + extra * 2);
            if (w > 3 && h > 3) res.Add(new OCRect(x, y, w, h));
        }
    }

    return res;
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
