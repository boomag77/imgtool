using OpenCvSharp;
using System.Diagnostics;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models;

public class Despeckler
{
    public enum DespeckleMethod
    {
        Classic,
        Effective
    }

    public static bool TryDespeckle(DespeckleMethod method, CancellationToken token, Mat src, out Mat? result, DespeckleSettings? settings = null,
                             bool debug = false, bool inputIsBinary = false, bool applyMaskToSource = true)
    {
        result = method switch
        {
            DespeckleMethod.Classic => DespeckleClassic(token, src, settings, debug, inputIsBinary, applyMaskToSource),
            DespeckleMethod.Effective => DespeckleEffective(token, src, settings, debug, inputIsBinary, applyMaskToSource),
            _ => null
        };
        return result != null;
    }

    public static Mat DespeckleClassic(CancellationToken token,
                                        Mat src,
                                        DespeckleSettings? settings = null,
                                        bool debug = false,
                                        bool inputIsBinary = false,
                                        bool applyMaskToSource = true)    // <-- new parameter
    {
        var startTime = DateTime.Now;
        try
        {
            token.ThrowIfCancellationRequested();

            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Mat(); // TODO: check if this is correct

            // helpers
            static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
            static double ClampDouble(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

            settings ??= new DespeckleSettings();

            // If src is already binary and inputIsBinary==true, we will use it directly.
            // Otherwise we'll compute bin from src.
            Mat bin = null!;
            bool localBinCreated = false;

            try
            {
                if (inputIsBinary)
                {
                    // src может быть бинарной в gray или BGR/BGRA (после BinarizeAdaptive)
                    Mat grayBin = new Mat();
                    if (src.Channels() == 1)
                    {

                        src.CopyTo(grayBin);
                    }
                    else if (src.Channels() == 3)
                    {
                        Cv2.CvtColor(src, grayBin, ColorConversionCodes.BGR2GRAY);
                    }
                    else if (src.Channels() == 4)
                    {
                        Cv2.CvtColor(src, grayBin, ColorConversionCodes.BGRA2GRAY);
                    }
                    else
                    {
                        throw new ArgumentException("inputIsBinary == true requires 1, 3 or 4 channel image.");
                    }

                    token.ThrowIfCancellationRequested();
                    // Автоопределение полярности: хотим bin, где text == 255, background == 0
                    double white = Cv2.CountNonZero(grayBin);
                    double total = grayBin.Rows * grayBin.Cols;
                    if (white >= total / 2.0)
                    {
                        // Белого больше половины → фон белый, текст чёрный → инвертируем
                        Cv2.BitwiseNot(grayBin, grayBin);
                    }
                    // Теперь в grayBin: text == 255, bg == 0
                    bin = grayBin;
                    localBinCreated = true;
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    // Convert to gray if needed
                    Mat gray = new Mat();
                    if (src.Channels() == 3)
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                    else if (src.Channels() == 4)
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                    else
                        src.CopyTo(gray);

                    token.ThrowIfCancellationRequested();
                    // Estimate background (large open)
                    int grayCols = gray.Cols;
                    int approxBgKernel = ClampInt(grayCols / 30, 51, Math.Max(51, grayCols / 10));
                    token.ThrowIfCancellationRequested();
                    var kernelBg = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(approxBgKernel | 1, approxBgKernel | 1));
                    token.ThrowIfCancellationRequested();
                    Mat bg = new();
                    Cv2.MorphologyEx(gray, bg, MorphTypes.Open, kernelBg);
                    kernelBg.Dispose();

                    token.ThrowIfCancellationRequested();
                    // Normalize by background
                    Mat corr = new();
                    Cv2.Subtract(gray, bg, corr);
                    token.ThrowIfCancellationRequested();
                    Cv2.Normalize(corr, corr, 0, 255, NormTypes.MinMax);
                    token.ThrowIfCancellationRequested();
                    corr.ConvertTo(corr, MatType.CV_8UC1);

                    // Light denoise
                    Mat denoised = new();
                    Cv2.MedianBlur(corr, denoised, 3);

                    // Decide Otsu vs Adaptive
                    token.ThrowIfCancellationRequested();
                    Cv2.MinMaxLoc(bg, out double bgMin, out double bgMax);
                    double bgRange = bgMax - bgMin;

                    Mat binLocal = new();
                    if (bgRange < 30)
                    {
                        token.ThrowIfCancellationRequested();
                        Cv2.Threshold(denoised, binLocal, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                        // Хотим binLocal с text == 255, bg == 0
                        double whitePixels = Cv2.CountNonZero(binLocal);
                        double total = binLocal.Rows * binLocal.Cols;

                        // Если белого >= половины → скорее всего фон белый, текст чёрный → инвертируем
                        if (whitePixels >= total / 2.0)
                            Cv2.BitwiseNot(binLocal, binLocal);
                    }
                    else
                    {
                        int blockSize = ClampInt(grayCols / 40, 11, 101) | 1;
                        int C = 14;
                        Cv2.AdaptiveThreshold(denoised, binLocal, 255,
                            AdaptiveThresholdTypes.GaussianC,
                            ThresholdTypes.Binary,
                            blockSize, C);

                        // Та же логика полярности
                        double whitePixels = Cv2.CountNonZero(binLocal);
                        double total = binLocal.Rows * binLocal.Cols;
                        if (whitePixels >= total / 2.0)
                            Cv2.BitwiseNot(binLocal, binLocal);
                    }

                    // cleanup temporaries
                    gray.Dispose();
                    bg.Dispose();
                    corr.Dispose();
                    denoised.Dispose();

                    bin = binLocal;
                    localBinCreated = true;
                }

                // Now we have 'bin' as CV_8UC1 where text == 255, background == 0
                if (settings.EnableDustRemoval)
                {
                    token.ThrowIfCancellationRequested();
                    int medianK = Math.Max(1, settings.DustMedianKsize);
                    if (medianK % 2 == 0) medianK += 1;

                    int openK = Math.Max(1, settings.DustOpenKernel);
                    if (openK % 2 == 0) openK += 1;

                    int openIter = Math.Max(1, settings.DustOpenIter);

                    var dustBin = bin.Clone();
                    if (medianK > 1)
                        Cv2.MedianBlur(dustBin, dustBin, medianK);

                    if (openK > 1)
                    {
                        using var dustKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(openK, openK));
                        Cv2.MorphologyEx(dustBin, dustBin, MorphTypes.Open, dustKernel, iterations: openIter);
                    }

                    if (localBinCreated)
                        bin.Dispose();
                    bin = dustBin;
                    localBinCreated = true;
                }

                token.ThrowIfCancellationRequested();
                // Mask of original text pixels: 255 where bin == 255
                using var textMask = new Mat();
                Cv2.InRange(bin, new Scalar(255), new Scalar(255), textMask); // 0 where text==255

                // Prepare labelingMat (optionally dilate to merge touching dots)
                Mat labelingMat = bin.Clone();
                if (settings.UseDilateBeforeCC && settings.DilateIter > 0)
                {
                    token.ThrowIfCancellationRequested();
                    Mat k;
                    switch (settings.DilateKernel)
                    {
                        case "3x1": k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 1)); break;
                        case "3x3": k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)); break;
                        default: k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3)); break;
                    }
                    token.ThrowIfCancellationRequested();
                    var tmp = new Mat();
                    Cv2.Dilate(labelingMat, tmp, k, iterations: settings.DilateIter);
                    labelingMat.Dispose();
                    k.Dispose();
                    labelingMat = tmp;
                }

                // Connected components on labelingMat
                using var labels = new Mat();
                using var stats = new Mat();
                using var centroids = new Mat();
                token.ThrowIfCancellationRequested();
                int nLabels = Cv2.ConnectedComponentsWithStats(labelingMat, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);

                var comps = new List<(int label, Rect bbox, int area)>();
                for (int lbl = 1; lbl < nLabels; lbl++)
                {
                    token.ThrowIfCancellationRequested();
                    int left = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Left);
                    int top = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Top);
                    int width = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Width);
                    int height = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Height);
                    int area = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Area);
                    comps.Add((lbl, new Rect(left, top, width, height), area));
                }

                if (comps.Count == 0)
                {
                    labelingMat.Dispose();
                    textMask.Dispose();
                    var retEmpty = applyMaskToSource ? src.Clone() : bin.Clone();
                    //if (localBinCreated) bin.Dispose();
                    return retEmpty;
                }

                // median char height
                var heights = comps.Select(c => c.bbox.Height).Where(h => h >= 3).ToArray();
                int medianHeight = heights.Length > 0 ? heights.OrderBy(h => h).ElementAt(heights.Length / 2) : 20;

                // thresholds
                int smallAreaThrPx = settings.SmallAreaRelative
                    ? Math.Max(1, (int)Math.Round(settings.SmallAreaMultiplier * medianHeight * medianHeight))
                    : Math.Max(1, settings.SmallAreaAbsolutePx);

                int maxDotHeight = Math.Max(1, (int)Math.Round(settings.MaxDotHeightFraction * medianHeight));
                double proximityRadius = Math.Max(1.0, settings.ProximityRadiusFraction * medianHeight);
                double squarenessTolerance = ClampDouble(settings.SquarenessTolerance, 0.0, 1.0);

                //int binRows = bin.Rows, binCols = bin.Cols;
                //var horProj = new int[binRows];
                //for (int y = 0; y < binRows; y++)
                //{
                //    if (y % 32 == 0)
                //        token.ThrowIfCancellationRequested();
                //    horProj[y] = Cv2.CountNonZero(bin.Row(y)); // text px per row
                //}

                int binRows = bin.Rows, binCols = bin.Cols;

                // ONE OpenCV pass instead of binRows calls:
                using var projSum = new Mat(); // (binRows x 1) int32
                Cv2.Reduce(bin, projSum, dim: ReduceDimension.Column, rtype: ReduceTypes.Sum, dtype: MatType.CV_32S);

                var horProj = new int[binRows];
                for (int y = 0; y < binRows; y++)
                {
                    if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                    horProj[y] = projSum.Get<int>(y, 0) / 255; // bin is 0/255
                }



                //int projThr = Math.Max(1, binCols / 100);
                int projThr = Math.Max(10, binCols / 40); // ~2.5% заполняемости строки

                //var textRows = new HashSet<int>(
                //    Enumerable.Range(0, binRows).Where(y => horProj[y] > projThr)
                //);

                var textRowFlags = new bool[binRows];
                for (int y = 0; y < binRows; y++)
                    textRowFlags[y] = horProj[y] > projThr;

                Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

                var bigBoxes = comps.Where(c => c.bbox.Height >= medianHeight * 0.6 || c.area > smallAreaThrPx * 4)
                                    .Select(c => c.bbox).ToArray();


                static double DistPointToRectSq(OpenCvSharp.Point p, OpenCvSharp.Rect r)
                {
                    int dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
                    int dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);

                    // avoid int overflow by promoting before multiply
                    double ddx = dx;
                    double ddy = dy;
                    return ddx * ddx + ddy * ddy;
                }


                var smallComps = comps.Where(c => c.area < smallAreaThrPx || c.bbox.Height <= maxDotHeight).ToArray();

                var toRemoveLabels = new List<int>();
                var toKeepLabels = new HashSet<int>();




                int rowCheckRange = Math.Max(1, medianHeight / 3);
                int clusterHoriz = Math.Max(3, (int)(medianHeight * 0.6));

                // ---- Precompute centers once (huge win even without grid) ----
                var smallCenters = new Point[smallComps.Length];
                for (int i = 0; i < smallComps.Length; i++)
                    smallCenters[i] = Center(smallComps[i].bbox);

                // ---- Build grid buckets only if KeepClusters enabled ----
                List<int>[]? buckets = null;
                int cellW = 0, cellH = 0, gridW = 0, gridH = 0;

                if (settings.KeepClusters && smallComps.Length > 1)
                {
                    // Cell sizes: pick thresholds so neighbors are in same/adjacent buckets
                    cellW = Math.Max(1, clusterHoriz);     // horizontal threshold
                    cellH = Math.Max(1, rowCheckRange);    // vertical threshold

                    gridW = (binCols + cellW - 1) / cellW;
                    gridH = (binRows + cellH - 1) / cellH;

                    buckets = new List<int>[gridW * gridH];
                    for (int k = 0; k < buckets.Length; k++)
                        buckets[k] = new List<int>();

                    for (int i = 0; i < smallCenters.Length; i++)
                    {
                        var p = smallCenters[i];

                        int gx = p.X / cellW;
                        int gy = p.Y / cellH;

                        if (gx < 0) gx = 0; else if (gx >= gridW) gx = gridW - 1;
                        if (gy < 0) gy = 0; else if (gy >= gridH) gy = gridH - 1;

                        buckets[gx + gy * gridW].Add(i); // store index of smallComps
                    }
                }

                double radiusSq = proximityRadius * proximityRadius;
                for (int i = 0; i < smallComps.Length; i++)
                {
                    if ((i & 127) == 0) token.ThrowIfCancellationRequested();

                    var c = smallComps[i];
                    var rect = c.bbox;
                    var center = smallCenters[i];
                    if (settings.EnableDustShapeFilter)
                    {
                        double minSolidity = ClampDouble(settings.DustMinSolidity, 0.05, 1.0);
                        double maxAspect = ClampDouble(settings.DustMaxAspectRatio, 1.0, 20.0);

                        double aspect = rect.Height <= 0 ? maxAspect : (double)rect.Width / rect.Height;
                        if (aspect < 1.0) aspect = 1.0 / aspect;

                        if (aspect >= maxAspect)
                        {
                            toRemoveLabels.Add(c.label);
                            continue;
                        }

                        using var compMask = new Mat(rect.Height, rect.Width, MatType.CV_8UC1, Scalar.All(0));
                        for (int yy = rect.Top; yy < rect.Bottom; yy++)
                        {
                            if ((yy & 31) == 0) token.ThrowIfCancellationRequested();
                            for (int xx = rect.Left; xx < rect.Right; xx++)
                            {
                                if (labels.Get<int>(yy, xx) == c.label)
                                    compMask.Set<byte>(yy - rect.Top, xx - rect.Left, 255);
                            }
                        }

                        Cv2.FindContours(compMask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                        if (contours != null && contours.Length > 0)
                        {
                            double maxArea = 0;
                            int maxIdx = -1;
                            for (int ci = 0; ci < contours.Length; ci++)
                            {
                                double area = Cv2.ContourArea(contours[ci]);
                                if (area > maxArea) { maxArea = area; maxIdx = ci; }
                            }

                            if (maxIdx >= 0)
                            {
                                var hull = Cv2.ConvexHull(contours[maxIdx]);
                                double hullArea = Math.Max(1.0, Cv2.ContourArea(hull));
                                double solidity = c.area / hullArea;
                                if (solidity <= minSolidity)
                                {
                                    toRemoveLabels.Add(c.label);
                                    continue;
                                }
                            }
                        }
                    }



                    double minD2 = double.MaxValue;
                    foreach (var br in bigBoxes)
                    {
                        double d2 = DistPointToRectSq(center, br);
                        if (d2 < minD2) minD2 = d2;

                        if (minD2 < radiusSq) break; // early exit
                    }

                    bool nearBig = minD2 < radiusSq; // keep strict '<' same as before

                    // --- 2. попадает ли компонент в текстовую полосу по horProj/textRows ---
                    bool onTextLine = false;
                    if (textRowFlags.Length > 0)
                    {
                        for (int ry = Math.Max(0, center.Y - rowCheckRange);
                             ry <= Math.Min(binRows - 1, center.Y + rowCheckRange);
                             ry++)
                        {
                            //if (textRows.Contains(ry)) { onTextLine = true; break; }
                            if (textRowFlags[ry]) { onTextLine = true; break; }
                        }
                    }

                    // ---------- ГЛАВНОЕ: разделение фон / текст ----------

                    bool inTextRegion = nearBig || onTextLine;

                    // 3А. Если НЕ текстовая зона → чистим агрессивно
                    if (!inTextRegion)
                    {
                        toRemoveLabels.Add(c.label);
                        continue;
                    }

                    // 3Б. Если текстовая зона → включаем аккуратные эвристики

                    bool squareLike = Math.Abs(rect.Width - rect.Height) <= Math.Max(1, rect.Height * squarenessTolerance);

                    bool partOfCluster = false;

                    if (buckets != null) // значит KeepClusters == true и индекс построен
                    {
                        int gx = center.X / cellW;
                        int gy = center.Y / cellH;

                        if (gx < 0) gx = 0; else if (gx >= gridW) gx = gridW - 1;
                        if (gy < 0) gy = 0; else if (gy >= gridH) gy = gridH - 1;

                        // Проверяем текущую клетку и 8 соседних (3x3)
                        for (int yy = Math.Max(0, gy - 1); yy <= Math.Min(gridH - 1, gy + 1) && !partOfCluster; yy++)
                        {
                            for (int xx = Math.Max(0, gx - 1); xx <= Math.Min(gridW - 1, gx + 1) && !partOfCluster; xx++)
                            {
                                var list = buckets[xx + yy * gridW];
                                for (int t = 0; t < list.Count; t++)
                                {
                                    int j = list[t];
                                    if (j == i) continue; // тот же компонент

                                    // сравнение по label (как у тебя было)
                                    if (smallComps[j].label == c.label) continue;

                                    var p2 = smallCenters[j];

                                    int dy = p2.Y - center.Y;
                                    int dx = p2.X - center.X;

                                    if ((uint)(dy + rowCheckRange) <= (uint)(2 * rowCheckRange) &&
                                        (uint)(dx + clusterHoriz) <= (uint)(2 * clusterHoriz))
                                    {
                                        partOfCluster = true;
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    else if (settings.KeepClusters)
                    {
                        // fallback (на случай если smallComps.Length <= 1 или индекс не построен)
                        // можно оставить пустым: partOfCluster останется false
                    }



                    // Более строгая защита: только в текстовой зоне
                    if (nearBig || (onTextLine && squareLike) || (onTextLine && partOfCluster))
                    {
                        toKeepLabels.Add(c.label);
                    }
                    else
                    {
                        toRemoveLabels.Add(c.label);
                    }
                }
                Debug.WriteLine(
                   $"Despeckle: comps={comps.Count}, small={smallComps.Length}, toRemove={toRemoveLabels.Count}"
                );

                // remove: build mask from labels and intersect with original black mask


                using var removeMask = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.All(0)); // will accumulate removed pixels


                if (toRemoveLabels.Count > 0)
                {
                    var removeLut = new bool[nLabels];
                    foreach (var lbl in toRemoveLabels)
                        if ((uint)lbl < (uint)nLabels)
                            removeLut[lbl] = true;

                    var po = new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = Environment.ProcessorCount
                    };

                    unsafe
                    {
                        int rows = labels.Rows;
                        int cols = labels.Cols;

                        int* labelsPtr = (int*)labels.DataPointer;
                        long labelsStep = labels.Step() / sizeof(int); // ints per row

                        byte* maskPtr = (byte*)removeMask.DataPointer;
                        long maskStep = removeMask.Step(); // bytes per row

                        Parallel.For(0, rows, po, y =>
                        {
                            int* lrow = labelsPtr + y * labelsStep;
                            byte* mrow = maskPtr + y * maskStep;

                            for (int x = 0; x < cols; x++)
                            {
                                int lbl = lrow[x];
                                if ((uint)lbl < (uint)removeLut.Length && removeLut[lbl])
                                    mrow[x] = 255;
                            }
                        });
                    }
                }




                using var intersect = new Mat();
                Cv2.BitwiseAnd(removeMask, textMask, intersect); // ensure only original black pixels removed

                int removedPixels = Cv2.CountNonZero(intersect);
                Debug.WriteLine($"Despeckle: comps={comps.Count}, small={smallComps.Length}, toRemove={toRemoveLabels.Count}, removedPixels={removedPixels}");


                // Apply removal to binary
                // Apply removal to binary (если нужен бинарный результат)
                Mat cleanedBinary = bin.Clone();
                cleanedBinary.SetTo(new Scalar(0), intersect); // speckles -> background (0)

                if (applyMaskToSource)
                {
                    token.ThrowIfCancellationRequested();
                    Mat outMat;

                    if (src.Channels() == 1)
                    {
                        //Mat srcGray = src.Type() == MatType.CV_8UC1 ? src.Clone() : new Mat();
                        Mat srcGray = new Mat();
                        if (src.Type() != MatType.CV_8UC1)
                        {
                            Cv2.CvtColor(src, srcGray,
                                src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
                            outMat = srcGray;
                        }
                        else
                        {
                            outMat = src.Clone();
                        }



                        if (debug)
                        {
                            // для отладки — красим спеклы в средне-серый, чтобы видно было
                            Mat maskToDraw = intersect;
                            using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
                            maskToDraw = intersect.Clone();
                            Cv2.Dilate(maskToDraw, maskToDraw, k, iterations: 2);
                            outMat.SetTo(new Scalar(128), maskToDraw);
                            maskToDraw.Dispose();
                        }
                        else
                        {
                            // боевой режим: спеклы -> белый фон
                            outMat.SetTo(new Scalar(255), intersect);
                        }
                    }
                    else if (src.Channels() == 3)
                    {

                        outMat = src.Clone();

                        // Для наглядности увеличим маску, если debug == true
                        Mat maskToDraw = intersect;
                        if (debug)
                        {
                            maskToDraw = intersect.Clone();
                            using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
                            Cv2.Dilate(maskToDraw, maskToDraw, k, iterations: 2);
                            outMat.SetTo(new Scalar(0, 0, 255), maskToDraw);
                        }
                        else
                        {
                            outMat.SetTo(new Scalar(255, 255, 255), intersect);
                        }



                        if (debug && maskToDraw != intersect)
                            maskToDraw.Dispose();
                    }
                    else // 4 channels
                    {
                        outMat = src.Clone();
                        Mat[] chs = Cv2.Split(outMat);
                        Mat alpha = chs.Length >= 4 ? chs[3].Clone() : null;
                        // пересобираем 4ch (RGB+Alpha)
                        Cv2.Merge(new[]
                        {
                        chs[0],
                        chs[1],
                        chs[2],
                        (alpha ?? new Mat(outMat.Size(), MatType.CV_8UC1, Scalar.All(255)))
                    }, outMat);

                        if (debug)
                        {
                            Mat maskToDraw = intersect.Clone();
                            using var k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
                            Cv2.Dilate(maskToDraw, maskToDraw, k, iterations: 2);
                            outMat.SetTo(new Scalar(0, 0, 255, 255), maskToDraw);
                            maskToDraw.Dispose();
                        }
                        else
                        {
                            // боевой режим: белый с полной альфой
                            outMat.SetTo(new Scalar(255, 255, 255, 255), intersect);
                        }

                        if (alpha != null) alpha.Dispose();
                        foreach (var m in chs) m.Dispose();
                    }

                    labelingMat.Dispose();

                    return outMat;
                }
                else
                {
                    labelingMat.Dispose();
                    var ret = cleanedBinary.Clone();
                    cleanedBinary.Dispose();
                    //if (localBinCreated) bin.Dispose();
                    return ret;
                }
            }
            finally
            {
                if (localBinCreated && bin != null && !bin.IsDisposed) bin.Dispose();
            }

        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Despeckler: cancelled");
            return null;
        }
        finally
        {
            var elapsed = DateTime.Now - startTime;
            Debug.WriteLine($"Despeckler: elapsed {elapsed.TotalMilliseconds} ms");
        }
    }

    public static Mat DespeckleEffective(
                                        CancellationToken token,
                                        Mat src,
                                        DespeckleSettings? settings = null,
                                        bool debug = false,
                                        bool inputIsBinary = false,
                                        bool applyMaskToSource = true)
    {
        var startTime = DateTime.Now;
        try
        {
            token.ThrowIfCancellationRequested();

            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

            static int ClampInt(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);
            static double ClampDouble(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

            settings ??= new DespeckleSettings();

            Mat bin = null!;
            bool localBinCreated = false;

            try
            {
                if (inputIsBinary)
                {
                    Mat grayBin = new Mat();
                    if (src.Channels() == 1)
                    {
                        src.CopyTo(grayBin);
                    }
                    else if (src.Channels() == 3)
                    {
                        Cv2.CvtColor(src, grayBin, ColorConversionCodes.BGR2GRAY);
                    }
                    else if (src.Channels() == 4)
                    {
                        Cv2.CvtColor(src, grayBin, ColorConversionCodes.BGRA2GRAY);
                    }
                    else
                    {
                        throw new ArgumentException("inputIsBinary == true requires 1, 3 or 4 channel image.");
                    }

                    token.ThrowIfCancellationRequested();
                    double white = Cv2.CountNonZero(grayBin);
                    double total = grayBin.Rows * grayBin.Cols;
                    if (white >= total / 2.0)
                        Cv2.BitwiseNot(grayBin, grayBin);
                    bin = grayBin;
                    localBinCreated = true;
                }
                else
                {
                    token.ThrowIfCancellationRequested();
                    Mat gray = new Mat();
                    if (src.Channels() == 1)
                    {
                        src.CopyTo(gray);
                    }
                    else if (src.Channels() == 3)
                    {
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                    }
                    else if (src.Channels() == 4)
                    {
                        Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                    }
                    else
                    {
                        throw new ArgumentException("Unsupported image format.");
                    }

                    Mat binLocal = new Mat();
                    int grayCols = gray.Cols;
                    if (grayCols > 0 && grayCols < 1200)
                    {
                        using var bg = new Mat();
                        Cv2.GaussianBlur(gray, bg, new Size(41, 41), 0);
                        using var corr = new Mat();
                        Cv2.Absdiff(gray, bg, corr);
                        using var denoised = new Mat();
                        Cv2.GaussianBlur(corr, denoised, new Size(3, 3), 0);
                        Cv2.Threshold(denoised, binLocal, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                        double whitePixels = Cv2.CountNonZero(binLocal);
                        double total = binLocal.Rows * binLocal.Cols;
                        if (whitePixels >= total / 2.0)
                            Cv2.BitwiseNot(binLocal, binLocal);
                    }
                    else
                    {
                        int blockSize = ClampInt(grayCols / 40, 11, 101) | 1;
                        int c = 14;
                        Cv2.AdaptiveThreshold(gray, binLocal, 255,
                            AdaptiveThresholdTypes.GaussianC,
                            ThresholdTypes.Binary,
                            blockSize, c);

                        double whitePixels = Cv2.CountNonZero(binLocal);
                        double total = binLocal.Rows * binLocal.Cols;
                        if (whitePixels >= total / 2.0)
                            Cv2.BitwiseNot(binLocal, binLocal);
                    }

                    gray.Dispose();
                    bin = binLocal;
                    localBinCreated = true;
                }

                if (settings.EnableDustRemoval)
                {
                    token.ThrowIfCancellationRequested();
                    int medianK = Math.Max(1, settings.DustMedianKsize);
                    if (medianK % 2 == 0) medianK += 1;

                    int openK = Math.Max(1, settings.DustOpenKernel);
                    if (openK % 2 == 0) openK += 1;

                    int openIter = Math.Max(1, settings.DustOpenIter);

                    var dustBin = bin.Clone();
                    if (medianK > 1)
                        Cv2.MedianBlur(dustBin, dustBin, medianK);

                    if (openK > 1)
                    {
                        using var dustKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(openK, openK));
                        Cv2.MorphologyEx(dustBin, dustBin, MorphTypes.Open, dustKernel, iterations: openIter);
                    }

                    if (localBinCreated)
                        bin.Dispose();
                    bin = dustBin;
                    localBinCreated = true;
                }

                token.ThrowIfCancellationRequested();
                using var textMask = new Mat();
                Cv2.InRange(bin, new Scalar(255), new Scalar(255), textMask);

                Mat labelingMat = bin.Clone();
                if (settings.UseDilateBeforeCC && settings.DilateIter > 0)
                {
                    token.ThrowIfCancellationRequested();
                    Mat k;
                    switch (settings.DilateKernel)
                    {
                        case "3x1": k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 1)); break;
                        case "3x3": k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)); break;
                        default: k = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(1, 3)); break;
                    }
                    var tmp = new Mat();
                    Cv2.Dilate(labelingMat, tmp, k, iterations: settings.DilateIter);
                    labelingMat.Dispose();
                    k.Dispose();
                    labelingMat = tmp;
                }

                using var labels = new Mat();
                using var stats = new Mat();
                using var centroids = new Mat();
                token.ThrowIfCancellationRequested();
                int nLabels = Cv2.ConnectedComponentsWithStats(labelingMat, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);

                if (nLabels <= 1)
                {
                    labelingMat.Dispose();
                    var retEmpty = applyMaskToSource ? src.Clone() : bin.Clone();
                    return retEmpty;
                }

                var heights = new List<int>(nLabels - 1);
                for (int lbl = 1; lbl < nLabels; lbl++)
                {
                    heights.Add(stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Height));
                }

                int medianHeight = 20;
                if (heights.Count > 0)
                {
                    var ordered = heights.Where(h => h >= 3).OrderBy(h => h).ToArray();
                    medianHeight = ordered.Length > 0 ? ordered[ordered.Length / 2] : 20;
                }

                int smallAreaThrPx = settings.SmallAreaRelative
                    ? Math.Max(1, (int)Math.Round(settings.SmallAreaMultiplier * medianHeight * medianHeight))
                    : Math.Max(1, settings.SmallAreaAbsolutePx);

                int maxDotHeight = Math.Max(1, (int)Math.Round(settings.MaxDotHeightFraction * medianHeight));
                double rowCheckRange = Math.Max(1.0, settings.ProximityRadiusFraction * medianHeight);
                double squarenessTolerance = ClampDouble(settings.SquarenessTolerance, 0.0, 1.0);

                int binRows = bin.Rows;
                int binCols = bin.Cols;

                using var projSum = new Mat();
                Cv2.Reduce(bin, projSum, dim: ReduceDimension.Column, rtype: ReduceTypes.Sum, dtype: MatType.CV_32S);
                var horProj = new int[binRows];
                for (int y = 0; y < binRows; y++)
                {
                    if ((y & 63) == 0) token.ThrowIfCancellationRequested();
                    horProj[y] = projSum.Get<int>(y, 0) / 255;
                }

                int projThr = Math.Max(10, binCols / 40);
                var textRowFlags = new bool[binRows];
                for (int y = 0; y < binRows; y++)
                    textRowFlags[y] = horProj[y] > projThr;

                var removeLut = new bool[nLabels];
                for (int lbl = 1; lbl < nLabels; lbl++)
                {
                    if ((lbl & 127) == 0) token.ThrowIfCancellationRequested();

                    int left = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Left);
                    int top = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Top);
                    int width = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Width);
                    int height = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Height);
                    int area = stats.Get<int>(lbl, (int)ConnectedComponentsTypes.Area);

                    if (width <= 0 || height <= 0)
                        continue;

                    bool isSmall = area < smallAreaThrPx || height <= maxDotHeight;
                    if (!isSmall)
                        continue;

                    int cy = top + height / 2;
                    bool onTextLine = false;
                    int r0 = Math.Max(0, (int)Math.Round(cy - rowCheckRange));
                    int r1 = Math.Min(binRows - 1, (int)Math.Round(cy + rowCheckRange));
                    for (int ry = r0; ry <= r1; ry++)
                    {
                        if (textRowFlags[ry]) { onTextLine = true; break; }
                    }

                    bool squareLike = Math.Abs(width - height) <= Math.Max(1, height * squarenessTolerance);
                    if (settings.KeepClusters && onTextLine && squareLike)
                        continue;

                    if (settings.EnableDustShapeFilter)
                    {
                        double minSolidity = ClampDouble(settings.DustMinSolidity, 0.05, 1.0);
                        double maxAspect = ClampDouble(settings.DustMaxAspectRatio, 1.0, 20.0);

                        double aspect = height <= 0 ? maxAspect : (double)width / height;
                        if (aspect < 1.0) aspect = 1.0 / aspect;
                        if (aspect >= maxAspect)
                        {
                            removeLut[lbl] = true;
                            continue;
                        }

                        using var compMask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
                        for (int yy = top; yy < top + height; yy++)
                        {
                            if ((yy & 31) == 0) token.ThrowIfCancellationRequested();
                            for (int xx = left; xx < left + width; xx++)
                            {
                                if (labels.Get<int>(yy, xx) == lbl)
                                    compMask.Set<byte>(yy - top, xx - left, 255);
                            }
                        }

                        Cv2.FindContours(compMask, out OpenCvSharp.Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                        if (contours != null && contours.Length > 0)
                        {
                            double maxArea = 0;
                            int maxIdx = -1;
                            for (int ci = 0; ci < contours.Length; ci++)
                            {
                                double cArea = Cv2.ContourArea(contours[ci]);
                                if (cArea > maxArea) { maxArea = cArea; maxIdx = ci; }
                            }

                            if (maxIdx >= 0)
                            {
                                var hull = Cv2.ConvexHull(contours[maxIdx]);
                                double hullArea = Math.Max(1.0, Cv2.ContourArea(hull));
                                double solidity = area / hullArea;
                                if (solidity <= minSolidity)
                                {
                                    removeLut[lbl] = true;
                                    continue;
                                }
                            }
                        }
                    }

                    removeLut[lbl] = true;
                }

                using var removeMask = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.All(0));
                var po = new ParallelOptions
                {
                    CancellationToken = token,
                    MaxDegreeOfParallelism = Environment.ProcessorCount
                };

                unsafe
                {
                    int rows = labels.Rows;
                    int cols = labels.Cols;

                    int* labelsPtr = (int*)labels.DataPointer;
                    long labelsStep = labels.Step() / sizeof(int);

                    byte* maskPtr = (byte*)removeMask.DataPointer;
                    long maskStep = removeMask.Step();

                    Parallel.For(0, rows, po, y =>
                    {
                        int* lrow = labelsPtr + y * labelsStep;
                        byte* mrow = maskPtr + y * maskStep;

                        for (int x = 0; x < cols; x++)
                        {
                            int lbl = lrow[x];
                            if ((uint)lbl < (uint)removeLut.Length && removeLut[lbl])
                                mrow[x] = 255;
                        }
                    });
                }

                using var intersect = new Mat();
                Cv2.BitwiseAnd(removeMask, textMask, intersect);

                Mat cleanedBinary = bin.Clone();
                cleanedBinary.SetTo(new Scalar(0), intersect);

                if (applyMaskToSource)
                {
                    token.ThrowIfCancellationRequested();
                    Mat outMat;

                    if (src.Channels() == 1)
                    {
                        Mat srcGray = new Mat();
                        if (src.Type() != MatType.CV_8UC1)
                        {
                            Cv2.CvtColor(src, srcGray,
                                src.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
                            outMat = srcGray;
                        }
                        else
                        {
                            outMat = src.Clone();
                            srcGray.Dispose();
                        }

                        outMat.SetTo(new Scalar(255), intersect);
                        return outMat;
                    }

                    Mat srcColor = new Mat();
                    if (src.Channels() == 3)
                        src.CopyTo(srcColor);
                    else if (src.Channels() == 4)
                        Cv2.CvtColor(src, srcColor, ColorConversionCodes.BGRA2BGR);
                    else
                        Cv2.CvtColor(src, srcColor, ColorConversionCodes.GRAY2BGR);

                    srcColor.SetTo(new Scalar(255, 255, 255), intersect);
                    return srcColor;
                }

                return cleanedBinary;
            }
            finally
            {
                if (localBinCreated && bin != null && !bin.IsDisposed)
                    bin.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
            Debug.WriteLine("Despeckler(Eff): cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Despeckler(Eff) failed: {ex}");
            return src.Clone();
        }
        finally
        {
            var elapsed = DateTime.Now - startTime;
            Debug.WriteLine($"Despeckler(Eff): elapsed {elapsed.TotalMilliseconds} ms");
        }
    }

}
