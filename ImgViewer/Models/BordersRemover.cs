using OpenCvSharp;
using System.Diagnostics;

namespace ImgViewer.Models
{
    public class BordersRemover
    {

        public enum BordersRemovalMode
        {
            Cut,
            Fill
        }

        public enum BrickInpaintMode
        {
            Fill,           // старое поведение: просто залить цветом страницы
            Telea,          // Cv2.Inpaint(..., InpaintMethod.Telea)
            NS    // Cv2.Inpaint(..., InpaintMethod.NavierStokes)
        }


        public static Mat RemoveBorderArtifactsGeneric_Safe(
            CancellationToken token,
            Mat src,
            byte thr,                       // порог для определения "тёмного" (например EstimateBlackThreshold(rotated)) 
            Scalar? bgColor = null,         // цвет фона (null -> автоопределение по углам)
            int minAreaPx = 2000,           // если площадь >= этого -> считается значимой
            double minSpanFraction = 0.6,   // если bbox покрывает >= этой доли по ширине/высоте -> кандидат
            double solidityThreshold = 0.6, // если solidity >= -> кандидат
            double minDepthFraction = 0.05, // проникновение вглубь, в долях min(rows,cols)
            int featherPx = 12,
            bool useTeleaHybrid = true
        )


        {




            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

            using var srcClone = src.Clone();


            Mat working = srcClone;
            bool disposeWorking = false;
           


                try
                {
                    token.ThrowIfCancellationRequested();
                    if (srcClone.Type() == MatType.CV_8UC3)
                    {
                    // already BGR
                    }
                    else if (srcClone.Type() == MatType.CV_8UC4)
                    {
                        working = new Mat();
                        Cv2.CvtColor(srcClone, working, ColorConversionCodes.BGRA2BGR);
                        disposeWorking = true;
                    }
                    else if (srcClone.Type() == MatType.CV_8UC1)
                    {
                        working = new Mat();
                        Cv2.CvtColor(srcClone, working, ColorConversionCodes.GRAY2BGR);
                        disposeWorking = true;
                    }
                    else
                    {
                        throw new InvalidOperationException("Unsupported image type for border removal");
                    }


                    int rows = working.Rows;
                    int cols = working.Cols;
                    int minDepthPx = (int)Math.Round(minDepthFraction * Math.Min(rows, cols));

                    // determine bg color from corners if not provided
                    Scalar chosenBg = bgColor ?? new Scalar(255, 255, 255);
                    if (!bgColor.HasValue)
                    {


                        int cornerSize = Math.Max(5, Math.Min(2, Math.Min(rows, cols) / 30));
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
                            token.ThrowIfCancellationRequested();
                            if (r.Width <= 0 || r.Height <= 0) continue;
                            using var patch = new Mat(working, r);
                            var mean = Cv2.Mean(patch);
                            double brightness = (mean.Val0 + mean.Val1 + mean.Val2) / 3.0;
                            if (brightness > thr * 1.0) { sb += mean.Val0; sg += mean.Val1; sr += mean.Val2; cnt++; }
                        }
                        if (cnt > 0) chosenBg = new Scalar(sb / cnt, sg / cnt, sr / cnt);
                    }



                    // 1) binary dark mask
                    using var gray = new Mat();
                    Cv2.CvtColor(working, gray, ColorConversionCodes.BGR2GRAY);

                    using var darkMask = new Mat();
                    Cv2.Threshold(gray, darkMask, thr, 255, ThresholdTypes.BinaryInv); // dark->255




                    // small open to reduce noise
                    //token.ThrowIfCancellationRequested();
                    //using (var kOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
                    //{
                    //    token.ThrowIfCancellationRequested();
                    //    Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kOpen);check 
                    //}

                    // 2) connected components
                    using var labels = new Mat();
                    using var stats = new Mat();
                    using var cents = new Mat();
                    token.ThrowIfCancellationRequested();
                    int nLabels = Cv2.ConnectedComponentsWithStats(darkMask, labels, stats, cents);
                    token.ThrowIfCancellationRequested();

                    System.Diagnostics.Debug.WriteLine($"darkMask nonzero: {Cv2.CountNonZero(darkMask)}");
                    System.Diagnostics.Debug.WriteLine($"nLabels: {nLabels}  labels.type={labels.Type()}  stats.size={stats.Rows}x{stats.Cols}");

                    // selected mask init
                    //  var selectedMask = Mat.Zeros(darkMask.Size(), MatType.CV_8U);
                    var selectedMask = new Mat(darkMask.Size(), MatType.CV_8U, Scalar.All(0));


                    // будем накапливать максимальную "глубину" бордюра для каждой стороны
                    //int maxTopDepth = 0, maxBottomDepth = 0, maxLeftDepth = 0, maxRightDepth = 0;
                    //bool hasTop = false, hasBottom = false, hasLeft = false, hasRight = false;


                    // iterate components

                    // ширина пограничного пояса (то же, что раньше borderBandPx)
                    int borderBandPx = minDepthPx;

                    // --- 1) Предрасчёт геометрии по stats для каждого label ---
                    bool[] touchesLeftArr = new bool[nLabels];
                    bool[] touchesTopArr = new bool[nLabels];
                    bool[] touchesRightArr = new bool[nLabels];
                    bool[] touchesBottomArr = new bool[nLabels];
                    double[] solidityArr = new double[nLabels];
                    double[] spanWArr = new double[nLabels];
                    double[] spanHArr = new double[nLabels];

                    for (int i = 1; i < nLabels; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        int x = stats.At<int>(i, 0);
                        int y = stats.At<int>(i, 1);
                        int w = stats.At<int>(i, 2);
                        int h = stats.At<int>(i, 3);
                        int area = stats.At<int>(i, 4);

                        bool touchesLeft = x <= 0;
                        bool touchesTop = y <= 0;
                        bool touchesRight = (x + w) >= (cols - 1);
                        bool touchesBottom = (y + h) >= (rows - 1);

                        // если не касается ни одной стороны — дальше этот label нам не интересен
                        if (!(touchesLeft || touchesTop || touchesRight || touchesBottom))
                            continue;

                        touchesLeftArr[i] = touchesLeft;
                        touchesTopArr[i] = touchesTop;
                        touchesRightArr[i] = touchesRight;
                        touchesBottomArr[i] = touchesBottom;

                        double solidity = (w > 0 && h > 0) ? (double)area / (w * h) : 0.0;
                        double spanFractionW = (double)w / cols;
                        double spanFractionH = (double)h / rows;

                        solidityArr[i] = solidity;
                        spanWArr[i] = spanFractionW;
                        spanHArr[i] = spanFractionH;
                    }

                    // --- 2) Один проход по labels: считаем maxDepth для каждого label ---
                    int[] maxDepth = new int[nLabels];
                    int lastRow = rows - 1;
                    int lastCol = cols - 1;

                    for (int yy = 0; yy < rows; yy++)
                    {
                        token.ThrowIfCancellationRequested();
                        int dY = Math.Min(yy, lastRow - yy);

                        for (int xx = 0; xx < cols; xx++)
                        {
                            int label = labels.At<int>(yy, xx);
                            if (label <= 0) continue; // фон

                            int d = Math.Min(Math.Min(xx, lastCol - xx), dY);
                            if (d > maxDepth[label]) maxDepth[label] = d;
                        }
                    }

                    // --- 3) Решаем, какие labels считаем бордюром ---
                    bool[] select = new bool[nLabels];

                    for (int i = 1; i < nLabels; i++)
                    {
                        token.ThrowIfCancellationRequested();

                        bool touchesLeft = touchesLeftArr[i];
                        bool touchesTop = touchesTopArr[i];
                        bool touchesRight = touchesRightArr[i];
                        bool touchesBottom = touchesBottomArr[i];

                        if (!(touchesLeft || touchesTop || touchesRight || touchesBottom))
                            continue; // мы их вообще не рассматривали

                        bool touchesAny = true;

                        double solidity = solidityArr[i];
                        double spanFractionW = spanWArr[i];
                        double spanFractionH = spanHArr[i];
                        bool spansHoriz = (touchesTop || touchesBottom) && spanFractionW >= minSpanFraction;
                        bool spansVert = (touchesLeft || touchesRight) && spanFractionH >= minSpanFraction;
                        bool isBigFrame = (touchesLeft && touchesRight) || (touchesTop && touchesBottom);
                        bool isInBorderBand = maxDepth[i] <= borderBandPx;

                        bool s = false;

                        if (touchesAny)
                        {
                            // реальные полосы/рамки вдоль краёв
                            if (spansHoriz || spansVert || isBigFrame)
                                s = true;

                            // неровные/тонкие штуки в узком поясе + низкая solidity
                            if (isInBorderBand && solidity < solidityThreshold)
                                s = true;
                        }

                        select[i] = s;
                    }

                    // --- 4) Второй проход по labels: строим selectedMask только по пограничному поясу ---
                    selectedMask.SetTo(Scalar.All(0));

                    for (int yy = 0; yy < rows; yy++)
                    {
                        token.ThrowIfCancellationRequested();
                        int dY = Math.Min(yy, lastRow - yy);

                        for (int xx = 0; xx < cols; xx++)
                        {
                            int label = labels.At<int>(yy, xx);
                            if (label <= 0) continue;
                            if (!select[label]) continue;

                            int d = Math.Min(Math.Min(xx, lastCol - xx), dY);
                            if (d <= borderBandPx)
                            {
                                selectedMask.Set<byte>(yy, xx, 255);
                            }
                        }
                    }

                    // дальше идёт твой STRIP MODE (если когда-нибудь разкомментируешь)
                    // // === STRIP MODE: ...


                    //for (int i = 1; i < nLabels; i++)
                    //{
                    //    token.ThrowIfCancellationRequested();
                    //    int x = stats.At<int>(i, 0);
                    //    int y = stats.At<int>(i, 1);
                    //    int w = stats.At<int>(i, 2);
                    //    int h = stats.At<int>(i, 3);
                    //    int area = stats.At<int>(i, 4);

                    //    bool touchesLeft = x <= 0;
                    //    bool touchesTop = y <= 0;
                    //    bool touchesRight = (x + w) >= (cols - 1);
                    //    bool touchesBottom = (y + h) >= (rows - 1);

                    //    if (!(touchesLeft || touchesTop || touchesRight || touchesBottom)) continue;

                    //    double solidity = (w > 0 && h > 0) ? (double)area / (w * h) : 0.0;
                    //    double spanFractionW = (double)w / cols;
                    //    double spanFractionH = (double)h / rows;

                    //    // compute maxDepth by scanning the component mask area (safe, no ToArray)
                    //    //int maxDepth = 0;
                    //    using (var compMask = new Mat())
                    //    {
                    //        Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask); // 255 где label==i

                    //        int maxDepth = 0;

                    //        // НОВОЕ: маска "пограничной части" этого компонента
                    //        using var compBorderMask = new Mat(darkMask.Size(), MatType.CV_8U, Scalar.All(0));

                    //        // ширина пограничного пояса в пикселях (то, что раньше minDepthPx / maxBorderDepthPx)
                    //        int borderBandPx = (int)Math.Round(minDepthFraction * Math.Min(rows, cols));

                    //        // скан bbox компонента
                    //        int x0 = Math.Max(0, x);
                    //        int y0 = Math.Max(0, y);
                    //        int x1 = Math.Min(cols - 1, x + w - 1);
                    //        int y1 = Math.Min(rows - 1, y + h - 1);

                    //        for (int yy = y0; yy <= y1; yy++)
                    //        {
                    //            token.ThrowIfCancellationRequested();
                    //            for (int xx = x0; xx <= x1; xx++)
                    //            {
                    //                byte v = compMask.At<byte>(yy, xx);
                    //                if (v == 0) continue;

                    //                int d = Math.Min(
                    //                            Math.Min(xx, cols - 1 - xx),
                    //                            Math.Min(yy, rows - 1 - yy));

                    //                if (d > maxDepth)
                    //                    maxDepth = d;

                    //                // ⬅ если пиксель этого компонента находится близко к краю — пишем его в compBorderMask
                    //                if (d <= borderBandPx)
                    //                    compBorderMask.Set<byte>(yy, xx, 255);
                    //            }
                    //        }

                    //        bool touchesAny = touchesLeft || touchesTop || touchesRight || touchesBottom;

                    //        bool isInBorderBand = maxDepth <= borderBandPx;

                    //        bool spansHoriz =
                    //            (touchesTop || touchesBottom) &&
                    //            spanFractionW >= minSpanFraction;

                    //        bool spansVert =
                    //            (touchesLeft || touchesRight) &&
                    //            spanFractionH >= minSpanFraction;

                    //        bool isBigFrame =
                    //            (touchesLeft && touchesRight) ||
                    //            (touchesTop && touchesBottom);

                    //        bool select = false;

                    //        if (touchesAny)
                    //        {
                    //            // реальный бордюр — полосы вдоль краёв или рамка
                    //            if (spansHoriz || spansVert || isBigFrame)
                    //                select = true;

                    //            // опционально: неровные/тонкие штуки в узком поясе
                    //            if (isInBorderBand && solidity < solidityThreshold)
                    //                select = true;
                    //        }

                    //        if (select)
                    //        {
                    //            // ⬇ ВАЖНО: добавляем только погран-пояс компонента, а не весь компонент
                    //            Cv2.BitwiseOr(selectedMask, compBorderMask, selectedMask);

                    //            // НОВОЕ: обновляем максимальную глубину бордюра по сторонам
                    //            //if (maxDepth > 0)
                    //            //{
                    //            //    if (touchesTop)
                    //            //    {
                    //            //        hasTop = true;
                    //            //        if (maxDepth > maxTopDepth) maxTopDepth = maxDepth;
                    //            //    }
                    //            //    if (touchesBottom)
                    //            //    {
                    //            //        hasBottom = true;
                    //            //        if (maxDepth > maxBottomDepth) maxBottomDepth = maxDepth;
                    //            //    }
                    //            //    if (touchesLeft)
                    //            //    {
                    //            //        hasLeft = true;
                    //            //        if (maxDepth > maxLeftDepth) maxLeftDepth = maxDepth;
                    //            //    }
                    //            //    if (touchesRight)
                    //            //    {
                    //            //        hasRight = true;
                    //            //        if (maxDepth > maxRightDepth) maxRightDepth = maxDepth;
                    //            //    }
                    //            //}
                    //        }


                    //    }
                    //}

                    // === STRIP MODE: заменяем зубчатую маску бордюра на ровные полосы по сторонам ===
                    //if (hasTop || hasBottom || hasLeft || hasRight)
                    //{
                    //    // ограничим максимальную глубину разумным потолком
                    //    int maxDepthCap = (int)Math.Round(minDepthFraction * Math.Min(rows, cols));
                    //    maxDepthCap = Math.Max(1, maxDepthCap);

                    //    // очистим старую зубчатую маску
                    //    selectedMask.SetTo(Scalar.All(0));

                    //    // TOP
                    //    if (hasTop && maxTopDepth > 0)
                    //    {
                    //        int depth = Math.Min(maxTopDepth, maxDepthCap);
                    //        depth = Math.Min(depth, rows); // safety
                    //        if (depth > 0)
                    //        {
                    //            var topRect = new Rect(0, 0, cols, depth);
                    //            using (var roi = new Mat(selectedMask, topRect))
                    //                roi.SetTo(255);
                    //        }
                    //    }

                    //    // BOTTOM
                    //    if (hasBottom && maxBottomDepth > 0)
                    //    {
                    //        int depth = Math.Min(maxBottomDepth, maxDepthCap);
                    //        depth = Math.Min(depth, rows);
                    //        if (depth > 0)
                    //        {
                    //            var bottomRect = new Rect(0, rows - depth, cols, depth);
                    //            using (var roi = new Mat(selectedMask, bottomRect))
                    //                roi.SetTo(255);
                    //        }
                    //    }

                    //    // LEFT
                    //    if (hasLeft && maxLeftDepth > 0)
                    //    {
                    //        int depth = Math.Min(maxLeftDepth, maxDepthCap);
                    //        depth = Math.Min(depth, cols);
                    //        if (depth > 0)
                    //        {
                    //            var leftRect = new Rect(0, 0, depth, rows);
                    //            using (var roi = new Mat(selectedMask, leftRect))
                    //                roi.SetTo(255);
                    //        }
                    //    }

                    //    // RIGHT
                    //    if (hasRight && maxRightDepth > 0)
                    //    {
                    //        int depth = Math.Min(maxRightDepth, maxDepthCap);
                    //        depth = Math.Min(depth, cols);
                    //        if (depth > 0)
                    //        {
                    //            var rightRect = new Rect(cols - depth, 0, depth, rows);
                    //            using (var roi = new Mat(selectedMask, rightRect))
                    //                roi.SetTo(255);
                    //        }
                    //    }
                    //}
                    //// === END STRIP MODE ===

                    var filled = working.Clone();

                    int margin = featherPx; // <-- ваш n: положительное = расширить, отрицательное = врезать внутрь



                    // ensure mask is CV_8U with values 0/255
                    Mat modMask = new Mat();
                    if (selectedMask.Type() != MatType.CV_8U)
                        selectedMask.ConvertTo(modMask, MatType.CV_8U);
                    else
                        modMask = selectedMask.Clone();

                    if (margin > 0)
                    {
                        // use a square kernel of size (2*margin+1)
                        var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * margin + 1, 2 * margin + 1));
                        Cv2.Dilate(modMask, modMask, k, iterations: 1);
                    }
                    else if (margin < 0)
                    {
                        int m = -margin;
                        var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * m + 1, 2 * m + 1));
                        Cv2.Erode(modMask, modMask, k, iterations: 1);
                    }

                    int guardBandPx = Math.Max(8, minDepthPx / 2); // половина глубины бордюра
                    guardBandPx = Math.Min(guardBandPx, Math.Min(rows, cols) / 4); // safety cap

                    // если картинка совсем маленькая, guardBand не имеет смысла
                    if (rows > 2 * guardBandPx && cols > 2 * guardBandPx)
                    {
                        using var innerDark = new Mat(modMask.Size(), MatType.CV_8U, Scalar.All(0));

                        // прямоугольник "внутри страницы", отступленный от границ
                        var innerRoi = new Rect(
                            guardBandPx,
                            guardBandPx,
                            cols - 2 * guardBandPx,
                            rows - 2 * guardBandPx);

                        // скопируем тёмные пиксели из darkMask в innerDark только в этом внутреннем прямоугольнике
                        using (var innerDarkRoi = new Mat(innerDark, innerRoi))
                        using (var darkMaskRoi = new Mat(darkMask, innerRoi))
                        {
                            darkMaskRoi.CopyTo(innerDarkRoi);
                        }

                        // Теперь innerDark=255 там, где тёмный контент внутри страницы.
                        // Вырежем его из маски заливки: modMask &= ~innerDark
                        using var innerDarkInv = new Mat();
                        Cv2.BitwiseNot(innerDark, innerDarkInv);
                        Cv2.BitwiseAnd(modMask, innerDarkInv, modMask);
                    }


                    //// === NEW: сгладим mask вдоль краёв, чтобы не было "рваных" краёв ===

                    //// ширина полосы вдоль краёв, где будем сглаживать маску
                    //int edgeBandPx = Math.Max(4, Math.Min(minDepthPx * 2, Math.Min(rows, cols) / 6));

                    //// если картинка слишком маленькая — просто пропустим
                    //if (edgeBandPx > 0 && rows > 2 && cols > 2)
                    //{
                    //    // горизонтальное сглаживание (top / bottom)
                    //    // ядро вытянуто по горизонтали: закрывает дырки и ступеньки вдоль линий
                    //    using var kHoriz = Cv2.GetStructuringElement(
                    //        MorphShapes.Rect,
                    //        new OpenCvSharp.Size(141, 1)); // длину можно потом подстроить

                    //    // TOP band
                    //    int topBand = Math.Min(edgeBandPx, rows);
                    //    if (topBand > 0)
                    //    {
                    //        var topRect = new Rect(0, 0, cols, topBand);
                    //        using var topRoi = new Mat(modMask, topRect);
                    //        Cv2.MorphologyEx(topRoi, topRoi, MorphTypes.Close, kHoriz);
                    //    }

                    //    // BOTTOM band
                    //    int bottomBand = Math.Min(edgeBandPx, rows);
                    //    if (bottomBand > 0)
                    //    {
                    //        var bottomRect = new Rect(0, rows - bottomBand, cols, bottomBand);
                    //        using var bottomRoi = new Mat(modMask, bottomRect);
                    //        Cv2.MorphologyEx(bottomRoi, bottomRoi, MorphTypes.Close, kHoriz);
                    //    }

                    //    // вертикальное сглаживание (left / right)
                    //    using var kVert = Cv2.GetStructuringElement(
                    //        MorphShapes.Rect,
                    //        new OpenCvSharp.Size(1, 141)); // вытянуто по вертикали

                    //    // LEFT band
                    //    int leftBand = Math.Min(edgeBandPx, cols);
                    //    if (leftBand > 0)
                    //    {
                    //        var leftRect = new Rect(0, 0, leftBand, rows);
                    //        using var leftRoi = new Mat(modMask, leftRect);
                    //        Cv2.MorphologyEx(leftRoi, leftRoi, MorphTypes.Close, kVert);
                    //    }

                    //    // RIGHT band
                    //    int rightBand = Math.Min(edgeBandPx, cols);
                    //    if (rightBand > 0)
                    //    {
                    //        var rightRect = new Rect(cols - rightBand, 0, rightBand, rows);
                    //        using var rightRoi = new Mat(modMask, rightRect);
                    //        Cv2.MorphologyEx(rightRoi, rightRoi, MorphTypes.Close, kVert);
                    //    }
                    //}
                    //// === END NEW ===

                    // Если в маске ничего нет — просто вернуть исходник
                    if (Cv2.CountNonZero(modMask) == 0)
                        return working.Clone();


                    if (useTeleaHybrid)
                    {
                        Debug.WriteLine("Using Telea hybrid border removal");
                        // === CASE 3: Telea + защитный пояс ===

                        // 1) Outer mask — вся зона бордюра после guard
                        using var outerMask = modMask.Clone();

                        // 2) Inner mask — защищённая внутренняя часть в зоне бордюра
                        using var innerMask = modMask.Clone();
                        int innerRadius = Math.Max(1, margin / 2);
                        if (innerRadius > 0)
                        {
                            using var kInner = Cv2.GetStructuringElement(
                                MorphShapes.Rect,
                                new OpenCvSharp.Size(2 * innerRadius + 1, 2 * innerRadius + 1));
                            Cv2.Erode(innerMask, innerMask, kInner, iterations: 1);
                        }

                        // 3) Inpaint по всей зоне outerMask
                        using var inpainted = new Mat();
                        double inpaintRadius = Math.Max(3.0, margin);
                        Cv2.Inpaint(working, outerMask, inpainted, inpaintRadius, InpaintMethod.Telea);

                        // 4) Собираем финальный результат
                        filled = working.Clone();

                        // 4.1. Внешняя часть бордюра — заливаем цветом бумаги (удаляем рамку)
                        filled.SetTo(chosenBg, outerMask);

                        // 4.2. Внутренняя часть (innerMask) — копируем Telea-результат,
                        // чтобы восстановить/смягчить текст и контент, прилегающий к бордюру
                        inpainted.CopyTo(filled, innerMask);

                        // 4.3. Твой зелёный debug по исходной selectedMask (оставляем!)
                        //filled.SetTo(new Scalar(0, 255, 0), selectedMask);

                        return filled;
                    }

                    filled.SetTo(chosenBg, modMask);

                    //filled.SetTo(new Scalar(0, 255, 0), selectedMask); // bright green

                    //4) smooth the seam: create blurred(soft) mask and do local per-pixel blend in ROI
                    //create blurred mask(CV_8U -> blurred uchar)
                    using var blurred = new Mat();
                    int ksize = Math.Max(3, (featherPx / 2) * 2 + 1);
                    Cv2.GaussianBlur(modMask, blurred, new OpenCvSharp.Size(ksize, ksize), 0);

                    // compute bounding box of blurred mask (scan for nonzero)
                    int top = -1, bottom = -1, left = -1, right = -1;
                    for (int r = 0; r < rows; r++)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            if (blurred.At<byte>(r, c) != 0)
                            {
                                if (top == -1 || r < top) top = r;
                                if (bottom == -1 || r > bottom) bottom = r;
                                if (left == -1 || c < left) left = c;
                                if (right == -1 || c > right) right = c;
                            }
                        }
                    }

                    // if no nonzero found (should not), return filled
                    if (top == -1) return filled.Clone();

                    // expand ROI a bit
                    top = Math.Max(0, top - featherPx);
                    bottom = Math.Min(rows - 1, bottom + featherPx);
                    left = Math.Max(0, left - featherPx);
                    right = Math.Min(cols - 1, right + featherPx);

                    // per-pixel blend inside ROI using blurred mask as alpha (0..255)
                    for (int r = top; r <= bottom; r++)
                    {
                        for (int c = left; c <= right; c++)
                        {
                            byte a = blurred.At<byte>(r, c); // 0..255
                            if (a == 0) continue; // no change
                            if (a == 255)
                            {
                                // fully filled already
                                // ensure pixel in result is bg (it is because filled.SetTo done)
                                continue;
                            }
                            // alpha normalized
                            double alpha = a / 255.0;
                            var origB = working.At<Vec3b>(r, c);
                            var fillB = filled.At<Vec3b>(r, c);
                            byte nb = (byte)Math.Round(fillB.Item0 * alpha + origB.Item0 * (1 - alpha));
                            byte ng = (byte)Math.Round(fillB.Item1 * alpha + origB.Item1 * (1 - alpha));
                            byte nr = (byte)Math.Round(fillB.Item2 * alpha + origB.Item2 * (1 - alpha));
                            // write to filled result
                            filled.Set<Vec3b>(r, c, new Vec3b(nb, ng, nr));
                            //filled.Set<Vec3b>(r, c, new Vec3b(0, 0, 255));
                        }
                    }


                    return filled;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                finally
                {
                    if (disposeWorking && working != null) working.Dispose();
                }
        }

        public static Mat RemoveBordersByRowColWhite(CancellationToken token, Mat src,
                                                    double threshFrac = 0.60,
                                                    int contrastThr = 30,
                                                    double centralSample = 0.30,
                                                    double maxRemoveFrac = 0.25)
        {
            //Debug.WriteLine("RemoveBordersByRowColWhite started. Before checking _currentImage");
            if (src == null || src.Empty())
                return null;

            //Debug.WriteLine("RemoveBordersByRowColWhite started.");

            // Подготовка источника (убедиться, что BGR CV_8UC3)
            //Mat src = _currentImage;
            Mat srcBgr = src;
            bool converted = false;
            try
            {
                if (src.Type() == MatType.CV_8UC3)
                {
                    // already BGR
                }
                else if (src.Type() == MatType.CV_8UC4)
                {
                    srcBgr = new Mat();
                    Cv2.CvtColor(src, srcBgr, ColorConversionCodes.BGRA2BGR);
                    converted = true;
                }
                else if (src.Type() == MatType.CV_8UC1)
                {
                    srcBgr = new Mat();
                    Cv2.CvtColor(src, srcBgr, ColorConversionCodes.GRAY2BGR);
                    converted = true;
                }
                else
                {
                    throw new InvalidOperationException("Unsupported image type for border removal");
                }

                // Грейскейл
                Mat gray = new Mat();
                Cv2.CvtColor(srcBgr, gray, ColorConversionCodes.BGR2GRAY);
                token.ThrowIfCancellationRequested();

                int h = gray.Rows;
                int w = gray.Cols;

                // Ограничим входные параметры
                centralSample = Math.Max(0.05, Math.Min(0.9, centralSample));
                threshFrac = Math.Max(0.01, Math.Min(0.99, threshFrac));
                maxRemoveFrac = Math.Max(0.01, Math.Min(0.5, maxRemoveFrac));

                // Центральный прямоугольник для оценки медианы
                int cx0 = (int)Math.Round(w * (0.5 - centralSample / 2.0));
                int cy0 = (int)Math.Round(h * (0.5 - centralSample / 2.0));
                int cx1 = (int)Math.Round(w * (0.5 + centralSample / 2.0));
                int cy1 = (int)Math.Round(h * (0.5 + centralSample / 2.0));
                cx0 = Math.Max(0, Math.Min(w - 1, cx0));
                cy0 = Math.Max(0, Math.Min(h - 1, cy0));
                cx1 = Math.Max(cx0 + 1, Math.Min(w, cx1));
                cy1 = Math.Max(cy0 + 1, Math.Min(h, cy1));

                Mat central = new Mat(gray, new Rect(cx0, cy0, cx1 - cx0, cy1 - cy0));
                int centralMedian = ComputeMatMedian(central);

                // Считаем количество "не-фон" пикселей в каждой строке и колонке
                int[] rowCounts = new int[h];
                int[] colCounts = new int[w];

                for (int y = 0; y < h; y++)
                {
                    token.ThrowIfCancellationRequested();
                    for (int x = 0; x < w; x++)
                    {
                        byte v = gray.At<byte>(y, x);
                        if (Math.Abs(v - centralMedian) > contrastThr)
                        {
                            rowCounts[y]++;
                            colCounts[x]++;
                        }
                    }
                }

                // Фракции
                double[] rowFrac = new double[h];
                double[] colFrac = new double[w];
                for (int y = 0; y < h; y++) rowFrac[y] = (double)rowCounts[y] / w;
                for (int x = 0; x < w; x++) colFrac[x] = (double)colCounts[x] / h;

                // Сканируем от краёв
                int top = 0;
                for (int y = 0; y < h; y++)
                {
                    if (rowFrac[y] > threshFrac) top = y + 1;
                    else break;
                }
                int bottom = 0;
                for (int y = h - 1; y >= 0; y--)
                {
                    if (rowFrac[y] > threshFrac) bottom = h - y;
                    else break;
                }
                int left = 0;
                for (int x = 0; x < w; x++)
                {
                    if (colFrac[x] > threshFrac) left = x + 1;
                    else break;
                }
                int right = 0;
                for (int x = w - 1; x >= 0; x--)
                {
                    if (colFrac[x] > threshFrac) right = w - x;
                    else break;
                }


                // Защита от удаления слишком большой доли
                int maxTop = (int)Math.Round(maxRemoveFrac * h);
                int maxSide = (int)Math.Round(maxRemoveFrac * w);
                if (top > maxTop) top = maxTop;
                if (bottom > maxTop) bottom = maxTop;
                if (left > maxSide) left = maxSide;
                if (right > maxSide) right = maxSide;


                // Применяем заливку белым (in-place в новом Mat)
                Mat result = srcBgr.Clone();
                Scalar white = new Scalar(255, 255, 255);
                if (top > 0) result[new Rect(0, 0, w, top)].SetTo(white);
                if (bottom > 0) result[new Rect(0, h - bottom, w, bottom)].SetTo(white);
                if (left > 0) result[new Rect(0, 0, left, h)].SetTo(white);
                if (right > 0) result[new Rect(w - right, 0, right, h)].SetTo(white);

                central.Dispose();
                gray.Dispose();
                if (converted) srcBgr.Dispose();

                return result;
                // trying to crop instead of fill
                //int row0 = top;
                //int row1 = h - bottom;
                //int col0 = left;
                //int col1 = w - right;
                //if (row1 <= row0 || col1 <= col0) return new Mat();
                //result = srcBgr.RowRange(row0, row1).ColRange(col0, col1).Clone();


                //// Заменяем поле _currentImage на result (освобождая прежний Mat)
                ////WorkingImage = result;

                //// Освобождаем временные объекты
                //central.Dispose();
                //gray.Dispose();
                //if (converted) srcBgr.Dispose(); // если создали новый Mat при конвертации
                ////old?.Dispose();

                //// (опционально) логирование — можно убрать
                //Debug.WriteLine($"RemoveBordersByRowColWhite applied: cuts(top,bottom,left,right) = ({top},{bottom},{left},{right}), centralMedian={centralMedian}");
                //return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                if (converted && srcBgr != null)
                    srcBgr.Dispose();
            }
        }

        /// <summary>
        /// Вспомогательная: медиана значений CV_8UC1 Mat.
        /// </summary>
        private static int ComputeMatMedian(Mat grayMat)
        {
            if (grayMat == null || grayMat.Empty()) return 255;
            if (grayMat.Type() != MatType.CV_8UC1)
            {
                using var tmp = new Mat();
                Cv2.CvtColor(grayMat, tmp, ColorConversionCodes.BGR2GRAY);
                grayMat = tmp;
            }

            List<byte> list = new List<byte>(grayMat.Rows * grayMat.Cols);
            for (int y = 0; y < grayMat.Rows; y++)
            {
                for (int x = 0; x < grayMat.Cols; x++)
                {
                    list.Add(grayMat.At<byte>(y, x));
                }
            }

            list.Sort();
            int mid = list.Count / 2;
            if (list.Count == 0) return 255;
            if (list.Count % 2 == 1) return list[mid];
            return (list[mid - 1] + list[mid]) / 2;
        }


        public static Mat? ManualCut(CancellationToken token,
                                    Mat src,
                                    int x, int y, int w, int h,
                                    BordersRemovalMode mode = BordersRemovalMode.Fill,
                                    bool debug = true)
        {
            Debug.WriteLine("Manual cut");

            token.ThrowIfCancellationRequested();
            if (src == null || src.Empty())
                return null;

            Mat srcBgr = src;
            bool converted = false;

            
            try
            {
                if (src.Type() == MatType.CV_8UC4)
                {
                    // already BGRA
                }
                else if (src.Type() == MatType.CV_8UC3)
                {
                    srcBgr = new Mat();
                    Cv2.CvtColor(src, srcBgr, ColorConversionCodes.BGR2BGRA);
                    converted = true;
                }
                else if (src.Type() == MatType.CV_8UC1)
                {
                    srcBgr = new Mat();
                    Cv2.CvtColor(src, srcBgr, ColorConversionCodes.GRAY2BGRA);
                    converted = true;
                }
                else
                {
                    throw new ArgumentException(
                        $"ManualCut supports only 8-bit 1/3/4-channel Mats. Got {src.Type()}",
                        nameof(src));
                }
                int rows = src.Rows;
                int cols = src.Cols;

                x = Math.Max(0, Math.Min(cols - 1, x));
                y = Math.Max(0, Math.Min(rows - 1, y));
                w = Math.Max(1, Math.Min(cols - x, w));
                h = Math.Max(1, Math.Min(rows - y, h));
                var roi = new Rect(x, y, w, h);

                Mat result = srcBgr.Clone();

                if (debug)
                {
                    using var mask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(255));
                    // 0 inside ROI -> overlay will apply OUTSIDE ROI
                    Cv2.Rectangle(mask, roi, Scalar.All(0), thickness: -1);

                    using var overlay = result.Clone();
                    overlay.SetTo(new Scalar(0, 0, 255, 255), mask); // red outside ROI

                    const double alpha = 0.3; // 0 = transparent, 1 = full red
                    Cv2.AddWeighted(overlay, alpha, result, 1.0 - alpha, 0, result);

                    // In debug mode we ONLY show this preview, no actual cutting/filling
                    return result;

                }
                if (mode == BordersRemovalMode.Fill)
                {
                    // FILL: залить ВСЁ ВНЕ ROI указанным цветом
                    // сюда подставь свой способ получить цвет: GetBgColor(srcBgr) или параметр

                    using var pageMask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(0));
                    Cv2.Rectangle(pageMask, roi, Scalar.All(255), thickness: -1);

                    Scalar fillColor = EstimatePageFillColor(src, pageMask);

                    using var borderMask = new Mat(src.Size(), MatType.CV_8UC1, Scalar.All(255));
                    // 0 внутри ROI → маска = бордюры
                    Cv2.Rectangle(borderMask, roi, Scalar.All(0), thickness: -1);


                    result.SetTo(fillColor, borderMask);
                    // результат: ROI как был, всё снаружи залито fillColor
                }
                else // BordersRemovalMode.Cut (или любой другой не-Fill)
                {
                    // CUT: обрезать до ROI
                    result = new Mat(result, roi).Clone();
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            finally
            {
                if (converted && srcBgr != null)
                    srcBgr.Dispose();
            }

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
            Mat color = src;
            bool ownsColor = false;
            if (src.Type() == MatType.CV_8UC3)
            {
                // already BGR
            }
            else if (src.Type() == MatType.CV_8UC4)
            {
                color = new Mat();
                Cv2.CvtColor(src, color, ColorConversionCodes.BGRA2BGR);
                ownsColor = true;
            }
            else if (src.Type() == MatType.CV_8UC1)
            {
                color = new Mat();
                Cv2.CvtColor(src, color, ColorConversionCodes.GRAY2BGR);
                ownsColor = true;
            }
            else
            {
                throw new ArgumentException("src must be CV_8UC1, CV_8UC3 or CV_8UC4", nameof(src));
            }

            try
            {
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
            catch (Exception)
            {
                return new Scalar(255, 255, 255);
            }
            finally
            {
                if (ownsColor && color != null)
                    color.Dispose();

            }
        }

        /// <summary>
        /// Оценивает один кирпич в Lab: средний цвет, текстуру и то,
        /// является ли он "сильным" бордюром (явно отличается от страницы).
        /// </summary>
        private static (bool strongBorder,
                        double meanL,
                        double meanA,
                        double meanB,
                        double stdL,
                        double distToPage)
            EvaluateBrickLab(
                Mat L, Mat A, Mat B,
                Rect rect,
                double pageL, double pageA, double pageB,
                double colorDistStrongThr,
                double LDiffStrongThr,
                double textureThr)
        {
            using var roiL = new Mat(L, rect);
            using var roiA = new Mat(A, rect);
            using var roiB = new Mat(B, rect);

            Cv2.MeanStdDev(roiL, out var mL, out var sL);
            var mA = Cv2.Mean(roiA);
            var mB = Cv2.Mean(roiB);

            double Lmean = mL.Val0;
            double LstdLocal = sL.Val0;
            double Amean = mA.Val0;
            double Bmean = mB.Val0;

            double dL = Lmean - pageL;
            double da = Amean - pageA;
            double db = Bmean - pageB;
            double distToPage = Math.Sqrt(dL * dL + da * da + db * db);

            bool strongBorder =
                (distToPage >= colorDistStrongThr || Math.Abs(dL) >= LDiffStrongThr) &&
                LstdLocal <= textureThr;

            return (strongBorder, Lmean, Amean, Bmean, LstdLocal, distToPage);
        }

        private enum BorderSide { Left, Right, Top, Bottom }

        private static bool TryComputeSideBorderReference(CancellationToken token,
            BorderSide side,
            Mat L, Mat A, Mat B,
            int rows, int cols,
            double pageL, double pageA, double pageB,
            double colorDistStrongThr,
            double LDiffStrongThr,
            double textureThr,
            int probeBrick,
            out double refL, out double refA, out double refB, out double baseDiff)
        {
            refL = refA = refB = baseDiff = 0;

            int edgeDepth = 3; // small strip at the very edge for reference
            edgeDepth = Math.Min(edgeDepth, (side == BorderSide.Left || side == BorderSide.Right) ? cols : rows);
            if (edgeDepth <= 0) return false;

            // weaker seeding than strongBorder (for low-contrast borders)
            double weakSeedDist = Math.Max(3.5, colorDistStrongThr * 0.45);
            double weakSeedLDiff = Math.Max(2.0, LDiffStrongThr * 0.35);
            double texCap = textureThr * 1.25;

            double sumL = 0, sumA = 0, sumB = 0, sumDiff = 0;
            int accept = 0, total = 0;

            if (side == BorderSide.Left || side == BorderSide.Right)
            {
                token.ThrowIfCancellationRequested();
                int x = (side == BorderSide.Left) ? 0 : Math.Max(0, cols - edgeDepth);

                for (int y0 = 0; y0 < rows; y0 += probeBrick)
                {
                    int y1 = Math.Min(rows, y0 + probeBrick);
                    int bandH = y1 - y0;
                    if (bandH <= 0) continue;

                    var rect = new Rect(x, y0, edgeDepth, bandH);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(L, A, B, rect, pageL, pageA, pageB, colorDistStrongThr, LDiffStrongThr, textureThr);

                    total++;

                    bool seedCandidate =
                        (strongBorder || distToPage >= weakSeedDist || Math.Abs(Lm - pageL) >= weakSeedLDiff) &&
                        (Lsigma <= texCap);

                    if (seedCandidate)
                    {
                        accept++;
                        sumL += Lm; sumA += Am; sumB += Bm;
                        sumDiff += distToPage;
                    }
                }
            }
            else
            {
                int y = (side == BorderSide.Top) ? 0 : Math.Max(0, rows - edgeDepth);

                for (int x0 = 0; x0 < cols; x0 += probeBrick)
                {
                    token.ThrowIfCancellationRequested();
                    int x1 = Math.Min(cols, x0 + probeBrick);
                    int bandW = x1 - x0;
                    if (bandW <= 0) continue;

                    var rect = new Rect(x0, y, bandW, edgeDepth);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(L, A, B, rect, pageL, pageA, pageB, colorDistStrongThr, LDiffStrongThr, textureThr);

                    total++;

                    bool seedCandidate =
                        (strongBorder || distToPage >= weakSeedDist || Math.Abs(Lm - pageL) >= weakSeedLDiff) &&
                        (Lsigma <= texCap);

                    if (seedCandidate)
                    {
                        accept++;
                        sumL += Lm; sumA += Am; sumB += Bm;
                        sumDiff += distToPage;
                    }
                }
            }

            // Need enough accepted bricks so a single stamp/text corner doesn't become "the border ref"
            int minAccept = Math.Max(4, total / 8);
            if (accept < minAccept) return false;

            refL = sumL / accept;
            refA = sumA / accept;
            refB = sumB / accept;
            baseDiff = Math.Max(4.0, sumDiff / accept);
            return true;
        }

        private static int EstimateMaxBorderDepthForSide(CancellationToken token,
            BorderSide side,
            Mat L, Mat A, Mat B,
            int rows, int cols,
            double pageL, double pageA, double pageB,
            double colorDistStrongThr,
            double LDiffStrongThr,
            double textureThr,
            double bordersColorTolerance,
            int brickThickness,
            int maxDepthCapPx)
        {
            // “medium brick thickness” probe (independent from UI precision)
            int probeBrick = Math.Max(12, Math.Min(48, brickThickness * 3));

            if (maxDepthCapPx <= 0)
                maxDepthCapPx = (side == BorderSide.Left || side == BorderSide.Right) ? cols / 2 : rows / 2;

            maxDepthCapPx = Math.Max(brickThickness, Math.Min(maxDepthCapPx,
                (side == BorderSide.Left || side == BorderSide.Right) ? cols / 2 : rows / 2));

            if (!TryComputeSideBorderReference(token,
                    side, L, A, B, rows, cols,
                    pageL, pageA, pageB,
                    colorDistStrongThr, LDiffStrongThr, textureThr,
                    probeBrick,
                    out double refL, out double refA, out double refB, out double baseDiff))
            {
                // If we cannot even seed a reference safely — keep cap small (prevents “eating” handwriting)
                return Math.Max(brickThickness, Math.Min(maxDepthCapPx, probeBrick));
            }

            // tolerance: allow border to drift (gradient / uneven lighting)
            // IMPORTANT: add a floor, otherwise “gray border” baseDiff is small and tolerance becomes too strict.
            double allowedDelta = 8.0 + baseDiff * bordersColorTolerance;

            // A weaker “still border-ish” check (in addition to ref distance)
            double weakStillBorderDist = Math.Max(3.0, baseDiff * 0.35);
            double texCap = textureThr * 1.25;

            // coverage threshold: how much of the side must look “border-ish” at this depth
            double coverageThr = 0.20 + (1.0 - bordersColorTolerance) * 0.15; // ~0.20..0.35

            // depth step (coarse) – we only estimate maxDepth, not build mask here
            int depthStep = Math.Max(1, Math.Min(6, probeBrick / 8));

            int maxGap = (bordersColorTolerance < 0.3) ? 0 : (bordersColorTolerance < 0.6 ? 1 : 2);

            int lastGood = 0;
            int gapRun = 0;

            if (side == BorderSide.Left || side == BorderSide.Right)
            {
                for (int d = 0; d < maxDepthCapPx; d += depthStep)
                {
                    token.ThrowIfCancellationRequested();
                    int borderCnt = 0, totalCnt = 0;

                    int x = (side == BorderSide.Left) ? d : (cols - d - depthStep);
                    x = Math.Max(0, Math.Min(cols - depthStep, x));

                    for (int y0 = 0; y0 < rows; y0 += probeBrick)
                    {
                        int y1 = Math.Min(rows, y0 + probeBrick);
                        int bandH = y1 - y0;
                        if (bandH <= 0) continue;

                        var rect = new Rect(x, y0, depthStep, bandH);

                        var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                            EvaluateBrickLab(L, A, B, rect, pageL, pageA, pageB, colorDistStrongThr, LDiffStrongThr, textureThr);

                        totalCnt++;

                        double dLRef = Lm - refL;
                        double daRef = Am - refA;
                        double dbRef = Bm - refB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        bool borderish =
                            (Lsigma <= texCap) &&
                            (strongBorder || distToRef <= allowedDelta || distToPage >= weakStillBorderDist);

                        if (borderish) borderCnt++;
                    }

                    double coverage = (totalCnt > 0) ? (borderCnt / (double)totalCnt) : 0.0;

                    if (coverage >= coverageThr)
                    {
                        lastGood = d + depthStep;
                        gapRun = 0;
                    }
                    else if (lastGood > 0)
                    {
                        gapRun++;
                        if (gapRun > maxGap) break;
                    }
                }
            }
            else
            {
                for (int d = 0; d < maxDepthCapPx; d += depthStep)
                {
                    token.ThrowIfCancellationRequested();
                    int borderCnt = 0, totalCnt = 0;

                    int y = (side == BorderSide.Top) ? d : (rows - d - depthStep);
                    y = Math.Max(0, Math.Min(rows - depthStep, y));

                    for (int x0 = 0; x0 < cols; x0 += probeBrick)
                    {
                        int x1 = Math.Min(cols, x0 + probeBrick);
                        int bandW = x1 - x0;
                        if (bandW <= 0) continue;

                        var rect = new Rect(x0, y, bandW, depthStep);

                        var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                            EvaluateBrickLab(L, A, B, rect, pageL, pageA, pageB, colorDistStrongThr, LDiffStrongThr, textureThr);

                        totalCnt++;

                        double dLRef = Lm - refL;
                        double daRef = Am - refA;
                        double dbRef = Bm - refB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        bool borderish =
                            (Lsigma <= texCap) &&
                            (strongBorder || distToRef <= allowedDelta || distToPage >= weakStillBorderDist);

                        if (borderish) borderCnt++;
                    }

                    double coverage = (totalCnt > 0) ? (borderCnt / (double)totalCnt) : 0.0;

                    if (coverage >= coverageThr)
                    {
                        lastGood = d + depthStep;
                        gapRun = 0;
                    }
                    else if (lastGood > 0)
                    {
                        gapRun++;
                        if (gapRun > maxGap) break;
                    }
                }
            }

            lastGood = Math.Max(brickThickness, Math.Min(maxDepthCapPx, lastGood));
            return lastGood;
        }

        private static void ComputeMaxBorderDepthHorizontal(CancellationToken token,
            Mat L, Mat A, Mat B,
            int rows, int cols,
            double pageL, double pageA, double pageB,
            double colorDistStrongThr,
            double LDiffStrongThr,
            double textureThr,
            double bordersColorTolerance,
            int brickThickness,
            int maxDepthCapX,
            out int maxLeft,
            out int maxRight)
        {
            maxLeft = EstimateMaxBorderDepthForSide(token,
                BorderSide.Left, L, A, B, rows, cols,
                pageL, pageA, pageB,
                colorDistStrongThr, LDiffStrongThr, textureThr,
                bordersColorTolerance,
                brickThickness,
                maxDepthCapX);

            maxRight = EstimateMaxBorderDepthForSide(token,
                BorderSide.Right, L, A, B, rows, cols,
                pageL, pageA, pageB,
                colorDistStrongThr, LDiffStrongThr, textureThr,
                bordersColorTolerance,
                brickThickness,
                maxDepthCapX);
        }

        private static void ComputeMaxBorderDepthVertical(CancellationToken token,
            Mat L, Mat A, Mat B,
            int rows, int cols,
            double pageL, double pageA, double pageB,
            double colorDistStrongThr,
            double LDiffStrongThr,
            double textureThr,
            double bordersColorTolerance,
            int brickThickness,
            int maxDepthCapY,
            out int maxTop,
            out int maxBottom)
        {
            maxTop = EstimateMaxBorderDepthForSide(token,
                BorderSide.Top, L, A, B, rows, cols,
                pageL, pageA, pageB,
                colorDistStrongThr, LDiffStrongThr, textureThr,
                bordersColorTolerance,
                brickThickness,
                maxDepthCapY);

            maxBottom = EstimateMaxBorderDepthForSide(token,
                BorderSide.Bottom, L, A, B, rows, cols,
                pageL, pageA, pageB,
                colorDistStrongThr, LDiffStrongThr, textureThr,
                bordersColorTolerance,
                brickThickness,
                maxDepthCapY);
        }


        /// <summary>
        /// Максимальная глубина поиска бордюра слева/справа (по оси X).
        /// Для горизонтальных бордюров используем долю от ширины.
        /// </summary>
        //private static int ComputeMaxBorderDepthHorizontal(int cols, int brickThickness,
        //                                                   double maxBorderFraction = 0.40,
        //                                                   int maxBricks = 60)
        //{
        //    if (brickThickness <= 0)
        //        brickThickness = 8;

        //    // Не лезем глубже, чем maxBorderFraction от ширины
        //    int maxDepthBySize = (int)Math.Round(cols * maxBorderFraction);

        //    // И не лезем глубже, чем maxBricks "кирпичей" по глубине
        //    int maxDepthByBricks = brickThickness * maxBricks;

        //    int maxDepth = Math.Max(
        //        brickThickness,                     // минимум: один кирпич
        //        Math.Min(maxDepthBySize, maxDepthByBricks)
        //    );

        //    return maxDepth;
        //}

        ///// <summary>
        ///// Максимальная глубина поиска бордюра сверху/снизу (по оси Y).
        ///// Для вертикальных бордюров используем долю от высоты.
        ///// </summary>
        //private static int ComputeMaxBorderDepthVertical(int rows, int brickThickness,
        //                                                 double maxBorderFraction = 0.25,
        //                                                 int maxBricks = 14)
        //{
        //    if (brickThickness <= 0)
        //        brickThickness = 8;

        //    // Не лезем глубже, чем maxBorderFraction от высоты
        //    int maxDepthBySize = (int)Math.Round(rows * maxBorderFraction);

        //    // И не лезем глубже, чем maxBricks "кирпичей" по глубине
        //    int maxDepthByBricks = brickThickness * maxBricks;

        //    int maxDepth = Math.Max(
        //        brickThickness,
        //        Math.Min(maxDepthBySize, maxDepthByBricks)
        //    );

        //    return maxDepth;
        //}

        private static void SmoothDepthArray(int[] depth, int length, int kernelRadius)
        {
            if (depth == null || length <= 0 || kernelRadius <= 0)
                return;

            var tmp = new double[length];

            for (int i = 0; i < length; i++)
            {
                int di = depth[i];
                if (di <= 0)
                {
                    tmp[i] = 0;
                    continue;
                }

                int start = Math.Max(0, i - kernelRadius);
                int end = Math.Min(length - 1, i + kernelRadius);

                double sum = 0;
                int count = 0;

                for (int j = start; j <= end; j++)
                {
                    int dj = depth[j];
                    if (dj > 0)
                    {
                        sum += dj;
                        count++;
                    }
                }

                if (count > 0)
                    tmp[i] = sum / count;
                else
                    tmp[i] = di;
            }

            for (int i = 0; i < length; i++)
            {
                depth[i] = (int)Math.Round(tmp[i]);
            }
        }

        public struct MaxBorderDepthsFrac
        {
            public double Left;
            public double Right;
            public double Top;
            public double Bottom;
        }

        public static Mat RemoveBorders_LabBricks(CancellationToken token,
    Mat src,
    int brickThickness,
    double bordersColorTolerance,
    int safetyOffsetPx,
    BrickInpaintMode inpaintMode,
    double inpaintRadius,
    bool maxBordersDepthsAutoDetection,
    MaxBorderDepthsFrac maxBorderDepthsFrac,
    double seedContrastFactor,
    double seedBrightnessFactor,
    double textureAllowanceFactor,
    int kInterpolation,
    Scalar? fillColor = null)
        {
            Debug.WriteLine($"Bricj thickness: {brickThickness}");

            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

            bordersColorTolerance = Math.Max(0, Math.Min(1.0, bordersColorTolerance));

            // 0) Нормализуем к BGR
            Mat bgr = src;
            bool disposeBgr = false;
            if (src.Type() != MatType.CV_8UC3)
            {
                bgr = new Mat();
                if (src.Channels() == 1)
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
                else if (src.Channels() == 4)
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
                else
                    src.ConvertTo(bgr, MatType.CV_8UC3);
                disposeBgr = true;
            }

            int rows = bgr.Rows;
            int cols = bgr.Cols;
            if (rows < 40 || cols < 40)
                return bgr.Clone();

            // interpolation arrays
            int[] leftDepth = new int[rows];
            int[] rightDepth = new int[rows];
            int[] topDepth = new int[cols];
            int[] bottomDepth = new int[cols];


            brickThickness = Math.Max(1, brickThickness);

            // 1) BGR -> Lab
            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);
            var ch = lab.Split();
            using var L = ch[0];
            using var A = ch[1];
            using var B = ch[2];

            // 2) Цвет страницы по центру
            int margin = Math.Max(brickThickness * 2, Math.Min(rows, cols) / 10);
            margin = Math.Min(margin, Math.Min(rows, cols) / 4);

            var innerRect = new Rect(
                margin,
                margin,
                cols - 2 * margin,
                rows - 2 * margin);
            if (innerRect.Width <= 0 || innerRect.Height <= 0)
                innerRect = new Rect(0, 0, cols, rows);

            using var innerL = new Mat(L, innerRect);
            using var innerA = new Mat(A, innerRect);
            using var innerB = new Mat(B, innerRect);

            Cv2.MeanStdDev(innerL, out var meanL, out var stdL);
            Scalar meanA = Cv2.Mean(innerA);
            Scalar meanB = Cv2.Mean(innerB);
            
            double pageL = meanL.Val0;
            double pageA = meanA.Val0;
            double pageB = meanB.Val0;
            double pageLStd = Math.Max(4.0, stdL.Val0);

            // TODO: MOVE THIS PARAMETRS OUT TO UI!!!!!!!!!!!!!!!!!!!!!

            // "сильный" бордюр (seed)
            //double colorDistStrongThr = 8.0 + 0.3 * pageLStd;
            //double LDiffStrongThr = Math.Max(4.0, 0.8 * pageLStd);
            //double textureThr = pageLStd * 0.7;

            seedContrastFactor = Math.Max(0.3, Math.Min(3.0, seedContrastFactor));
            seedBrightnessFactor = Math.Max(0.3, Math.Min(3.0, seedBrightnessFactor));
            textureAllowanceFactor = Math.Max(0.3, Math.Min(3.0, textureAllowanceFactor));

            double colorDistStrongThr = (8.0 + 0.3 * pageLStd) * seedContrastFactor;
            double LDiffStrongThr = Math.Max(4.0, 0.8 * pageLStd) * seedBrightnessFactor;
            double textureThr = (pageLStd * 0.7) * textureAllowanceFactor;

            //int maxDepth = Math.Min(Math.Min(rows, cols) / 3, brickThickness * 16);
            //maxDepth = Math.Max(brickThickness, maxDepth);
            //int maxDepth = ComputeMaxBorderDepth(rows, cols, brickThickness);
            //int maxDepthX = ComputeMaxBorderDepthHorizontal(cols, brickThickness);
            //int maxDepthY = ComputeMaxBorderDepthVertical(rows, brickThickness);

            //int maxDepthCapX = ComputeMaxBorderDepthHorizontal(cols, brickThickness); // keep as safety cap
            //int maxDepthCapY = ComputeMaxBorderDepthVertical(rows, brickThickness);   // keep as safety cap
            int maxDepthCapX = cols / 2;
            int maxDepthCapY = rows / 2;

            int maxDepthLeftBorder      =   (int)(cols * maxBorderDepthsFrac.Left);
            int maxDepthRightBorder     =   (int)(cols * maxBorderDepthsFrac.Right);
            int maxDepthBottomBorder    =   (int)(rows * maxBorderDepthsFrac.Bottom);
            int maxDepthTopBorder       =   (int)(rows * maxBorderDepthsFrac.Top);

            if (maxBordersDepthsAutoDetection)
            {
                ComputeMaxBorderDepthHorizontal(token,
                L, A, B, rows, cols,
                pageL, pageA, pageB,
                colorDistStrongThr, LDiffStrongThr, textureThr,
                bordersColorTolerance,
                brickThickness,                                                    // possibly use brickThickness here?
                maxDepthCapX,
                out int maxDepthLeft,
                out int maxDepthRight);
                

                ComputeMaxBorderDepthVertical(token,
                    L, A, B, rows, cols,
                    pageL, pageA, pageB,
                    colorDistStrongThr, LDiffStrongThr, textureThr,
                    bordersColorTolerance,
                    brickThickness,                                                  // possibly use brickThickness here?
                    maxDepthCapY,
                    out int maxDepthTop,
                    out int maxDepthBottom);

                maxDepthLeftBorder = maxDepthLeft;
                maxDepthRightBorder = maxDepthRight;
                maxDepthTopBorder = maxDepthTop;
                maxDepthBottomBorder = maxDepthBottom;
            }




            // Максимальное количество подряд "сомнительных" полос внутри бордюра (для градиента)
            int maxNonBorderRun = (bordersColorTolerance < 0.3)
                ? 0
                : (bordersColorTolerance < 0.7 ? 1 : 2);

            // 3) Маска бордюра
            using var borderMask = new Mat(rows, cols, MatType.CV_8UC1, Scalar.All(0));

            int shrink = safetyOffsetPx;
            // =============== 4) LEFT / RIGHT – кирпичи высотой brickThickness ===============

            for (int y0 = 0; y0 < rows; y0 += brickThickness)
            {
                token.ThrowIfCancellationRequested();
                int y1 = Math.Min(rows, y0 + brickThickness);
                int bandH = y1 - y0;

                //int maxSearchX = Math.Min(cols / 2, maxDepthX);
                int maxSearchXLeft = Math.Min(cols / 2, maxDepthLeftBorder);
                int maxSearchXRight = Math.Min(cols / 2, maxDepthRightBorder);

                // ----- LEFT -----
                int lastBorderX = -1;
                int nonBorderRun = 0;

                // опорный цвет бордюра для ЭТОГО вертикального сегмента
                bool hasLeftRef = false;
                double leftRefL = 0, leftRefA = 0, leftRefB = 0, leftBaseDiff = 0;

                for (int x = 0; x < maxSearchXLeft; x++)
                {
                    var rect = new Rect(x, y0, 1, bandH);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(
                            L, A, B,
                            rect,
                            pageL, pageA, pageB,
                            colorDistStrongThr, LDiffStrongThr, textureThr);

                    bool isBorderHere = false;

                    if (!hasLeftRef)
                    {
                        // ищем первый "надёжный" seed-кирпич
                        if (strongBorder)
                        {
                            hasLeftRef = true;
                            leftRefL = Lm;
                            leftRefA = Am;
                            leftRefB = Bm;
                            leftBaseDiff = Math.Max(4.0, distToPage);
                            isBorderHere = true;
                        }
                    }
                    else
                    {
                        // у нас уже есть опорный цвет бордюра → допускаем градиент по tolerance
                        double dLRef = Lm - leftRefL;
                        double daRef = Am - leftRefA;
                        double dbRef = Bm - leftRefB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        double allowedDelta = leftBaseDiff * bordersColorTolerance;
                        bool closeToBorderRef = distToRef <= allowedDelta;

                        isBorderHere =
                            (strongBorder || closeToBorderRef) &&
                            Lsigma <= textureThr;
                    }

                    if (isBorderHere)
                    {
                        lastBorderX = x;
                        nonBorderRun = 0;
                    }
                    else if (lastBorderX >= 0)
                    {
                        nonBorderRun++;
                        if (nonBorderRun > maxNonBorderRun)
                            break; // бордюр закончился
                    }
                }

                int wLeft = Math.Max(0, lastBorderX + 1 - shrink);
                if (wLeft > 0)
                {
                    //var rect = new Rect(0, y0, wLeft, bandH);
                    //using var roi = new Mat(borderMask, rect);
                    //roi.SetTo(255);
                    // interate leftDepth array for later smoothing
                    for (int y = y0; y < y1; y++)
                    {
                        if (y >= 0 && y < rows)
                            leftDepth[y] = Math.Max(leftDepth[y], wLeft);
                    }

                }

                // ----- RIGHT -----
                lastBorderX = -1;
                nonBorderRun = 0;

                bool hasRightRef = false;
                double rightRefL = 0, rightRefA = 0, rightRefB = 0, rightBaseDiff = 0;

                int startX = cols - 1;
                int minX = Math.Max(0, cols - maxSearchXRight);

                for (int x = startX; x >= minX; x--)
                {
                    var rect = new Rect(x, y0, 1, bandH);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(
                            L, A, B,
                            rect,
                            pageL, pageA, pageB,
                            colorDistStrongThr, LDiffStrongThr, textureThr);

                    bool isBorderHere = false;

                    if (!hasRightRef)
                    {
                        if (strongBorder)
                        {
                            hasRightRef = true;
                            rightRefL = Lm;
                            rightRefA = Am;
                            rightRefB = Bm;
                            rightBaseDiff = Math.Max(4.0, distToPage);
                            isBorderHere = true;
                        }
                    }
                    else
                    {
                        double dLRef = Lm - rightRefL;
                        double daRef = Am - rightRefA;
                        double dbRef = Bm - rightRefB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        double allowedDelta = rightBaseDiff * bordersColorTolerance;
                        bool closeToBorderRef = distToRef <= allowedDelta;

                        isBorderHere =
                            (strongBorder || closeToBorderRef) &&
                            Lsigma <= textureThr;
                    }

                    if (isBorderHere)
                    {
                        lastBorderX = x;
                        nonBorderRun = 0;
                    }
                    else if (lastBorderX >= 0)
                    {
                        nonBorderRun++;
                        if (nonBorderRun > maxNonBorderRun)
                            break;
                    }
                }

                int wRight = 0;
                if (lastBorderX >= 0)
                {
                    wRight = Math.Max(0, cols - lastBorderX - shrink);
                }
                if (wRight > 0)
                {
                    //var rect = new Rect(cols - wRight, y0, wRight, bandH);
                    //using var roi = new Mat(borderMask, rect);
                    //roi.SetTo(255);
                    // interate rightDepth array for later smoothing
                    for (int y = y0; y < y1; y++)
                    {
                        if (y >= 0 && y < rows)
                            rightDepth[y] = Math.Max(rightDepth[y], wRight);
                    }
                }
            }

            // =============== 5) TOP / BOTTOM – кирпичи шириной brickThickness ===============

            for (int x0 = 0; x0 < cols; x0 += brickThickness)
            {
                token.ThrowIfCancellationRequested();
                int x1 = Math.Min(cols, x0 + brickThickness);
                int bandW = x1 - x0;

                //int maxSearchY = Math.Min(rows / 2, maxDepthY);
                int maxSearchYTop = Math.Min(rows / 2, maxDepthTopBorder);
                int maxSearchYBottom = Math.Min(rows / 2, maxDepthBottomBorder);

                // ----- TOP -----
                int lastBorderY = -1;
                int nonBorderRun = 0;

                bool hasTopRef = false;
                double topRefL = 0, topRefA = 0, topRefB = 0, topBaseDiff = 0;

                for (int y = 0; y < maxSearchYTop; y++)
                {
                    var rect = new Rect(x0, y, bandW, 1);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(
                            L, A, B,
                            rect,
                            pageL, pageA, pageB,
                            colorDistStrongThr, LDiffStrongThr, textureThr);

                    bool isBorderHere = false;

                    if (!hasTopRef)
                    {
                        if (strongBorder)
                        {
                            hasTopRef = true;
                            topRefL = Lm;
                            topRefA = Am;
                            topRefB = Bm;
                            topBaseDiff = Math.Max(4.0, distToPage);
                            isBorderHere = true;
                        }
                    }
                    else
                    {
                        double dLRef = Lm - topRefL;
                        double daRef = Am - topRefA;
                        double dbRef = Bm - topRefB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        double allowedDelta = topBaseDiff * bordersColorTolerance;
                        bool closeToBorderRef = distToRef <= allowedDelta;

                        isBorderHere =
                            (strongBorder || closeToBorderRef) &&
                            Lsigma <= textureThr;
                    }

                    if (isBorderHere)
                    {
                        lastBorderY = y;
                        nonBorderRun = 0;
                    }
                    else if (lastBorderY >= 0)
                    {
                        nonBorderRun++;
                        if (nonBorderRun > maxNonBorderRun)
                            break;
                    }
                }

                int hTop = Math.Max(0, lastBorderY + 1 - shrink);
                if (hTop > 0)
                {
                    //var rect = new Rect(x0, 0, bandW, hTop);
                    //using var roi = new Mat(borderMask, rect);
                    //roi.SetTo(255);

                    // interate topDepth array for later smoothing
                    for (int x = x0; x < x1; x++)
                    {
                        if (x >= 0 && x < cols)
                            topDepth[x] = Math.Max(topDepth[x], hTop);
                    }
                }

                // ----- BOTTOM -----
                lastBorderY = -1;
                nonBorderRun = 0;

                bool hasBottomRef = false;
                double bottomRefL = 0, bottomRefA = 0, bottomRefB = 0, bottomBaseDiff = 0;

                int startY = rows - 1;
                int minY = Math.Max(0, rows - maxSearchYBottom);

                for (int y = startY; y >= minY; y--)
                {
                    var rect = new Rect(x0, y, bandW, 1);

                    var (strongBorder, Lm, Am, Bm, Lsigma, distToPage) =
                        EvaluateBrickLab(
                            L, A, B,
                            rect,
                            pageL, pageA, pageB,
                            colorDistStrongThr, LDiffStrongThr, textureThr);

                    bool isBorderHere = false;

                    if (!hasBottomRef)
                    {
                        if (strongBorder)
                        {
                            hasBottomRef = true;
                            bottomRefL = Lm;
                            bottomRefA = Am;
                            bottomRefB = Bm;
                            bottomBaseDiff = Math.Max(4.0, distToPage);
                            isBorderHere = true;
                        }
                    }
                    else
                    {
                        double dLRef = Lm - bottomRefL;
                        double daRef = Am - bottomRefA;
                        double dbRef = Bm - bottomRefB;
                        double distToRef = Math.Sqrt(dLRef * dLRef + daRef * daRef + dbRef * dbRef);

                        double allowedDelta = bottomBaseDiff * bordersColorTolerance;
                        bool closeToBorderRef = distToRef <= allowedDelta;

                        isBorderHere =
                            (strongBorder || closeToBorderRef) &&
                            Lsigma <= textureThr;
                    }

                    if (isBorderHere)
                    {
                        lastBorderY = y;
                        nonBorderRun = 0;
                    }
                    else if (lastBorderY >= 0)
                    {
                        nonBorderRun++;
                        if (nonBorderRun > maxNonBorderRun)
                            break;
                    }
                }

                int hBottom = 0;
                if (lastBorderY >= 0)
                {
                    hBottom = Math.Max(0, rows - lastBorderY - shrink);
                }
                if (hBottom > 0)
                {
                    //var rect = new Rect(x0, rows - hBottom, bandW, hBottom);
                    //using var roi = new Mat(borderMask, rect);
                    //roi.SetTo(255);
                    // interate bottomDepth array for later smoothing
                    for (int x = x0; x < x1; x++)
                    {
                        if (x >= 0 && x < cols)
                            bottomDepth[x] = Math.Max(bottomDepth[x], hBottom);
                    }
                }
            }

            // 5.1) Сглаживаем профили глубины (integral k-interpolation)
            //int smoothK = Math.Max(1, brickThickness / 5); // можно вынести в параметр

            //SmoothDepthArray(leftDepth, rows, smoothK);
            //SmoothDepthArray(rightDepth, rows, smoothK);
            //SmoothDepthArray(topDepth, cols, smoothK);
            //SmoothDepthArray(bottomDepth, cols, smoothK);

            //int leftkNeighbors = 3; // <-- in BRICKS (UI parameter)
            //int rightkNeighbors = 3;
            //int topkNeighbors = 3;
            //int bottomkNeighbors = 3;
            //SmoothDepthByNeighborBricks(leftDepth, rows, brickThickness, leftkNeighbors);
            //SmoothDepthByNeighborBricks(rightDepth, rows, brickThickness, rightkNeighbors);
            //SmoothDepthByNeighborBricks(topDepth, cols, brickThickness, topkNeighbors);
            //SmoothDepthByNeighborBricks(bottomDepth, cols, brickThickness, bottomkNeighbors);


            int kNeighborsBricks = kInterpolation; // <-- expose to UI

            // 1) downsample per-pixel -> per-brick (max over brick span)
            var leftBricks = DownsampleMaxByBricks(leftDepth, rows, brickThickness);
            var rightBricks = DownsampleMaxByBricks(rightDepth, rows, brickThickness);
            var topBricks = DownsampleMaxByBricks(topDepth, cols, brickThickness);
            var bottomBricks = DownsampleMaxByBricks(bottomDepth, cols, brickThickness);

            //int gapBricks = 3; // <-- in BRICKS (UI parameter)

            //BridgeShortZeroGapsInPlace(leftBricks, gapBricks, maxDepthLeftBorder);

            //BridgeShortZeroGapsInPlace(rightBricks, gapBricks, maxDepthRightBorder);

            //BridgeShortZeroGapsInPlace(topBricks, gapBricks, maxDepthTopBorder);

            //BridgeShortZeroGapsInPlace(bottomBricks, gapBricks, maxDepthBottomBorder);

            // --- NEW knobs: this controls what "weak" means (=> will be bridged) ---
            int absWeakPx = Math.Max(1, brickThickness / 4); // X px
            double weakFrac = 0.40;                          // Y% of median (0..1)

            // if you want, you can also keep gap small as safety:
            int gapBricks = (kNeighborsBricks > 0) ? Math.Min(3, Math.Max(1, kNeighborsBricks / 2)) : 6;

            // compute per-side weak threshold = max(absWeakPx, round(medianNonZero * weakFrac))
            int weakLeft = ComputeWeakThresholdPx(leftBricks, absWeakPx, weakFrac);
            int weakRight = ComputeWeakThresholdPx(rightBricks, absWeakPx, weakFrac);
            int weakTop = ComputeWeakThresholdPx(topBricks, absWeakPx, weakFrac);
            int weakBottom = ComputeWeakThresholdPx(bottomBricks, absWeakPx, weakFrac);

            // convert "too short" into zeros => BridgeShortZeroGapsInPlace can fill them
            ZeroWeakBricksInPlace(leftBricks, weakLeft);
            ZeroWeakBricksInPlace(rightBricks, weakRight);
            ZeroWeakBricksInPlace(topBricks, weakTop);
            ZeroWeakBricksInPlace(bottomBricks, weakBottom);

            // bridge short gaps (now includes "missing" AND "too short")
            BridgeShortZeroGapsInPlace(leftBricks, gapBricks, maxDepthLeftBorder);
            BridgeShortZeroGapsInPlace(rightBricks, gapBricks, maxDepthRightBorder);
            BridgeShortZeroGapsInPlace(topBricks, gapBricks, maxDepthTopBorder);
            BridgeShortZeroGapsInPlace(bottomBricks, gapBricks, maxDepthBottomBorder);


            // 2) smooth in brick-space using k neighbors
            SmoothDepthBricksInPlace(leftBricks, kNeighborsBricks);
            SmoothDepthBricksInPlace(rightBricks, kNeighborsBricks);
            SmoothDepthBricksInPlace(topBricks, kNeighborsBricks);
            SmoothDepthBricksInPlace(bottomBricks, kNeighborsBricks);

            // 3) upsample back to per-pixel arrays (overwrite)
            UpsampleBricksToPixels(leftBricks, leftDepth, rows, brickThickness);
            UpsampleBricksToPixels(rightBricks, rightDepth, rows, brickThickness);
            UpsampleBricksToPixels(topBricks, topDepth, cols, brickThickness);
            UpsampleBricksToPixels(bottomBricks, bottomDepth, cols, brickThickness);


            // 5.2) Строим borderMask по сглаженным глубинам
            borderMask.SetTo(Scalar.All(0));

            // LEFT / RIGHT по строкам
            for (int y = 0; y < rows; y++)
            {
                int wL = leftDepth[y];
                if (wL > 0)
                {
                    int w = Math.Min(wL, cols);
                    var rectL = new Rect(0, y, w, 1);
                    using var roiL = new Mat(borderMask, rectL);
                    roiL.SetTo(255);
                }

                int wR = rightDepth[y];
                if (wR > 0)
                {
                    int w = Math.Min(wR, cols);
                    int xStart = Math.Max(0, cols - w);
                    var rectR = new Rect(xStart, y, w, 1);
                    using var roiR = new Mat(borderMask, rectR);
                    roiR.SetTo(255);
                }
            }

            // TOP / BOTTOM по колонкам
            for (int x = 0; x < cols; x++)
            {
                int hT = topDepth[x];
                if (hT > 0)
                {
                    int h = Math.Min(hT, rows);
                    var rectT = new Rect(x, 0, 1, h);
                    using var roiT = new Mat(borderMask, rectT);
                    roiT.SetTo(255);
                }

                int hB = bottomDepth[x];
                if (hB > 0)
                {
                    int h = Math.Min(hB, rows);
                    int yStart = Math.Max(0, rows - h);
                    var rectB = new Rect(x, yStart, 1, h);
                    using var roiB = new Mat(borderMask, rectB);
                    roiB.SetTo(255);
                }
            }




            // 6) Заливка
            //Scalar fill;
            //if (fillColor.HasValue)
            //{
            //    fill = fillColor.Value;
            //}
            //else
            //{
            //    using var innerBgr = new Mat(bgr, innerRect);
            //    fill = Cv2.Mean(innerBgr);
            //}

            //var dst = bgr.Clone();
            //dst.SetTo(fill, borderMask);

            //if (disposeBgr) bgr.Dispose();
            //return dst;
            // 6) Заливка / инпейнт

            // Цвет страницы (нам нужен и для Fill, и для “гибридного” inpaint)
            Scalar fill;
            if (fillColor.HasValue)
            {
                fill = fillColor.Value;
            }
            else
            {
                using var innerBgr = new Mat(bgr, innerRect);
                fill = Cv2.Mean(innerBgr);
            }

            Mat dst;
            // --- режим 1: старый Fill (просто залить бордюр цветом страницы) ---
            if (inpaintMode == BrickInpaintMode.Fill)
            {
                dst = bgr.Clone();
                // 1) Заливка бордюрной маски цветом страницы
                dst.SetTo(fill, borderMask);

                // 2) Небольшое размытие только в области бордюра,
                //    чтобы сделать переход к странице мягким

                double sigma = Math.Max(0.5, inpaintRadius); // можно подправить при желании
                sigma = Math.Min(10.0, sigma);

                if (sigma > 0.0)
                {
                    using var blurred = dst.Clone();

                    // Размываем ВСЁ изображение...
                    Cv2.GaussianBlur(blurred, blurred, new OpenCvSharp.Size(0, 0), sigma, sigma);

                    // ...но копируем из размытого только в зону borderMask
                    blurred.CopyTo(dst, borderMask);
                }
            }
            else
            {
                // --- режим 2: Inpaint (Telea / Navier-Stokes) по кирпичной маске ---

                // 1) Маска бордюра (outer) – всё, что мы считаем рамкой
                using var outerMask = borderMask.Clone();

                // 2) Внутренний пояс (innerMask) – слегка эрозим,
                // чтобы inpaint затронул только “переходную” зону у края страницы
                using var innerMask = borderMask.Clone();
                int innerRadius = Math.Max(1, (int)Math.Round(inpaintRadius / 2.0));
                if (innerRadius > 0)
                {
                    using var kInner = Cv2.GetStructuringElement(
                        MorphShapes.Rect,
                        new OpenCvSharp.Size(2 * innerRadius + 1, 2 * innerRadius + 1));
                    Cv2.Erode(innerMask, innerMask, kInner, iterations: 1);
                }

                // 3) Inpaint по всей зоне outerMask
                using var inpainted = new Mat();
                double radius = Math.Max(2.0, inpaintRadius);

                var method = (inpaintMode == BrickInpaintMode.Telea)
                    ? InpaintMethod.Telea
                    : InpaintMethod.NS;

                Cv2.Inpaint(bgr, outerMask, inpainted, radius, method);

                // 4) Собираем результат:
                //    - снаружи рамки → просто цвет страницы (fill),
                //    - по innerMask → втыкаем результат Telea/NS
                dst = bgr.Clone();

                // 4.1. Удаляем бордюр: заливаем его цветом страницы
                dst.SetTo(fill, outerMask);

                // 4.2. Мягко восстанавливаем/сглаживаем переход: вставляем inpaint
                inpainted.CopyTo(dst, innerMask);
            }

            if (disposeBgr) bgr.Dispose();
            return dst;


        }

        private static int ComputeWeakThresholdPx(int[] bricks, int absWeakPx, double weakFrac)
        {
            absWeakPx = Math.Max(0, absWeakPx);
            weakFrac = Math.Max(0.0, Math.Min(1.0, weakFrac));

            int med = MedianNonZero(bricks);
            int rel = (med > 0) ? (int)Math.Round(med * weakFrac) : 0;

            // THIS is the "absolute OR % of median" rule:
            // treat as weak if <= max(absWeakPx, rel)
            return Math.Max(absWeakPx, rel);
        }

        private static int MedianNonZero(int[] values)
        {
            if (values == null || values.Length == 0) return 0;
            var tmp = new List<int>(values.Length);
            for (int i = 0; i < values.Length; i++)
                if (values[i] > 0) tmp.Add(values[i]);

            if (tmp.Count == 0) return 0;
            tmp.Sort();
            return tmp[tmp.Count / 2];
        }


        // Converts "too short" bricks into zeros so BridgeShortZeroGapsInPlace can fill them.
        private static void ZeroWeakBricksInPlace(int[] bricks, int weakThrPx)
        {
            if (bricks == null || bricks.Length == 0) return;
            weakThrPx = Math.Max(0, weakThrPx);
            if (weakThrPx == 0) return;

            for (int i = 0; i < bricks.Length; i++)
            {
                int d = bricks[i];
                if (d > 0 && d <= weakThrPx) bricks[i] = 0;
            }
        }


        private static void BridgeShortZeroGapsInPlace(int[] depthBricks, int gapBricks, int maxDepthPx)
        {
            if (depthBricks == null || depthBricks.Length == 0) return;

            gapBricks = Math.Max(0, gapBricks);
            maxDepthPx = Math.Max(0, maxDepthPx);

            // clamp на всякий
            for (int i = 0; i < depthBricks.Length; i++)
                depthBricks[i] = Math.Min(depthBricks[i], maxDepthPx);

            if (gapBricks == 0) return;

            int n = depthBricks.Length;
            int i0 = 0;

            while (i0 < n)
            {
                if (depthBricks[i0] > 0) { i0++; continue; }

                int start = i0;
                while (i0 < n && depthBricks[i0] == 0) i0++;
                int end = i0 - 1;

                int len = end - start + 1;
                int left = (start - 1 >= 0) ? depthBricks[start - 1] : 0;
                int right = (i0 < n) ? depthBricks[i0] : 0;

                // Заполняем ТОЛЬКО "дыру внутри бордюра"
                if (len <= gapBricks && left > 0 && right > 0)
                {
                    int fill = (left + right) / 2;
                    fill = Math.Min(fill, maxDepthPx);

                    for (int k = start; k <= end; k++)
                        depthBricks[k] = fill;
                }
            }
        }


        /// <summary>
        /// Smooth depth profile using neighbor BRICKS count.
        /// depth[] is per-pixel-row (rows) or per-pixel-col (cols).
        /// brickThickness = brick size in pixels along this axis.
        /// kNeighborsBricks = how many neighbor bricks to include on each side.
        /// </summary>
        private static void SmoothDepthByNeighborBricks(int[] depth, int length, int brickThickness, int kNeighborsBricks)
        {
            if (depth == null || length <= 0) return;
            if (brickThickness <= 0) brickThickness = 8;
            if (kNeighborsBricks <= 0) return;

            int radius = kNeighborsBricks * brickThickness; // <-- key conversion

            var tmp = new double[length];

            for (int i = 0; i < length; i++)
            {
                int di = depth[i];
                if (di <= 0) { tmp[i] = 0; continue; }

                int start = Math.Max(0, i - radius);
                int end = Math.Min(length - 1, i + radius);

                double sum = 0;
                int count = 0;

                for (int j = start; j <= end; j++)
                {
                    int dj = depth[j];
                    if (dj > 0) { sum += dj; count++; }
                }

                tmp[i] = (count > 0) ? (sum / count) : di;
            }

            for (int i = 0; i < length; i++)
                depth[i] = (int)Math.Round(tmp[i]);
        }

        private static int[] DownsampleMaxByBricks(int[] perPixelDepth, int lengthPx, int brickThickness)
        {
            brickThickness = Math.Max(1, brickThickness);
            int nBricks = (lengthPx + brickThickness - 1) / brickThickness;

            var perBrick = new int[nBricks];
            for (int b = 0; b < nBricks; b++)
            {
                int start = b * brickThickness;
                int end = Math.Min(lengthPx, start + brickThickness);

                int m = 0;
                for (int i = start; i < end; i++)
                    m = Math.Max(m, perPixelDepth[i]);

                perBrick[b] = m;
            }
            return perBrick;
        }

        private static void SmoothDepthBricksInPlace(int[] depthBricks, int kNeighborsBricks)
        {
            if (depthBricks == null || depthBricks.Length == 0) return;
            if (kNeighborsBricks <= 0) return;

            int n = depthBricks.Length;
            var tmp = new double[n];

            for (int i = 0; i < n; i++)
            {
                int di = depthBricks[i];
                if (di <= 0) { tmp[i] = 0; continue; }

                int start = Math.Max(0, i - kNeighborsBricks);
                int end = Math.Min(n - 1, i + kNeighborsBricks);

                double sum = 0;
                int count = 0;
                for (int j = start; j <= end; j++)
                {
                    int dj = depthBricks[j];
                    if (dj > 0) { sum += dj; count++; }
                }
                tmp[i] = (count > 0) ? (sum / count) : di;
            }

            for (int i = 0; i < n; i++)
                depthBricks[i] = (int)Math.Round(tmp[i]);
        }

        private static void UpsampleBricksToPixels(int[] perBrickDepth, int[] perPixelDepth, int lengthPx, int brickThickness)
        {
            brickThickness = Math.Max(1, brickThickness);
            int nBricks = perBrickDepth.Length;

            for (int b = 0; b < nBricks; b++)
            {
                int start = b * brickThickness;
                int end = Math.Min(lengthPx, start + brickThickness);

                int v = perBrickDepth[b];
                for (int i = start; i < end; i++)
                    perPixelDepth[i] = v;
            }
        }


    }
}
