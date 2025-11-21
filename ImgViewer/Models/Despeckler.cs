using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models
{
    public class Despeckler
    {
        public static Mat DespeckleApplyToSource(
                                            CancellationToken token,
                                            Mat src,
                                            DespeckleSettings? settings = null,
                                            bool debug = false,
                                            bool inputIsBinary = false,
                                            bool applyMaskToSource = true)    // <-- new parameter
        {
            token.ThrowIfCancellationRequested();
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

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
                    var kernelBg = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(approxBgKernel | 1, approxBgKernel | 1));
                    Mat bg = new Mat();
                    Cv2.MorphologyEx(gray, bg, MorphTypes.Open, kernelBg);
                    kernelBg.Dispose();

                    token.ThrowIfCancellationRequested();
                    // Normalize by background
                    Mat corr = new Mat();
                    Cv2.Subtract(gray, bg, corr);
                    Cv2.Normalize(corr, corr, 0, 255, NormTypes.MinMax);
                    corr.ConvertTo(corr, MatType.CV_8UC1);

                    // Light denoise
                    Mat denoised = new Mat();
                    Cv2.MedianBlur(corr, denoised, 3);

                    // Decide Otsu vs Adaptive
                    Cv2.MinMaxLoc(bg, out double bgMin, out double bgMax);
                    double bgRange = bgMax - bgMin;

                    Mat binLocal = new Mat();
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
                        token.ThrowIfCancellationRequested();
                        int blockSize = ClampInt(grayCols / 40, 11, 101) | 1;
                        int C = 8;
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

                token.ThrowIfCancellationRequested();
                // Mask of original text pixels: 255 where bin == 255
                using var textMask = new Mat();
                Cv2.InRange(bin, new Scalar(255), new Scalar(255), textMask); // 0 where text==255

                // Prepare labelingMat (optionally dilate to merge touching dots)
                Mat labelingMat = bin.Clone();
                if (settings.UseDilateBeforeCC && settings.DilateIter > 0)
                {
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
                    if (localBinCreated) bin.Dispose();
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

                int binRows = bin.Rows, binCols = bin.Cols;
                var horProj = new int[binRows];
                for (int y = 0; y < binRows; y++)
                    horProj[y] = Cv2.CountNonZero(bin.Row(y)); // text px per row
                int projThr = Math.Max(1, binCols / 100);
                var textRows = new HashSet<int>(
                    Enumerable.Range(0, binRows).Where(y => horProj[y] > projThr)
                );

                Point Center(Rect r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

                var bigBoxes = comps.Where(c => c.bbox.Height >= medianHeight * 0.6 || c.area > smallAreaThrPx * 4)
                                    .Select(c => c.bbox).ToArray();

                static double DistPointToRect(Point p, Rect r)
                {
                    int dx = Math.Max(Math.Max(r.Left - p.X, 0), p.X - r.Right);
                    int dy = Math.Max(Math.Max(r.Top - p.Y, 0), p.Y - r.Bottom);
                    return Math.Sqrt(dx * dx + dy * dy);
                }

                var smallComps = comps.Where(c => c.area < smallAreaThrPx || c.bbox.Height <= maxDotHeight).ToArray();

                var toRemoveLabels = new List<int>();
                var toKeepLabels = new HashSet<int>();




                int rowCheckRange = Math.Max(1, medianHeight / 3);
                int clusterHoriz = Math.Max(3, (int)(medianHeight * 0.6));

                foreach (var c in smallComps)
                {
                    token.ThrowIfCancellationRequested();
                    //var rect = c.bbox;
                    //var center = Center(rect);

                    //double minDistToBig = double.MaxValue;
                    //foreach (var br in bigBoxes)
                    //{
                    //    double d = DistPointToRect(center, br);
                    //    if (d < minDistToBig) minDistToBig = d;
                    //}
                    //bool nearBig = minDistToBig < proximityRadius;

                    //bool onTextLine = false;
                    //for (int ry = Math.Max(0, center.Y - rowCheckRange); ry <= Math.Min(binRows - 1, center.Y + rowCheckRange); ry++)
                    //{
                    //    if (textRows.Contains(ry)) { onTextLine = true; break; }
                    //}

                    //bool squareLike = Math.Abs(rect.Width - rect.Height) <= Math.Max(1, rect.Height * squarenessTolerance);

                    //bool partOfCluster = false;
                    //if (settings.KeepClusters)
                    //{
                    //    foreach (var c2 in smallComps)
                    //    {
                    //        if (c2.label == c.label) continue;
                    //        if (Math.Abs(Center(c2.bbox).Y - center.Y) <= rowCheckRange &&
                    //            Math.Abs(Center(c2.bbox).X - center.X) <= clusterHoriz)
                    //        {
                    //            partOfCluster = true;
                    //            break;
                    //        }
                    //    }
                    //}

                    //if (nearBig || (onTextLine && squareLike) || partOfCluster)
                    //{
                    //    toKeepLabels.Add(c.label);
                    //    continue;
                    //}

                    toRemoveLabels.Add(c.label);
                }
                Debug.WriteLine(
                   $"Despeckle: comps={comps.Count}, small={smallComps.Length}, toRemove={toRemoveLabels.Count}"
                );

                // remove: build mask from labels and intersect with original black mask
                using var removeMask = new Mat(bin.Size(), MatType.CV_8UC1, Scalar.All(0)); // will accumulate removed pixels
                foreach (int lbl in toRemoveLabels)
                {
                    token.ThrowIfCancellationRequested();
                    using var m = new Mat();
                    Cv2.InRange(labels, new Scalar(lbl), new Scalar(lbl), m); // 255 where label==lbl
                    Cv2.BitwiseOr(removeMask, m, removeMask);
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
                        // отбеливаем именно speckles
                        //outMat.SetTo(new Scalar(0), intersect);
                        //if (removedPixels > 0)
                        //    outMat.SetTo(new Scalar(0));
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
                        } else
                        {
                            outMat.SetTo(new Scalar(255, 255, 255), intersect);
                        }



                        if (debug && maskToDraw != intersect)
                            maskToDraw.Dispose();

                        //outMat = src.Clone();
                        ////if (removedPixels > 0)
                        ////    outMat.SetTo(new Scalar(0, 0, 255)); // ВЕСЬ кадр в красный

                        //outMat.SetTo(new Scalar(0, 0, 255), intersect);
                    }
                    else // 4 channels
                    {
                        outMat = src.Clone();
                        Mat[] chs = Cv2.Split(outMat);
                        Mat alpha = chs.Length >= 4 ? chs[3].Clone() : null;

                        token.ThrowIfCancellationRequested();
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
                    removeMask.Dispose();
                    intersect.Dispose();
                    textMask.Dispose();
                    if (localBinCreated) bin.Dispose();

                    return outMat;
                }
                else
                {
                    labelingMat.Dispose();
                    removeMask.Dispose();
                    intersect.Dispose();
                    textMask.Dispose();
                    var ret = cleanedBinary.Clone();
                    cleanedBinary.Dispose();
                    if (localBinCreated) bin.Dispose();
                    return ret;
                }
            }
            finally
            {
                if (localBinCreated && bin != null && !bin.IsDisposed) bin.Dispose();
            }
        }

    }
}
