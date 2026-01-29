using OpenCvSharp;

namespace ImgViewer.Models
{
    internal enum InvertMethod
    {
        WholePage,
        ByMask
    }

    internal enum InvertObjectCount
    {
        Single,
        Auto
    }

    internal static class Inverter
    {
        public static Mat Apply(Mat src, InvertMethod method, InvertObjectCount objectCount, CancellationToken token)
        {
            if (src == null || src.Empty())
                return new Mat();

            return method switch
            {
                InvertMethod.ByMask => InvertByMask(src, objectCount, token),
                _ => InvertWhole(src)
            };
        }

        private static Mat InvertWhole(Mat src)
        {
            var dst = new Mat();
            Cv2.BitwiseNot(src, dst);
            return dst;
        }

        private static Mat InvertByMask(Mat src, InvertObjectCount objectCount, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            using var gray = ToGray8U(src);
            using var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);

            using var darkMask = new Mat();
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

            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            int count = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, centroids);
            if (count <= 1)
                return src.Clone();

            int imgArea = Math.Max(1, src.Width * src.Height);
            int minArea = Math.Max(5000, (int)Math.Round(imgArea * 0.02));

            using Mat componentMask = (objectCount == InvertObjectCount.Auto)
                ? new Mat(labels.Rows, labels.Cols, MatType.CV_8U, Scalar.All(0))
                : new Mat();

            if (objectCount == InvertObjectCount.Auto)
            {
                bool any = false;

                using var tmpMask = new Mat();
                for (int i = 1; i < count; i++)
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < minArea)
                        continue;

                    Cv2.Compare(labels, i, tmpMask, CmpType.EQ);
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
                for (int i = 1; i < count; i++)
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area > bestArea)
                    {
                        bestArea = area;
                        bestIdx = i;
                    }
                }

                if (bestIdx < 0 || bestArea < minArea)
                    return src.Clone();

                Cv2.Compare(labels, bestIdx, componentMask, CmpType.EQ);
            }

            using var filledMask = FillHoles(componentMask);

            int dilateSize = Math.Max(1, closeSize / 6);
            if (dilateSize % 2 == 0) dilateSize++;
            using var dilateKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilateSize, dilateSize));
            Cv2.Dilate(filledMask, filledMask, dilateKernel);

            var result = src.Clone();
            using var inverted = new Mat();
            Cv2.BitwiseNot(src, inverted);
            inverted.CopyTo(result, filledMask);
            return result;
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
