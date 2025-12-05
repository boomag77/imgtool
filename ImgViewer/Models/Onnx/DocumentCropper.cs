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
            //Mat cropped = new Mat(src, tightBox);
            //return cropped.Clone();

            int rows = src.Rows, cols = src.Cols;
            int thr = 10;
            var chosenBg = new Scalar(255, 255, 255); // default white background
            int cornerSize = Math.Max(8, Math.Min(32, Math.Min(rows, cols) / 30));
            double sb = 0, sg = 0, sr = 0; int cnt = 0;
            var rects = new[]
            {
                        new Rect(0,0,cornerSize,cornerSize),
                        new Rect(Math.Max(0,cols-cornerSize),0,cornerSize,cornerSize),
                        new Rect(0,Math.Max(0,rows-cornerSize),cornerSize,cornerSize),
                        new Rect(Math.Max(0,cols-cornerSize), Math.Max(0,rows-cornerSize), cornerSize, cornerSize)
                    };
            foreach (var r in rects)
            {
                
                if (r.Width <= 0 || r.Height <= 0) continue;
                using var patch = new Mat(src, r);
                var mean = Cv2.Mean(patch);
                double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                if (brightness > thr * 1.0) { sb += mean.Val0; sg += mean.Val1; sr += mean.Val2; cnt++; }
            }
            if (cnt > 0) chosenBg = new Scalar(sb / cnt, sg / cnt, sr / cnt);

            // fill instead of crop
            Mat filled = new Mat(src.Rows, src.Cols, src.Type(), chosenBg);
            Mat roi = new Mat(filled, tightBox);
            src[tightBox].CopyTo(roi);
            return filled.Clone();

            // Telea inpaint instead of filled
            //Mat inpainted = new Mat();
            //Cv2.BitwiseNot(maskClean, maskClean); // invert mask for inpainting
            //Cv2.Inpaint(src, maskClean, inpainted, 5, InpaintMethod.Telea);
            //return inpainted.Clone();

        }

    }
}
