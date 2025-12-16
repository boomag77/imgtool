using OpenCvSharp;
using OpenCvSharp.XPhoto;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models
{
    public enum LineOrientation
    {
        Horizontal,
        Vertical,
        Both
    }
    public struct LineColorRGB
    {
        int Red;
        int Green;
        int Blue;
    }

    public class LinesRemover
    {


        public static Mat RemoveScannerHorizontalStripes(
    Mat src,
    double sensitivityK,               // "чувствительность", обычно 2.0–3.5
    int smoothWindow,                  // окно сглаживания профиля, напр. 15–31
    int expandRows,                    // на сколько строк расширять найденные полосы
    out Mat stripesMask,               // маска горизонтальных полос
    bool debugWriteIntermediate = false,
    string debugOutputPath = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty())
            {
                stripesMask = new Mat();
                return src.Clone();
            }

            // 1) Приводим к BGR, потом в grayscale
            Mat bgr;
            if (src.Channels() == 1)
            {
                bgr = new Mat();
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                bgr = src.Clone();
            }

            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

            int rows = gray.Rows;
            int cols = gray.Cols;

            if (rows == 0 || cols == 0)
            {
                stripesMask = new Mat();
                return src.Clone();
            }

            // Немного сгладим шум
            Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);

            // 2) Средняя яркость по каждой строке
            var rowMeans = new double[rows];
            for (int y = 0; y < rows; y++)
            {
                using var row = gray.Row(y);
                rowMeans[y] = Cv2.Mean(row)[0];
            }

            // 3) Сглаженный профиль (скользящее среднее по строкам)
            smoothWindow = Math.Max(1, smoothWindow);
            if (smoothWindow > rows) smoothWindow = rows;
            if (smoothWindow % 2 == 0) smoothWindow++; // лучше нечётное

            int half = smoothWindow / 2;
            var smooth = new double[rows];

            for (int y = 0; y < rows; y++)
            {
                int y0 = Math.Max(0, y - half);
                int y1 = Math.Min(rows - 1, y + half);

                double sum = 0;
                int count = 0;
                for (int k = y0; k <= y1; k++)
                {
                    sum += rowMeans[k];
                    count++;
                }

                smooth[y] = (count > 0) ? (sum / count) : rowMeans[y];
            }

            // 4) Аномалия = |mean - smooth|
            var diff = new double[rows];
            double sumDiff = 0.0;
            for (int y = 0; y < rows; y++)
            {
                double d = Math.Abs(rowMeans[y] - smooth[y]);
                diff[y] = d;
                sumDiff += d;
            }

            double meanDiff = sumDiff / Math.Max(1, rows);
            if (meanDiff <= 0) meanDiff = 1.0;

            // Порог: sensitivityK * среднее отклонение
            double thr = sensitivityK * meanDiff;
            if (thr <= 0) thr = meanDiff * 2.5;

            // 5) 1D-маска полос (по строкам)
            var stripeRows = new bool[rows];
            for (int y = 0; y < rows; y++)
            {
                stripeRows[y] = diff[y] > thr;
            }

            // 6) Расширяем полосы на несколько строк
            expandRows = Math.Max(0, expandRows);
            if (expandRows > 0)
            {
                var expanded = new bool[rows];
                for (int y = 0; y < rows; y++)
                {
                    if (!stripeRows[y]) continue;

                    int y0 = Math.Max(0, y - expandRows);
                    int y1 = Math.Min(rows - 1, y + expandRows);

                    for (int k = y0; k <= y1; k++)
                        expanded[k] = true;
                }
                stripeRows = expanded;
            }

            // 7) Превращаем 1D-маску в 2D Mat
            stripesMask = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0));
            for (int y = 0; y < rows; y++)
            {
                if (!stripeRows[y]) continue;
                stripesMask.Row(y).SetTo(255);
            }

            // 8) Затираем полосы: заменяем строки на ближайшие "здоровые"
            var result = bgr.Clone();

            for (int y = 0; y < rows; y++)
            {
                if (!stripeRows[y]) continue;

                // ищем ближайшую "нормальную" строку сверху и снизу
                int top = y - 1;
                while (top >= 0 && stripeRows[top]) top--;

                int bottom = y + 1;
                while (bottom < rows && stripeRows[bottom]) bottom++;

                if (top < 0 && bottom >= rows)
                {
                    // весь столбец — сплошные полосы, нечего копировать
                    continue;
                }

                int srcY;
                if (top < 0) srcY = bottom;
                else if (bottom >= rows) srcY = top;
                else
                {
                    // выберем ту строку, у которой diff меньше (ближе к норме)
                    srcY = diff[top] <= diff[bottom] ? top : bottom;
                }

                using var srcRow = bgr.Row(srcY);
                var dstRow = result.Row(y);
                srcRow.CopyTo(dstRow);
            }

            // 9) Debug-вывод
            if (debugWriteIntermediate && !string.IsNullOrEmpty(debugOutputPath))
            {
                try
                {
                    Directory.CreateDirectory(debugOutputPath);

                    // Профиль яркости (строки) — как картинка
                    using var profile = new Mat(1, rows, MatType.CV_32F);
                    for (int y = 0; y < rows; y++)
                        profile.Set(0, y, rowMeans[y]);

                    using var profileNorm = new Mat();
                    Cv2.Normalize(profile, profileNorm, 0, 255, NormTypes.MinMax, MatType.CV_8U);

                    using var profileImg = new Mat();
                    Cv2.Resize(profileNorm, profileImg, new Size(rows, 100), 0, 0, InterpolationFlags.Nearest);
                    //Cv2.ImWrite(Path.Combine(debugOutputPath, "scanner_hstripes_profile.png"), profileImg);

                    // Overlay: полосы подсвечены красным
                    using var overlay = bgr.Clone();
                    overlay.SetTo(new Scalar(0, 0, 255), stripesMask);
                    //Cv2.ImWrite(Path.Combine(debugOutputPath, "scanner_hstripes_overlay.png"), overlay);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"RemoveScannerHorizontalStripes debug write error: {ex.Message}");
                }
            }

            // 10) Возвращаем в том же формате (BGR или GRAY), что и вход
            if (src.Channels() == 1)
            {
                using var resultGray = new Mat();
                Cv2.CvtColor(result, resultGray, ColorConversionCodes.BGR2GRAY);
                return resultGray.Clone();
            }

            return result;
        }


        public static Mat RemoveScannerVerticalStripes(
    Mat src,
    double sensitivityK,               // "чувствительность", обычно 2.0–3.5
    int smoothWindow,                  // окно сглаживания профиля, напр. 15–31
    int expandCols,                    // на сколько колонок расширять найденные полосы
    out Mat stripesMask,               // маска вертикальных полос
    bool debugWriteIntermediate = false,
    string debugOutputPath = null)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty())
            {
                stripesMask = new Mat();
                return src.Clone();
            }

            // 1) Приводим к BGR, потом в grayscale
            Mat bgr;
            if (src.Channels() == 1)
            {
                bgr = new Mat();
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            }
            else
            {
                bgr = src.Clone();
            }

            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

            int rows = gray.Rows;
            int cols = gray.Cols;

            if (rows == 0 || cols == 0)
            {
                stripesMask = new Mat();
                return src.Clone();
            }

            // Немного сгладим шум
            Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);

            // 2) Средняя яркость по каждой колонке
            var colMeans = new double[cols];
            for (int x = 0; x < cols; x++)
            {
                using var col = gray.Col(x);
                colMeans[x] = Cv2.Mean(col)[0];
            }

            // 3) Сглаженный профиль (скользящее среднее)
            smoothWindow = Math.Max(1, smoothWindow);
            if (smoothWindow > cols) smoothWindow = cols;
            if (smoothWindow % 2 == 0) smoothWindow++; // лучше нечётное

            int half = smoothWindow / 2;
            var smooth = new double[cols];

            for (int x = 0; x < cols; x++)
            {
                int x0 = Math.Max(0, x - half);
                int x1 = Math.Min(cols - 1, x + half);

                double sum = 0;
                int count = 0;
                for (int k = x0; k <= x1; k++)
                {
                    sum += colMeans[k];
                    count++;
                }

                smooth[x] = (count > 0) ? (sum / count) : colMeans[x];
            }

            // 4) Аномалия = |mean - smooth|
            var diff = new double[cols];
            double sumDiff = 0.0;
            for (int x = 0; x < cols; x++)
            {
                double d = Math.Abs(colMeans[x] - smooth[x]);
                diff[x] = d;
                sumDiff += d;
            }

            double meanDiff = sumDiff / Math.Max(1, cols);
            if (meanDiff <= 0) meanDiff = 1.0;

            // Порог: sensitivityK * среднее отклонение
            double thr = sensitivityK * meanDiff;
            if (thr <= 0) thr = meanDiff * 2.5;

            // 5) 1D-маска полос (по колонкам)
            var stripeCols = new bool[cols];
            for (int x = 0; x < cols; x++)
            {
                stripeCols[x] = diff[x] > thr;
            }

            // 6) Расширяем полосы на несколько колонок
            expandCols = Math.Max(0, expandCols);
            if (expandCols > 0)
            {
                var expanded = new bool[cols];
                for (int x = 0; x < cols; x++)
                {
                    if (!stripeCols[x]) continue;

                    int x0 = Math.Max(0, x - expandCols);
                    int x1 = Math.Min(cols - 1, x + expandCols);

                    for (int k = x0; k <= x1; k++)
                        expanded[k] = true;
                }
                stripeCols = expanded;
            }

            // 7) Превращаем 1D-маску в 2D Mat
            stripesMask = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0));
            for (int x = 0; x < cols; x++)
            {
                if (!stripeCols[x]) continue;
                stripesMask.Col(x).SetTo(255);
            }

            // 8) Затираем полосы: заменяем колонки на ближайшие "здоровые"
            var result = bgr.Clone();

            for (int x = 0; x < cols; x++)
            {
                if (!stripeCols[x]) continue;

                // ищем ближайшую "нормальную" колонку слева и справа
                int left = x - 1;
                while (left >= 0 && stripeCols[left]) left--;

                int right = x + 1;
                while (right < cols && stripeCols[right]) right++;

                if (left < 0 && right >= cols)
                {
                    // вся строка — сплошные полосы, нечего копировать
                    continue;
                }

                int srcX;
                if (left < 0) srcX = right;
                else if (right >= cols) srcX = left;
                else
                {
                    // выберем ту колонку, у которой diff меньше (ближе к норме)
                    srcX = diff[left] <= diff[right] ? left : right;
                }

                using var srcCol = bgr.Col(srcX);
                var dstCol = result.Col(x);
                srcCol.CopyTo(dstCol);
            }

            // 9) Debug-вывод
            if (debugWriteIntermediate && !string.IsNullOrEmpty(debugOutputPath))
            {
                try
                {
                    Directory.CreateDirectory(debugOutputPath);

                    // Профиль яркости (колонки) — как картинка
                    using var profile = new Mat(1, cols, MatType.CV_32F);
                    for (int x = 0; x < cols; x++)
                        profile.Set(0, x, colMeans[x]);

                    using var profileNorm = new Mat();
                    Cv2.Normalize(profile, profileNorm, 0, 255, NormTypes.MinMax, MatType.CV_8U);

                    using var profileImg = new Mat();
                    Cv2.Resize(profileNorm, profileImg, new Size(cols, 100), 0, 0, InterpolationFlags.Nearest);
                    //Cv2.ImWrite(Path.Combine(debugOutputPath, "scanner_stripes_profile.png"), profileImg);

                    // Overlay: полосы подсвечены красным
                    using var overlay = bgr.Clone();
                    overlay.SetTo(new Scalar(0, 0, 255), stripesMask);
                    //Cv2.ImWrite(Path.Combine(debugOutputPath, "scanner_stripes_overlay.png"), overlay);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"RemoveScannerVerticalStripes debug write error: {ex.Message}");
                }
            }

            // 10) Возвращаем в том же формате (BGR или GRAY), что и вход
            if (src.Channels() == 1)
            {
                using var resultGray = new Mat();
                Cv2.CvtColor(result, resultGray, ColorConversionCodes.BGR2GRAY);
                return resultGray.Clone();
            }

            return result;
        }




        public static Mat RemoveEdgeStripes(
    Mat src,
    int lineWidthPx,
    double minLengthFraction,            // 0..1
    LineOrientation orientation,
    int offsetStartPx,                   // допуск от краёв в px
    Scalar lineColorRgb,                 // сейчас игнорируем (работаем "any color")
    out Mat linesMask,
    int colorTolerance = 30,             // не используется
    bool invertColorMeaning = false,     // не используется
    bool combineColorAndEdge = false,    // не используется
    bool debugWriteIntermediate = false, // можно включить ImWrite при желании
    string debugOutputPath = null
)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            // Маска результата
            linesMask = new Mat(src.Rows, src.Cols, MatType.CV_8U, Scalar.All(0));

            // Подготовка BGR и GRAY
            Mat bgr = new Mat();
            if (src.Channels() == 1)
                Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            else
                bgr = src.Clone();

            using var gray = new Mat();
            Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

            int rows = gray.Rows, cols = gray.Cols;
            if (rows == 0 || cols == 0)
                return src.Clone();

            // Лёгкое сглаживание, чтобы убрать шум
            Cv2.GaussianBlur(gray, gray, new Size(3, 3), 0);

            // Глобальная маска всех найденных полос
            using var globalMask = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0));

            // Локальная функция: обработка одной ориентации
            void ProcessOrientation(LineOrientation dir)
            {
                // 1) Градиент по нужному направлению (Sobel)
                using var grad16 = new Mat();
                if (dir == LineOrientation.Horizontal)
                {
                    // горизонтальные линии → интересует вертикальный градиент (по Y)
                    Cv2.Sobel(gray, grad16, MatType.CV_16S, 0, 1, 3);
                }
                else
                {
                    // вертикальные линии → интересует горизонтальный градиент (по X)
                    Cv2.Sobel(gray, grad16, MatType.CV_16S, 1, 0, 3);
                }

                using var gradAbs = new Mat();
                Cv2.ConvertScaleAbs(grad16, gradAbs);

                // 2) Бинаризация градиента (Otsu)
                using var edgesBin = new Mat();
                Cv2.Threshold(gradAbs, edgesBin, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

                // 3) Чуть утолщим контуры, чтобы линии стали связными
                using var edgesThick = new Mat();
                using (var smallKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)))
                {
                    Cv2.Dilate(edgesBin, edgesThick, smallKernel);
                }

                // 4) Морфология для выделения длинных полос
                int minLenPx = (int)Math.Round(minLengthFraction * (dir == LineOrientation.Horizontal ? cols : rows));
                minLenPx = Math.Max(10, minLenPx); // хотя бы 10 пикселей

                Size lineKernelSize = dir == LineOrientation.Horizontal
                    ? new Size(minLenPx, Math.Max(1, lineWidthPx))
                    : new Size(Math.Max(1, lineWidthPx), minLenPx);

                using var lineKernel = Cv2.GetStructuringElement(MorphShapes.Rect, lineKernelSize);
                using var lineMask = new Mat();

                // Открытие: оставляем только то, что похоже на длинные узкие объекты вдоль lineKernel
                Cv2.MorphologyEx(edgesThick, lineMask, MorphTypes.Open, lineKernel);
                // Закрытие, чтобы залечить дырки внутри полос
                Cv2.MorphologyEx(lineMask, lineMask, MorphTypes.Close, lineKernel);

                // 5) Поиск контуров на lineMask
                Cv2.FindContours(lineMask, out Point[][] contours, out HierarchyIndex[] hier,
                    RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                foreach (var cnt in contours)
                {
                    if (cnt == null || cnt.Length < 5) continue;

                    var r = Cv2.BoundingRect(cnt);

                    int length = (dir == LineOrientation.Horizontal) ? r.Width : r.Height;
                    int thickness = (dir == LineOrientation.Horizontal) ? r.Height : r.Width;

                    if (length < minLenPx) continue; // короткая — не наша полоса

                    // Допустимая «толщина» полосы
                    int maxThickness = Math.Max(lineWidthPx * 3, lineWidthPx + 6);
                    if (thickness > maxThickness) continue;

                    // 6) Проверка, что полоса касается нужного края
                    // offsetStartPx — "насколько от края" она может начинаться.
                    // edgeTol = offsetStartPx + запас по толщине.
                    int edgeTol = Math.Max(lineWidthPx * 2, offsetStartPx + lineWidthPx);

                    bool touchesEdge = false;
                    if (dir == LineOrientation.Horizontal)
                    {
                        // горизонтальная линия, ищем у top/bottom
                        if (r.Y <= edgeTol || (rows - (r.Y + r.Height)) <= edgeTol)
                            touchesEdge = true;
                    }
                    else
                    {
                        // вертикальная линия, ищем у left/right
                        if (r.X <= edgeTol || (cols - (r.X + r.Width)) <= edgeTol)
                            touchesEdge = true;
                    }

                    if (!touchesEdge) continue;

                    // 7) Добавляем этот контур в глобальную маску
                    using var contourMask = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0));
                    Cv2.DrawContours(contourMask, new[] { cnt }, -1, Scalar.White, -1);

                    // Чуть расширим, чтобы накрыть всю полосу
                    int dilateRadius = Math.Max(1, lineWidthPx / 2);
                    using var dilKernel = Cv2.GetStructuringElement(
                        MorphShapes.Rect,
                        new Size(dilateRadius * 2 + 1, dilateRadius * 2 + 1));
                    Cv2.Dilate(contourMask, contourMask, dilKernel);

                    Cv2.BitwiseOr(globalMask, contourMask, globalMask);
                }
            }

            // Обрабатываем нужные ориентации
            if (orientation == LineOrientation.Horizontal || orientation == LineOrientation.Both)
                ProcessOrientation(LineOrientation.Horizontal);
            if (orientation == LineOrientation.Vertical || orientation == LineOrientation.Both)
                ProcessOrientation(LineOrientation.Vertical);

            // Если ничего не нашли — возвращаем исходник
            if (Cv2.CountNonZero(globalMask) == 0)
            {
                bgr.Dispose();
                return src.Clone();
            }

            // Сохраняем маску
            linesMask = globalMask.Clone();

            // Inpaint по маске
            Mat inpainted = new Mat();
            int inpaintRadius = Math.Max(3, lineWidthPx + 2);
            Cv2.Inpaint(bgr, globalMask, inpainted, inpaintRadius, InpaintMethod.Telea);

            bgr.Dispose();
            return inpainted;
        }







        /// <summary>
        /// Удаляет длинные прямые линии заданной ориентации/ширины/цвета.
        /// Возвращает inpainted image; также возвращает маску найденных линий через out parameter.
        /// </summary>
        /// <param name="src">Входной BGR Mat (может быть и grayscale — будет сконвертирован)</param>
        /// <param name="lineWidthPx">Ожидаемая толщина линии в пикселях (примерно)</param>
        /// <param name="minLengthFraction">Минимальная длина линии относительно размера изображения (0..1). Для horizontal — доля ширины, для vertical — доля высоты.</param>
        /// <param name="orientation">Ориентация линии</param>
        /// <param name="offsetStartPx">Максимальное расстояние от соответствующего края, откуда линия должна начинаться (например, левая/верхняя граница). Если не нужно фильтровать — передайте 0.</param>
        /// <param name="lineColorRgb">Целевой цвет линии в RGB (0..255). Примечание: OpenCV использует BGR, поэтому преобразуется внутри.</param>
        /// <param name="linesMask">out — маска найденных линий (CV_8U, 0/255)</param>
        /// <param name="colorTolerance">Допуск цвета (на каждый канал), по умолчанию 40</param>
        /// <param name="invertColorMeaning">Если ваша логика "всё 255 = чёрная линия" (инвертированная палитра), установите true — тогда маска инвертируется.</param>
        /// <returns>Mat — изображение с удалёнными линиями (inpainted)</returns>
        /// 
        static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        public static Mat RemoveLongLines(
            Mat src,
            int lineWidthPx,
            double minLengthFraction,
            LineOrientation orientation,
            int offsetStartPx,
            Scalar lineColorRgb,
            out Mat linesMask,
            int colorTolerance,
            bool invertColorMeaning)
        {

            Debug.WriteLine($"Line width: {lineWidthPx}");
            Debug.WriteLine($"Min Length Fraction: {minLengthFraction}");
            Debug.WriteLine($"orientation: {orientation.ToString()}");
            Debug.WriteLine($"Offset start: {offsetStartPx}");
            Debug.WriteLine($"R, G, B {lineColorRgb.Val0}, {lineColorRgb.Val1}, {lineColorRgb.Val2}");
            Debug.WriteLine($"Color tolerance: {colorTolerance}");

            if (src == null) throw new ArgumentNullException(nameof(src));
            if (minLengthFraction <= 0 || minLengthFraction > 1) throw new ArgumentOutOfRangeException(nameof(minLengthFraction));

            // 1) Подготовка BGR исходника
            Mat bgr = new Mat();
            if (src.Channels() == 1) Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            else bgr = src.Clone();

            int rows = bgr.Rows, cols = bgr.Cols;

            // 2) Маска по цвету (пользователь передает RGB; Scalar в OpenCvSharp — B,G,R)
            var targetBgr = new Scalar(lineColorRgb.Val2, lineColorRgb.Val1, lineColorRgb.Val0); // convert RGB->BGR: incoming Scalar assumed (R,G,B) positions
                                                                                                 // Note: we expect user call like new Scalar(R,G,B). We convert to B,G,R here.
                                                                                                 // Build lower/upper bounds
            Scalar lower = new Scalar(
                Math.Max(0, targetBgr.Val0 - colorTolerance),
                Math.Max(0, targetBgr.Val1 - colorTolerance),
                Math.Max(0, targetBgr.Val2 - colorTolerance));
            Scalar upper = new Scalar(
                Math.Min(255, targetBgr.Val0 + colorTolerance),
                Math.Min(255, targetBgr.Val1 + colorTolerance),
                Math.Min(255, targetBgr.Val2 + colorTolerance));

            Mat colorMask = new Mat();
            Cv2.InRange(bgr, lower, upper, colorMask);

            if (invertColorMeaning)
            {
                Cv2.BitwiseNot(colorMask, colorMask);
            }
            // 2) Маска по цвету — лучше в Lab-пространстве для "red + rose"
            //double perceptualDelta = Math.Max(8.0, colorTolerance * 0.6); // рекомендованное преобразование tolerance -> delta
            //Mat colorMask = CreatePerceptualColorMask(bgr, lineColorRgb, perceptualDelta, invertColorMeaning);




            // 3) Морфология для "вытягивания" линий: kernel длиной = minLengthPx, толщина = lineWidthPx
            Mat longLinesMask = new Mat(rows, cols, MatType.CV_8U);

            // helper to process one orientation
            void ProcessOrientation(LineOrientation orient)
            {
                int minLenPx = (int)Math.Round(minLengthFraction * (orient == LineOrientation.Horizontal ? cols : rows));
                if (minLenPx < 3) minLenPx = 3;

                Size kernelSize;
                if (orient == LineOrientation.Horizontal)
                    kernelSize = new Size(minLenPx, Math.Max(1, lineWidthPx));
                else // Vertical
                    kernelSize = new Size(Math.Max(1, lineWidthPx), minLenPx);

                using (var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, kernelSize))
                using (var morphed = new Mat())
                {
                    Cv2.MorphologyEx(colorMask, morphed, MorphTypes.Open, kernel);
                    {
                        int bridgeFactor = 3; // tune 2..5
                        int bridgeLen = Math.Max(3, lineWidthPx * bridgeFactor);

                        Size bridgeKernelSize = (orient == LineOrientation.Horizontal)
                            ? new Size(ClampInt(bridgeLen, 3, cols), Math.Max(1, lineWidthPx))
                            : new Size(Math.Max(1, lineWidthPx), ClampInt(bridgeLen, 3, rows));

                        using (var bridgeKernel = Cv2.GetStructuringElement(MorphShapes.Rect, bridgeKernelSize))
                        using (var tmp = new Mat())
                        {
                            Cv2.Dilate(morphed, tmp, bridgeKernel);
                            Cv2.Erode(tmp, morphed, bridgeKernel);

                            // optional: Open with original kernel to restore thin shape
                            Cv2.MorphologyEx(morphed, tmp, MorphTypes.Open, kernel);
                            tmp.CopyTo(morphed);
                        }
                    }

                    if (Cv2.CountNonZero(morphed) == 0) return;

                    // global distance transform (CV_32F): mask size = 3
                    using (var distAll = new Mat())
                    {
                        Cv2.DistanceTransform(morphed, distAll, DistanceTypes.L2, DistanceTransformMasks.Mask3);

                        // find contours
                        Cv2.FindContours(morphed, out Point[][] contours, out HierarchyIndex[] hierarchy, RetrievalModes.External, ContourApproximationModes.ApproxSimple);

                        foreach (var cnt in contours)
                        {
                            if (cnt == null || cnt.Length < 6) continue;

                            // bounding rect quick reject
                            var r = Cv2.BoundingRect(cnt);
                            int mainLenBbox = (orient == LineOrientation.Horizontal) ? r.Width : r.Height;
                            if (mainLenBbox < 0.6 * minLenPx) continue;

                            // --- compute PCA-like principal axis (no FitLine) ---
                            double sumX = 0, sumY = 0;
                            foreach (var p in cnt) { sumX += p.X; sumY += p.Y; }
                            double meanX = sumX / cnt.Length;
                            double meanY = sumY / cnt.Length;

                            double sxx = 0, syy = 0, sxy = 0;
                            foreach (var p in cnt)
                            {
                                double dx = p.X - meanX;
                                double dy = p.Y - meanY;
                                sxx += dx * dx;
                                syy += dy * dy;
                                sxy += dx * dy;
                            }

                            // principal direction angle theta = 0.5 * atan2(2*sxy, sxx - syy)
                            double theta = 0.5 * Math.Atan2(2.0 * sxy, sxx - syy);
                            float vx = (float)Math.Cos(theta);
                            float vy = (float)Math.Sin(theta);

                            // project points along axis to get min/max projections (endpoints)
                            double minProj = double.MaxValue, maxProj = double.MinValue;
                            Point minPt = new Point(), maxPt = new Point();
                            foreach (var p in cnt)
                            {
                                double t = (p.X - meanX) * vx + (p.Y - meanY) * vy;
                                if (t < minProj) { minProj = t; minPt = p; }
                                if (t > maxProj) { maxProj = t; maxPt = p; }
                            }

                            double lengthPx = Math.Abs(maxProj - minProj);
                            if (lengthPx < 0.7 * minLenPx) continue;

                            // ROI (pad)
                            int roiPad = Math.Max(4, lineWidthPx * 2);
                            int x0roi = Math.Max(0, r.X - roiPad);
                            int y0roi = Math.Max(0, r.Y - roiPad);
                            int x1roi = Math.Min(cols, r.X + r.Width + roiPad);
                            int y1roi = Math.Min(rows, r.Y + r.Height + roiPad);
                            if (x1roi <= x0roi || y1roi <= y0roi) continue;
                            var roi = new Rect(x0roi, y0roi, x1roi - x0roi, y1roi - y0roi);

                            // draw contour into full-size mask (Mat) and then submat it
                            using (var contourMaskFull = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0)))
                            {
                                Cv2.DrawContours(contourMaskFull, new[] { cnt }, -1, Scalar.White, -1);
                                using (var contourMask = new Mat(contourMaskFull, roi))
                                using (var distRoi = new Mat(distAll, roi))
                                {
                                    var dlist = new System.Collections.Generic.List<float>(1024);
                                    for (int yy = 0; yy < contourMask.Rows; yy++)
                                    {
                                        for (int xx = 0; xx < contourMask.Cols; xx++)
                                        {
                                            if (contourMask.Get<byte>(yy, xx) == 0) continue;
                                            // distRoi is CV_32F
                                            float dv = distRoi.Get<float>(yy, xx);
                                            if (dv > 0) dlist.Add(dv);
                                        }
                                    }

                                    if (dlist.Count == 0) continue;

                                    dlist.Sort();
                                    int idxP90 = (int)Math.Floor(dlist.Count * 0.9);
                                    idxP90 = ClampInt(idxP90, 0, dlist.Count - 1);
                                    float p90Half = dlist[idxP90];
                                    float medianHalf = dlist[dlist.Count / 2];

                                    float p90Thickness = 2f * p90Half;
                                    float medianThickness = 2f * medianHalf;

                                    float maxAllowed = Math.Max(1, lineWidthPx * 3);
                                    if (p90Thickness > maxAllowed) continue;

                                    // offset/edge proximity test using endpoints
                                    bool passesOffset = true;
                                    if (offsetStartPx >= 0)
                                    {
                                        int tol = Math.Max(1, lineWidthPx); // tolerance around the requested start position

                                        if (orient == LineOrientation.Horizontal)
                                        {
                                            // check left (x == offsetStartPx) or right (x == cols-1-offsetStartPx)
                                            bool leftStart =
                                                Math.Abs(minPt.X - offsetStartPx) <= tol ||
                                                Math.Abs(maxPt.X - offsetStartPx) <= tol;

                                            bool rightStart =
                                                Math.Abs(minPt.X - (cols - 1 - offsetStartPx)) <= tol ||
                                                Math.Abs(maxPt.X - (cols - 1 - offsetStartPx)) <= tol;

                                            // optionally accept starts near top/bottom (if line actually begins near vertical edges)
                                            bool topStart =
                                                Math.Abs(minPt.Y - offsetStartPx) <= tol ||
                                                Math.Abs(maxPt.Y - offsetStartPx) <= tol;

                                            bool bottomStart =
                                                Math.Abs(minPt.Y - (rows - 1 - offsetStartPx)) <= tol ||
                                                Math.Abs(maxPt.Y - (rows - 1 - offsetStartPx)) <= tol;

                                            // пропускаем, если старт у любого края (лево/право/верх/низ)
                                            passesOffset = leftStart || rightStart || topStart || bottomStart;
                                        }
                                        else // vertical
                                        {
                                            // check top (y == offsetStartPx) or bottom (y == rows-1-offsetStartPx)
                                            bool topStart =
                                                Math.Abs(minPt.Y - offsetStartPx) <= tol ||
                                                Math.Abs(maxPt.Y - offsetStartPx) <= tol;

                                            bool bottomStart =
                                                Math.Abs(minPt.Y - (rows - 1 - offsetStartPx)) <= tol ||
                                                Math.Abs(maxPt.Y - (rows - 1 - offsetStartPx)) <= tol;

                                            // also allow left/right if needed
                                            bool leftStart =
                                                Math.Abs(minPt.X - offsetStartPx) <= tol ||
                                                Math.Abs(maxPt.X - offsetStartPx) <= tol;

                                            bool rightStart =
                                                Math.Abs(minPt.X - (cols - 1 - offsetStartPx)) <= tol ||
                                                Math.Abs(maxPt.X - (cols - 1 - offsetStartPx)) <= tol;

                                            passesOffset = topStart || bottomStart || leftStart || rightStart;
                                        }
                                    }


                                    if (!passesOffset) continue;

                                    // Accept: copy mask and dilate
                                    using (var maskToAdd = new Mat(rows, cols, MatType.CV_8U, Scalar.All(0)))
                                    {
                                        contourMaskFull.CopyTo(maskToAdd); // full size mat
                                        int dilateK = Math.Max(1, (int)Math.Round(Math.Max(2, p90Thickness / 2.0)));
                                        using (var dkernel2 = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(dilateK * 2 + 1, dilateK * 2 + 1)))
                                        {
                                            Cv2.Dilate(maskToAdd, maskToAdd, dkernel2);
                                            Cv2.BitwiseOr(longLinesMask, maskToAdd, longLinesMask);
                                        }
                                    }
                                } // contourMask, distRoi
                            } // contourMaskFull
                        } // foreach contour
                    } // distAll
                } // kernel, morphed
            }



            if (orientation == LineOrientation.Horizontal || orientation == LineOrientation.Both)
                ProcessOrientation(LineOrientation.Horizontal);
            if (orientation == LineOrientation.Vertical || orientation == LineOrientation.Both)
                ProcessOrientation(LineOrientation.Vertical);

            // 4) Опционально — дополнительно выполнить немного дилатации, чтобы убрать пропуски
            if (lineWidthPx > 1)
            {
                var extraKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(Math.Max(1, lineWidthPx / 2), Math.Max(1, lineWidthPx / 2)));
                Cv2.Dilate(longLinesMask, longLinesMask, extraKernel);
                extraKernel.Dispose();
            }

            // 5) итоговая маска линий
            linesMask = longLinesMask.Clone();

            // 6) инпейтинг: преобразуем в 8U 3ch для inpaint (inpaint требует single-channel mask, ok)
            Mat inpaintSrc = bgr.Clone();
            Mat inpainted = new Mat();
            // Радиус inpaint немного больше толщины
            int inpaintRadius = Math.Max(3, lineWidthPx + 2);
            Cv2.Inpaint(inpaintSrc, linesMask, inpainted, inpaintRadius, InpaintMethod.Telea);

            // очистка
            inpaintSrc.Dispose();
            colorMask.Dispose();
            bgr.Dispose();

            return inpainted;
        }

        static void DynamicDistanceTransform(Mat src, Mat dst)
        {
            // Попробуем найти подходящую перегрузку Cv2.DistanceTransform через reflection,
            // и вызвать её с "Mask3"/3/Precise/Mask5 в порядке предпочтения.
            var cv2Type = typeof(OpenCvSharp.Cv2);
            var asm = cv2Type.Assembly;

            // получаем все методы с именем DistanceTransform
            var methods = cv2Type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                 .Where(m => m.Name == "DistanceTransform")
                                 .ToArray();

            if (methods.Length == 0)
            {
                // Нет таких методов — выбросим ясную ошибку
                throw new InvalidOperationException("OpenCvSharp.Cv2.DistanceTransform method not found in assembly.");
            }

            // Список кандидатных строковых имён enum-значений, которые могут использоваться в разных версиях
            string[] tryNames = new[] { "Mask3", "Mask5", "Precise", "Mask" };

            // 1) Попробуем найти метод с сигнатурой (Mat, Mat, DistanceTypes, <enumType>)
            foreach (var m in methods)
            {
                var pars = m.GetParameters();
                if (pars.Length == 4 &&
                    pars[0].ParameterType == typeof(Mat) &&
                    pars[1].ParameterType == typeof(Mat) &&
                    pars[2].ParameterType == typeof(DistanceTypes))
                {
                    var fourthType = pars[3].ParameterType;

                    // Если четвёртый параметр — enum, попытаемся распарсить подходящее значение
                    if (fourthType.IsEnum)
                    {
                        object enumValue = null;

                        // Попробуем перебрать имена в tryNames
                        foreach (var nm in tryNames)
                        {
                            try
                            {
                                enumValue = Enum.Parse(fourthType, nm);
                                if (enumValue != null) break;
                            }
                            catch { /* ignore */ }
                        }

                        // Если не нашли по именам, возьмём первое значение enum-а как fallback
                        if (enumValue == null)
                        {
                            var vals = Enum.GetValues(fourthType);
                            if (vals.Length > 0) enumValue = vals.GetValue(0);
                        }

                        if (enumValue != null)
                        {
                            // Invoke: src, dst, DistanceTypes.L2, enumValue
                            m.Invoke(null, new object[] { src, dst, DistanceTypes.L2, enumValue });
                            return;
                        }
                    }

                    // Если четвёртый параметр — int (реже), вызовем с 3
                    if (fourthType == typeof(int))
                    {
                        m.Invoke(null, new object[] { src, dst, DistanceTypes.L2, 3 });
                        return;
                    }
                }
            }

            // 2) Если не нашли 4-параметровую перегрузку, попробуем найти (Mat, Mat, DistanceTypes)
            var m3 = methods.FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return p.Length == 3 &&
                       p[0].ParameterType == typeof(Mat) &&
                       p[1].ParameterType == typeof(Mat) &&
                       p[2].ParameterType == typeof(DistanceTypes);
            });

            if (m3 != null)
            {
                m3.Invoke(null, new object[] { src, dst, DistanceTypes.L2 });
                return;
            }

            // 3) Если ничего не подошло — попробуем вызвать любую перегрузку, которая выглядит подходяще (последняя попытка)
            foreach (var m in methods)
            {
                var pars = m.GetParameters();
                // если первый два параметра Mat, используем DistanceTypes.L2 и передаём 3 если возможно
                if (pars.Length >= 3 && pars[0].ParameterType == typeof(Mat) && pars[1].ParameterType == typeof(Mat))
                {
                    var args = new List<object> { src, dst };

                    // если третий параметр DistanceTypes
                    if (pars.Length >= 3 && pars[2].ParameterType == typeof(DistanceTypes))
                    {
                        args.Add(DistanceTypes.L2);

                        // если есть четвёртый параметр и он enum -> parse Mask3, если int -> 3
                        if (pars.Length >= 4)
                        {
                            var t4 = pars[3].ParameterType;
                            if (t4.IsEnum)
                            {
                                object enumValue = null;
                                foreach (var nm in tryNames)
                                {
                                    try { enumValue = Enum.Parse(t4, nm); if (enumValue != null) break; } catch { }
                                }
                                if (enumValue == null)
                                {
                                    var vals = Enum.GetValues(t4);
                                    if (vals.Length > 0) enumValue = vals.GetValue(0);
                                }
                                if (enumValue != null) args.Add(enumValue);
                                else continue; // не можем подготовить 4й аргумент
                            }
                            else if (t4 == typeof(int))
                            {
                                args.Add(3);
                            }
                            else
                            {
                                continue; // не умеем подготовить 4й аргумент
                            }
                        }

                        // попытка invoke
                        try { m.Invoke(null, args.ToArray()); return; } catch { /* пробовать дальше */ }
                    }
                }
            }

            // Если ничего не получилось — бросаем понятную ошибку
            throw new InvalidOperationException("Could not call Cv2.DistanceTransform with any known overload. Check your OpenCvSharp version.");
        }

        static Mat CreatePerceptualColorMask(Mat bgrImage, Scalar targetRgb, double delta = 30.0, bool invertColorMeaning = false)
        {
            if (bgrImage == null) throw new ArgumentNullException(nameof(bgrImage));
            if (bgrImage.Empty()) return new Mat(bgrImage.Rows, bgrImage.Cols, MatType.CV_8U, Scalar.All(0));

            using var lab8 = new Mat();
            Cv2.CvtColor(bgrImage, lab8, ColorConversionCodes.BGR2Lab);
            using var lab = new Mat();
            lab8.ConvertTo(lab, MatType.CV_32F);

            using var targetBgrPixel = new Mat(1, 1, MatType.CV_8UC3, new Scalar(targetRgb.Val2, targetRgb.Val1, targetRgb.Val0));
            using var targetLab8 = new Mat();
            Cv2.CvtColor(targetBgrPixel, targetLab8, ColorConversionCodes.BGR2Lab);
            Vec3b tLabVec = targetLab8.Get<Vec3b>(0, 0);
            float L0 = tLabVec.Item0, a0 = tLabVec.Item1, b0 = tLabVec.Item2;

            Cv2.Split(lab, out Mat[] chs);
            using var tmp0 = new Mat(); using var tmp1 = new Mat(); using var tmp2 = new Mat();
            Cv2.Subtract(chs[0], new Scalar(L0), tmp0);
            Cv2.Subtract(chs[1], new Scalar(a0), tmp1);
            Cv2.Subtract(chs[2], new Scalar(b0), tmp2);
            Cv2.Multiply(tmp0, tmp0, tmp0);
            Cv2.Multiply(tmp1, tmp1, tmp1);
            Cv2.Multiply(tmp2, tmp2, tmp2);
            using var sqSum = new Mat();
            Cv2.Add(tmp0, tmp1, sqSum);
            Cv2.Add(sqSum, tmp2, sqSum); // CV_32F

            double deltaSq = delta * delta;
            // Threshold BinaryInv: set 255 where sqSum <= deltaSq
            using var maskFloat = new Mat();
            Cv2.Threshold(sqSum, maskFloat, deltaSq, 255.0, ThresholdTypes.BinaryInv);
            var mask = new Mat();
            maskFloat.ConvertTo(mask, MatType.CV_8U);

            if (invertColorMeaning) Cv2.BitwiseNot(mask, mask);

            // dispose chs
            foreach (var m in chs) m.Dispose();
            lab8.Dispose(); lab.Dispose();
            targetBgrPixel.Dispose(); targetLab8.Dispose();
            tmp0.Dispose(); tmp1.Dispose(); tmp2.Dispose(); sqSum.Dispose(); maskFloat.Dispose();

            return mask;
        }
    }
}


