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
    }
}
