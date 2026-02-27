using OpenCvSharp;

namespace ImgViewer.Models
{
    internal enum InvertMethod
    {
        WholePage,
        ByMask,
        ByRectMask,
        Hybrid
    }

    internal enum InvertObjectCount
    {
        Single,
        Auto
    }

    internal enum RectPaddingMode
    {
        Auto,
        Manual
    }

    internal static class Inverter
    {
        public static Mat Apply(Mat src,
                                InvertMethod method,
                                InvertObjectCount objectCount,
                                RectPaddingMode rectPaddingMode,
                                int rectPadLeft,
                                int rectPadRight,
                                int rectPadTop,
                                int rectPadBottom,
                                double rectAutoTrimSensitivity,
                                int inpaintRadiusPx,
                                CancellationToken token)
        {
            if (src == null || src.Empty())
                return new Mat();

            return method switch
            {
                InvertMethod.ByMask => InvertByMask(src, objectCount, inpaintRadiusPx, token),
                InvertMethod.ByRectMask => InvertByRectMask(src, objectCount, rectPaddingMode, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, rectAutoTrimSensitivity, inpaintRadiusPx, token),
                InvertMethod.Hybrid => InvertHybrid(src, objectCount, rectPaddingMode, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, rectAutoTrimSensitivity, inpaintRadiusPx, token),
                _ => InvertWhole(src)
            };
        }

        private static Mat InvertWhole(Mat src)
        {
            var dst = new Mat();
            Cv2.BitwiseNot(src, dst);
            return dst;
        }

        private static Mat InvertByMask(Mat src, InvertObjectCount objectCount, int inpaintRadiusPx, CancellationToken token)
        {
            using var ctx = BuildDarkMaskContext(src, token);
            if (ctx.Count <= 1)
                return src.Clone();

            using Mat componentMask = (objectCount == InvertObjectCount.Auto)
                ? new Mat(ctx.Labels.Rows, ctx.Labels.Cols, MatType.CV_8U, Scalar.All(0))
                : new Mat();

            if (objectCount == InvertObjectCount.Auto)
            {
                bool any = false;

                using var tmpMask = new Mat();
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < ctx.MinArea)
                        continue;

                    Cv2.Compare(ctx.Labels, i, tmpMask, CmpTypes.EQ);
                    Cv2.BitwiseOr(componentMask, tmpMask, componentMask);
                    any = true;
                }

                if (!any)
                {
                    return src.Clone();
                }
            }
            else
            {
                int bestIdx = -1;
                int bestArea = 0;
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestArea < ctx.MinArea)
                    return src.Clone();

                Cv2.Compare(ctx.Labels, bestIdx, componentMask, CmpTypes.EQ);
            }

            using var filledMask = FillHoles(componentMask);

            int dilateSize = Math.Max(1, ctx.CloseSize / 6);
            if (dilateSize % 2 == 0) dilateSize++;
            using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilateSize, dilateSize));
            Cv2.Dilate(filledMask, filledMask, dilateKernel);

            var result = src.Clone();
            using var inverted = new Mat();
            Cv2.BitwiseNot(src, inverted);
            inverted.CopyTo(result, filledMask);
            if (inpaintRadiusPx > 0)
            {
                int band = Math.Max(1, inpaintRadiusPx);
                int k = band * 2 + 1;
                using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                using var dil = new Mat();
                using var ero = new Mat();
                Cv2.Dilate(filledMask, dil, edgeKernel);
                Cv2.Erode(filledMask, ero, edgeKernel);
                using var edgeMask = new Mat();
                Cv2.Subtract(dil, ero, edgeMask);
                Cv2.Inpaint(result, edgeMask, result, band, InpaintTypes.Telea);
            }
            return result;
        }

        private static Mat InvertByRectMask(Mat src,
                                            InvertObjectCount objectCount,
                                            RectPaddingMode rectPaddingMode,
                                            int rectPadLeft,
                                            int rectPadRight,
                                            int rectPadTop,
                                            int rectPadBottom,
                                            double rectAutoTrimSensitivity,
                                            int inpaintRadiusPx,
                                            CancellationToken token)
        {
            using var ctx = BuildDarkMaskContext(src, token);
            if (ctx.Count <= 1)
                return src.Clone();

            using var rectMask = new Mat(src.Rows, src.Cols, MatType.CV_8U, Scalar.All(0));
            using var tmpMask = new Mat();

            if (objectCount == InvertObjectCount.Auto)
            {
                bool any = false;
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < ctx.MinArea)
                        continue;

                    var rect = RectFromStats(ctx.Stats, i, src.Width, src.Height);
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;
                    if (rectPaddingMode == RectPaddingMode.Manual)
                    {
                        rect = ApplyRectPadding(rect, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, src.Width, src.Height);
                    }
                    else
                    {
                        Cv2.Compare(ctx.Labels, i, tmpMask, CmpTypes.EQ);
                        rect = AutoTrimRect(tmpMask, rect, rectAutoTrimSensitivity);
                    }
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;
                    Cv2.Rectangle(rectMask, rect, Scalar.All(255), thickness: -1);
                    any = true;
                }

                if (!any)
                    return src.Clone();
            }
            else
            {
                int bestIdx = -1;
                int bestArea = 0;
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestArea < ctx.MinArea)
                    return src.Clone();

                var rect = RectFromStats(ctx.Stats, bestIdx, src.Width, src.Height);
                if (rect.Width <= 0 || rect.Height <= 0)
                    return src.Clone();
                if (rectPaddingMode == RectPaddingMode.Manual)
                {
                    rect = ApplyRectPadding(rect, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, src.Width, src.Height);
                }
                else
                {
                    Cv2.Compare(ctx.Labels, bestIdx, tmpMask, CmpTypes.EQ);
                    rect = AutoTrimRect(tmpMask, rect, rectAutoTrimSensitivity);
                }
                if (rect.Width <= 0 || rect.Height <= 0)
                    return src.Clone();
                Cv2.Rectangle(rectMask, rect, Scalar.All(255), thickness: -1);
            }

            int dilateSize = Math.Max(1, ctx.CloseSize / 6);
            if (dilateSize % 2 == 0) dilateSize++;
            using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilateSize, dilateSize));
            Cv2.Dilate(rectMask, rectMask, dilateKernel);

            var result = src.Clone();
            using var inverted = new Mat();
            Cv2.BitwiseNot(src, inverted);
            inverted.CopyTo(result, rectMask);
            if (inpaintRadiusPx > 0)
            {
                int band = Math.Max(1, inpaintRadiusPx);
                int k = band * 2 + 1;
                using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                using var dil = new Mat();
                using var ero = new Mat();
                Cv2.Dilate(rectMask, dil, edgeKernel);
                Cv2.Erode(rectMask, ero, edgeKernel);
                using var edgeMask = new Mat();
                Cv2.Subtract(dil, ero, edgeMask);
                Cv2.Inpaint(result, edgeMask, result, band, InpaintTypes.Telea);
            }

            return result;
        }

        private static Mat InvertHybrid(Mat src,
                                        InvertObjectCount objectCount,
                                        RectPaddingMode rectPaddingMode,
                                        int rectPadLeft,
                                        int rectPadRight,
                                        int rectPadTop,
                                        int rectPadBottom,
                                        double rectAutoTrimSensitivity,
                                        int inpaintRadiusPx,
                                        CancellationToken token)
        {
            using var ctx = BuildDarkMaskContext(src, token);
            if (ctx.Count <= 1)
                return src.Clone();

            using var rectMask = new Mat(src.Rows, src.Cols, MatType.CV_8U, Scalar.All(0));
            using var componentMask = new Mat(src.Rows, src.Cols, MatType.CV_8U, Scalar.All(0));
            using var tmpMask = new Mat();

            if (objectCount == InvertObjectCount.Auto)
            {
                bool any = false;
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < ctx.MinArea)
                        continue;

                    Cv2.Compare(ctx.Labels, i, tmpMask, CmpTypes.EQ);
                    Cv2.BitwiseOr(componentMask, tmpMask, componentMask);

                    var rect = RectFromStats(ctx.Stats, i, src.Width, src.Height);
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;
                    if (rectPaddingMode == RectPaddingMode.Manual)
                    {
                        rect = ApplyRectPadding(rect, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, src.Width, src.Height);
                    }
                    else
                    {
                        rect = AutoTrimRect(tmpMask, rect, rectAutoTrimSensitivity);
                    }
                    if (rect.Width <= 0 || rect.Height <= 0)
                        continue;
                    Cv2.Rectangle(rectMask, rect, Scalar.All(255), thickness: -1);
                    any = true;
                }

                if (!any)
                    return src.Clone();
            }
            else
            {
                int bestIdx = -1;
                int bestArea = 0;
                for (int i = 1; i < ctx.Count; i++)
                {
                    int area = ctx.Stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestArea < ctx.MinArea)
                    return src.Clone();

                Cv2.Compare(ctx.Labels, bestIdx, tmpMask, CmpTypes.EQ);
                Cv2.BitwiseOr(componentMask, tmpMask, componentMask);

                var rect = RectFromStats(ctx.Stats, bestIdx, src.Width, src.Height);
                if (rect.Width <= 0 || rect.Height <= 0)
                    return src.Clone();
                if (rectPaddingMode == RectPaddingMode.Manual)
                {
                    rect = ApplyRectPadding(rect, rectPadLeft, rectPadRight, rectPadTop, rectPadBottom, src.Width, src.Height);
                }
                else
                {
                    rect = AutoTrimRect(tmpMask, rect, rectAutoTrimSensitivity);
                }
                if (rect.Width <= 0 || rect.Height <= 0)
                    return src.Clone();
                Cv2.Rectangle(rectMask, rect, Scalar.All(255), thickness: -1);
            }

            using var filledMask = FillHoles(componentMask);
            Cv2.BitwiseAnd(filledMask, rectMask, filledMask);

            int dilateSize = Math.Max(1, ctx.CloseSize / 6);
            if (dilateSize % 2 == 0) dilateSize++;
            using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilateSize, dilateSize));
            Cv2.Dilate(filledMask, filledMask, dilateKernel);

            var result = src.Clone();
            using var inverted = new Mat();
            Cv2.BitwiseNot(src, inverted);
            inverted.CopyTo(result, filledMask);
            if (inpaintRadiusPx > 0)
            {
                int band = Math.Max(1, inpaintRadiusPx);
                int k = band * 2 + 1;
                using var edgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(k, k));
                using var dil = new Mat();
                using var ero = new Mat();
                Cv2.Dilate(filledMask, dil, edgeKernel);
                Cv2.Erode(filledMask, ero, edgeKernel);
                using var edgeMask = new Mat();
                Cv2.Subtract(dil, ero, edgeMask);
                Cv2.Inpaint(result, edgeMask, result, band, InpaintTypes.Telea);
            }

            return result;
        }

        private sealed class DarkMaskContext : IDisposable
        {
            private readonly Mat _gray;
            private readonly Mat _blur;
            private readonly Mat _darkMask;
            private readonly Mat _centroids;

            public DarkMaskContext(Mat gray,
                                   Mat blur,
                                   Mat darkMask,
                                   Mat labels,
                                   Mat stats,
                                   Mat centroids,
                                   int count,
                                   int closeSize,
                                   int openSize,
                                   int minArea)
            {
                _gray = gray;
                _blur = blur;
                _darkMask = darkMask;
                Labels = labels;
                Stats = stats;
                _centroids = centroids;
                Count = count;
                CloseSize = closeSize;
                OpenSize = openSize;
                MinArea = minArea;
            }

            public Mat Labels { get; }
            public Mat Stats { get; }
            public int Count { get; }
            public int CloseSize { get; }
            public int OpenSize { get; }
            public int MinArea { get; }

            public void Dispose()
            {
                _gray.Dispose();
                _blur.Dispose();
                _darkMask.Dispose();
                Labels.Dispose();
                Stats.Dispose();
                _centroids.Dispose();
            }
        }

        private static DarkMaskContext BuildDarkMaskContext(Mat src, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            var gray = ToGray8U(src);
            var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

            var darkMask = new Mat();
            Cv2.Threshold(blur, darkMask, 0, 255, ThresholdTypes.BinaryInv | ThresholdTypes.Otsu);

            int minSide = Math.Max(1, Math.Min(src.Width, src.Height));
            int closeSize = Math.Max(7, minSide / 60);
            if (closeSize % 2 == 0) closeSize++;

            int openSize = Math.Max(3, closeSize / 3);
            if (openSize % 2 == 0) openSize++;

            using var closeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(closeSize, closeSize));
            Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Close, closeKernel);

            using var openKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(openSize, openSize));
            Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, openKernel);

            var labels = new Mat();
            var stats = new Mat();
            var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, centroids);

            int imgArea = Math.Max(1, src.Width * src.Height);
            int minArea = Math.Max(5000, (int)Math.Round(imgArea * 0.02));

            return new DarkMaskContext(gray, blur, darkMask, labels, stats, centroids, count, closeSize, openSize, minArea);
        }

        private static Rect RectFromStats(Mat stats, int index, int maxWidth, int maxHeight)
        {
            int left = stats.Get<int>(index, (int)ConnectedComponentsTypes.Left);
            int top = stats.Get<int>(index, (int)ConnectedComponentsTypes.Top);
            int width = stats.Get<int>(index, (int)ConnectedComponentsTypes.Width);
            int height = stats.Get<int>(index, (int)ConnectedComponentsTypes.Height);

            if (left < 0) { width += left; left = 0; }
            if (top < 0) { height += top; top = 0; }
            if (left + width > maxWidth) width = maxWidth - left;
            if (top + height > maxHeight) height = maxHeight - top;

            if (width < 1 || height < 1)
                return new Rect(0, 0, 0, 0);

            return new Rect(left, top, width, height);
        }

        private static Rect ApplyRectPadding(Rect rect, int padLeft, int padRight, int padTop, int padBottom, int maxWidth, int maxHeight)
        {
            int left = rect.Left - padLeft;
            int top = rect.Top - padTop;
            int right = rect.Right + padRight;
            int bottom = rect.Bottom + padBottom;

            if (left < 0) left = 0;
            if (top < 0) top = 0;
            if (right > maxWidth) right = maxWidth;
            if (bottom > maxHeight) bottom = maxHeight;

            int width = right - left;
            int height = bottom - top;
            if (width < 1 || height < 1)
                return new Rect(0, 0, 0, 0);

            return new Rect(left, top, width, height);
        }

        private static Rect AutoTrimRect(Mat componentMask, Rect rect, double densityFrac)
        {
            if (double.IsNaN(densityFrac) || densityFrac <= 0)
                densityFrac = 0.03;

            using var roi = new Mat(componentMask, rect);
            using var colSum = new Mat();
            using var rowSum = new Mat();
            Cv2.Reduce(roi, colSum, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            Cv2.Reduce(roi, rowSum, ReduceDimension.Column, ReduceTypes.Sum, MatType.CV_32S);

            int h = roi.Rows;
            int w = roi.Cols;
            if (h <= 0 || w <= 0)
                return rect;

            int colThresh = Math.Max(1, (int)Math.Round(h * 255.0 * densityFrac));
            int rowThresh = Math.Max(1, (int)Math.Round(w * 255.0 * densityFrac));

            int left = 0;
            while (left < w && colSum.At<int>(0, left) < colThresh) left++;
            int right = w - 1;
            while (right >= left && colSum.At<int>(0, right) < colThresh) right--;

            int top = 0;
            while (top < h && rowSum.At<int>(top, 0) < rowThresh) top++;
            int bottom = h - 1;
            while (bottom >= top && rowSum.At<int>(bottom, 0) < rowThresh) bottom--;

            if (left >= right || top >= bottom)
                return rect;

            return new Rect(rect.X + left, rect.Y + top, right - left + 1, bottom - top + 1);
        }

        private static Mat ToGray8U(Mat src)
        {
            if (src.Channels() == 1 && src.Depth() == MatType.CV_8U)
                return src.Clone();

            var gray = new Mat();
            if (src.Channels() == 3)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            else if (src.Channels() == 4)
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
            else
                src.ConvertTo(gray, MatType.CV_8U);

            if (gray.Depth() != MatType.CV_8U)
                gray.ConvertTo(gray, MatType.CV_8U);

            return gray;
        }

        private static Mat FillHoles(Mat mask)
        {
            using var padded = new Mat();
            Cv2.CopyMakeBorder(mask, padded, 1, 1, 1, 1, BorderTypes.Constant, Scalar.All(0));
            Cv2.FloodFill(padded, new Point(0, 0), Scalar.All(255));

            using var floodFill = new Mat(padded, new Rect(1, 1, mask.Width, mask.Height));
            using var floodInv = new Mat();
            Cv2.BitwiseNot(floodFill, floodInv);

            var filled = new Mat();
            Cv2.BitwiseOr(mask, floodInv, filled);
            return filled;
        }
    }
}
