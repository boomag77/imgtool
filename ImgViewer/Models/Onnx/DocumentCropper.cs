using System;
using System.Linq;
using OpenCvSharp;
using Size = OpenCvSharp.Size;
using Point = OpenCvSharp.Point;

namespace ImgViewer.Models.Onnx
{
    internal static class DocumentCropper
    {
        /// <summary>
        /// Берёт исходный BGR src и бинарную маску (0/255),
        /// находит крупнейший контур и делает ОКНО (BoundingRect) БЕЗ поворота.
        /// Возвращает вырезанный документ и overlay с зелёным прямоугольником.
        /// </summary>
        public static Mat CropByMask(Mat src, Mat mask, out Mat debugOverlay)
        {
            if (src.Empty()) throw new ArgumentException("src is empty", nameof(src));
            if (mask.Empty()) throw new ArgumentException("mask is empty", nameof(mask));

            // 1) Чистка маски: закрываем дырки
            Mat maskClean = mask.Clone();
            Mat kernelClose = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
            Cv2.MorphologyEx(maskClean, maskClean, MorphTypes.Close, kernelClose);

            // 2) Немного "съедаем" маску внутрь, чтобы откусить тонкий бордер
            //Подбери размер ядра: 3x3, 5x5, 7x7 — чем больше, тем агрессивнее.
            Mat kernelErode = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
            Cv2.Erode(maskClean, maskClean, kernelErode, iterations: 1);

            // 3) Контуры
            Point[][] contours;
            HierarchyIndex[] hierarchy;
            Cv2.FindContours(
                image: maskClean,
                contours: out contours,
                hierarchy: out hierarchy,
                mode: RetrievalModes.External,
                method: ContourApproximationModes.ApproxSimple);

            debugOverlay = src.Clone();

            if (contours == null || contours.Length == 0)
                return src.Clone(); // fallback — ничего не нашли

            Point[] largest = contours
                .OrderByDescending(c => Cv2.ContourArea(c))
                .First();

            double area = Cv2.ContourArea(largest);
            if (area < src.Rows * src.Cols * 0.1)
                return src.Clone(); // слишком мало — считаем шумом

            // 4) Обычный axis-aligned bounding rect
            Rect bbox = Cv2.BoundingRect(largest);

            // 5) Дополнительно "поджимаем" прямоугольник внутрь,
            //    чтобы убрать тонкий бордер, но не резать текст.
            //    trimFraction = 0.01 → по 1% с каждой стороны.
            double trimFraction = 0; // регулируется: 0.005..0.03
            int trimX = (int)Math.Round(bbox.Width * trimFraction);
            int trimY = (int)Math.Round(bbox.Height * trimFraction);
            trimX = 0;
            trimY = 0;

            // следим, чтобы не ушли в минус
            int newX = bbox.X + trimX;
            int newY = bbox.Y + trimY;
            int newW = bbox.Width - 2 * trimX;
            int newH = bbox.Height - 2 * trimY;

            if (newW < 10 || newH < 10)
            {
                // слишком ужали — оставляем исходный bbox
                newX = bbox.X;
                newY = bbox.Y;
                newW = bbox.Width;
                newH = bbox.Height;
            }

            // подрезаем по границам кадра
            newX = Math.Max(0, newX);
            newY = Math.Max(0, newY);
            if (newX + newW > src.Cols) newW = src.Cols - newX;
            if (newY + newH > src.Rows) newH = src.Rows - newY;

            Rect tightBox = new Rect(newX, newY, newW, newH);

            // 6) Рисуем уже "поджатый" bbox на overlay
            Cv2.Rectangle(
                debugOverlay,
                tightBox,
                new Scalar(0, 255, 0),
                2);

            // 7) Кроп без поворота
            Mat cropped = new Mat(src, tightBox);
            return cropped.Clone();

            //var chosenBg = EstimatePageFillColor(src, mask);

            // fill instead of crop
            //Mat filled = new Mat(src.Rows, src.Cols, src.Type(), chosenBg);
            //Mat roi = new Mat(filled, tightBox);
            //src[tightBox].CopyTo(roi);
            //return filled.Clone();

            // Telea inpaint instead of filled
            //Mat inpainted = new Mat();
            //Cv2.BitwiseNot(maskClean, maskClean); // invert mask for inpainting
            //Cv2.Inpaint(filled, maskClean, inpainted, 5, InpaintMethod.Telea);
            //return inpainted.Clone();

        }

        private static Scalar EstimatePageFillColor(
                                        Mat src,
                                        Mat pageMask,                  // 8U, 0/255: 255 = страница без бордюров
                                        int gridSize = 5,              // количество точек по одной стороне сетки
                                        double innerMarginFraction = 0.10, // какую долю от краёв откусить (0.1 = 10%)
                                        int patchRadius = 20,          // радиус патча (окно (2R+1)x(2R+1))
                                        double maxPatchStdDev = 12.0,  // максимум σ по патчу (гомогенность)
                                        double minBrightness = 0.70,   // минимальная яркость (0..1) для фона
                                        double minMaskCoverage = 0.30  // минимальная доля покрытых маской пикселей в патче
        )
        {
            if (src == null || src.Empty())
                throw new ArgumentException("src is null or empty", nameof(src));

            if (pageMask == null || pageMask.Empty())
                throw new ArgumentException("pageMask is null or empty", nameof(pageMask));

            if (pageMask.Type() != MatType.CV_8UC1)
                throw new ArgumentException("pageMask must be CV_8UC1", nameof(pageMask));

            if (pageMask.Rows != src.Rows || pageMask.Cols != src.Cols)
                throw new ArgumentException("pageMask size must match src size", nameof(pageMask));

            // --- привести к BGR (3 канала) ---
            Mat color;
            if (src.Channels() == 3)
            {
                color = src;
            }
            else if (src.Channels() == 4)
            {
                color = new Mat();
                Cv2.CvtColor(src, color, ColorConversionCodes.BGRA2BGR);
            }
            else
            {
                color = new Mat();
                Cv2.CvtColor(src, color, ColorConversionCodes.GRAY2BGR);
            }

            int rows = color.Rows;
            int cols = color.Cols;
            if (rows < 2 || cols < 2)
                return new Scalar(255, 255, 255);

            // --- центральный ROI (без внешних процентов полей) ---
            int marginX = (int)Math.Round(cols * innerMarginFraction);
            int marginY = (int)Math.Round(rows * innerMarginFraction);

            int x0 = marginX;
            int y0 = marginY;
            int x1 = cols - marginX - 1;
            int y1 = rows - marginY - 1;

            // если откусили слишком много — берём весь кадр
            if (x1 <= x0 || y1 <= y0)
            {
                x0 = 0;
                y0 = 0;
                x1 = cols - 1;
                y1 = rows - 1;
            }

            int roiW = x1 - x0 + 1;
            int roiH = y1 - y0 + 1;

            var goodSamples = new List<Vec3d>();

            // --- сетка патчей по центральному ROI ---
            for (int gy = 0; gy < gridSize; gy++)
            {
                for (int gx = 0; gx < gridSize; gx++)
                {
                    double cxF = x0 + (gx + 0.5) * roiW / gridSize;
                    double cyF = y0 + (gy + 0.5) * roiH / gridSize;

                    int cx = (int)Math.Round(cxF);
                    int cy = (int)Math.Round(cyF);

                    cx = Math.Max(x0, Math.Min(cx, x1));
                    cy = Math.Max(y0, Math.Min(cy, y1));

                    int xStart = Math.Max(cx - patchRadius, x0);
                    int yStart = Math.Max(cy - patchRadius, y0);
                    int xEnd = Math.Min(cx + patchRadius, x1);
                    int yEnd = Math.Min(cy + patchRadius, y1);

                    int pw = xEnd - xStart + 1;
                    int ph = yEnd - yStart + 1;
                    if (pw <= 1 || ph <= 1)
                        continue;

                    var patchRect = new Rect(xStart, yStart, pw, ph);

                    using var patch = new Mat(color, patchRect);
                    using var patchMask = new Mat(pageMask, patchRect);

                    // сколько вообще валидных пикселей страницы в этом патче
                    int nonZero = Cv2.CountNonZero(patchMask);
                    int total = pw * ph;

                    if (nonZero < total * minMaskCoverage)
                        continue; // в патче слишком много бордюров/фона вне страницы

                    // статистика только по пикселям, где mask=255
                    Cv2.MeanStdDev(patch, out Scalar mean, out Scalar stddev, patchMask);

                    double b = mean.Val0;
                    double g = mean.Val1;
                    double r = mean.Val2;

                    double brightness = 0.114 * b + 0.587 * g + 0.299 * r; // 0..255
                    double sigma = (stddev.Val0 + stddev.Val1 + stddev.Val2) / 3.0;

                    if (brightness < minBrightness * 255.0)
                        continue; // слишком темно → текст/шум

                    if (sigma > maxPatchStdDev)
                        continue; // слишком неоднородно → текст/линии

                    goodSamples.Add(new Vec3d(b, g, r));
                }
            }

            if (goodSamples.Count > 0)
            {
                double sumB = 0, sumG = 0, sumR = 0;
                foreach (var v in goodSamples)
                {
                    sumB += v.Item0;
                    sumG += v.Item1;
                    sumR += v.Item2;
                }

                int n = goodSamples.Count;
                return new Scalar(sumB / n, sumG / n, sumR / n); // BGR
            }
            else
            {
                // fallback: средний цвет центрального ROI по маске страницы
                using var innerColor = new Mat(color, new Rect(x0, y0, roiW, roiH));
                using var innerMask = new Mat(pageMask, new Rect(x0, y0, roiW, roiH));

                Cv2.MeanStdDev(innerColor, out Scalar meanAll, out _, innerMask);
                return new Scalar(meanAll.Val0, meanAll.Val1, meanAll.Val2);
            }
        }

    }
}
