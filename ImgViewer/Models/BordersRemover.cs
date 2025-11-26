using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Runtime.Remoting.Messaging;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
    public class BordersRemover
    {

        public static Mat RemoveBorderArtifactsGeneric_Safe(
            CancellationToken token,
            Mat src,
            byte thr,                       // порог для определения "тёмного" (например EstimateBlackThreshold(rotated)) 
            Scalar? bgColor = null,         // цвет фона (null -> автоопределение по углам)
            int minAreaPx = 2000,           // если площадь >= этого -> считается значимой
            double minSpanFraction = 0.6,   // если bbox покрывает >= этой доли по ширине/высоте -> кандидат
            double solidityThreshold = 0.6, // если solidity >= -> кандидат
            double minDepthFraction = 0.05, // проникновение вглубь, в долях min(rows,cols)
            int featherPx = 12              // радиус растушёвки для мягкого перехода
        )


        {


            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return src.Clone();

            using var srcClone = src.Clone();
            Mat working = srcClone;
            bool disposeWorking = false;
            token.ThrowIfCancellationRequested();
            if (srcClone.Type() != MatType.CV_8UC3)
            {
                working = new Mat();
                srcClone.ConvertTo(working, MatType.CV_8UC3);
                disposeWorking = true;
            }

            try
            {
                int rows = working.Rows, cols = working.Cols;
                int minDepthPx = (int)Math.Round(minDepthFraction * Math.Min(rows, cols));

                // determine bg color from corners if not provided
                Scalar chosenBg = bgColor ?? new Scalar(255, 255, 255);
                if (!bgColor.HasValue)
                {


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





                //+++
                //int kernelWidth = Math.Max(7, working.Cols / 60);
                //using var longKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(kernelWidth, 1));
                //Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Close, longKernel);
                //using var smallK = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(1, 1));
                //Cv2.Dilate(darkMask, darkMask, smallK, iterations: 1);
                //+++

                // small open to reduce noise
                token.ThrowIfCancellationRequested();
                using (var kOpen = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3)))
                {
                    token.ThrowIfCancellationRequested();
                    Cv2.MorphologyEx(darkMask, darkMask, MorphTypes.Open, kOpen);
                }

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

                // iterate components
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
                    if (!(touchesLeft || touchesTop || touchesRight || touchesBottom)) continue;

                    double solidity = (w > 0 && h > 0) ? (double)area / (w * h) : 0.0;
                    double spanFractionW = (double)w / cols;
                    double spanFractionH = (double)h / rows;

                    // compute maxDepth by scanning the component mask area (safe, no ToArray)
                    int maxDepth = 0;
                    using (var compMask = new Mat())
                    {
                        Cv2.InRange(labels, new Scalar(i), new Scalar(i), compMask); // compMask: 255 where label==i

                        // scan bounding box only for speed
                        int x0 = Math.Max(0, x);
                        int y0 = Math.Max(0, y);
                        int x1 = Math.Min(cols - 1, x + w - 1);
                        int y1 = Math.Min(rows - 1, y + h - 1);

                        for (int yy = y0; yy <= y1; yy++)
                        {
                            token.ThrowIfCancellationRequested();
                            for (int xx = x0; xx <= x1; xx++)
                            {
                                byte v = compMask.At<byte>(yy, xx);
                                if (v == 0) continue;
                                int d = Math.Min(Math.Min(xx, cols - 1 - xx), Math.Min(yy, rows - 1 - yy));
                                if (d > maxDepth) maxDepth = d;
                            }
                        }

                        bool select = false;
                        if (area >= minAreaPx) select = true;
                        if (solidity >= solidityThreshold) select = true;
                        if (spanFractionW >= minSpanFraction && (touchesTop || touchesBottom)) select = true;
                        if (spanFractionH >= minSpanFraction && (touchesLeft || touchesRight)) select = true;
                        if (maxDepth >= minDepthPx) select = true;
                        if ((touchesLeft && touchesRight) || (touchesTop && touchesBottom)) select = true;

                        if (select)
                        {
                            // add compMask -> selectedMask (BitwiseOr). Use Cv2.BitwiseOr with real Mats.
                            Cv2.BitwiseOr(selectedMask, compMask, selectedMask);
                        }
                    }
                }

                //System.Diagnostics.Debug.WriteLine($"selectedMask nonzero: {Cv2.CountNonZero(selectedMask)}, type={selectedMask.Type()}");

                // if nothing selected -> return clone
                if (Cv2.CountNonZero(selectedMask) == 0)
                    return working.Clone();

                token.ThrowIfCancellationRequested();

                // 3) fill selected areas with background color (hard fill)
                //var filled = working.Clone();

                //// cut ++++++

                //int margin = featherPx; // adjust if you want expansion/shrink of mask

                //// Ensure mask is 0/255 CV_8U and clone
                //Mat modMask;
                //if (selectedMask.Type() != MatType.CV_8U)
                //{
                //    modMask = new Mat();
                //    selectedMask.ConvertTo(modMask, MatType.CV_8U);
                //}
                //else
                //{
                //    modMask = selectedMask.Clone();
                //}

                //// apply symmetric margin if requested
                //if (margin != 0)
                //{
                //    int m = Math.Abs(margin);
                //    var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * m + 1, 2 * m + 1));
                //    if (margin > 0)
                //        Cv2.Dilate(modMask, modMask, k, iterations: 1);
                //    else
                //        Cv2.Erode(modMask, modMask, k, iterations: 1);
                //}

                //// Safely convert chosenBg components to 0..255 integers WITHOUT Math.Clamp
                //int bVal = (int)Math.Round(chosenBg.Val0);
                //if (bVal < 0) bVal = 0; else if (bVal > 255) bVal = 255;
                //int gVal = (int)Math.Round(chosenBg.Val1);
                //if (gVal < 0) gVal = 0; else if (gVal > 255) gVal = 255;
                //int rVal = (int)Math.Round(chosenBg.Val2);
                //if (rVal < 0) rVal = 0; else if (rVal > 255) rVal = 255;

                //// Build result initialized with chosen background color (B,G,R)
                //Mat result = new Mat(working.Size(), MatType.CV_8UC3, new Scalar(bVal, gVal, rVal));

                //// Copy original pixels where mask == 0 (outside selection)
                //Mat invMask = new Mat();
                //Cv2.BitwiseNot(modMask, invMask);
                //working.CopyTo(result, invMask);

                //// return result immediately (no further blending)
                //return result;
                // 3) fill selected areas with background color (hard fill + crop)
                int margin = featherPx; // adjust if you want expansion/shrink of mask

                // Ensure mask is 0/255 CV_8U and clone
                Mat modMask;
                if (selectedMask.Type() != MatType.CV_8U)
                {
                    modMask = new Mat();
                    selectedMask.ConvertTo(modMask, MatType.CV_8U);
                }
                else
                {
                    modMask = selectedMask.Clone();
                }

                // apply symmetric margin if requested
                if (margin != 0)
                {
                    int m = Math.Abs(margin);
                    using var k = Cv2.GetStructuringElement(
                        MorphShapes.Rect,
                        new OpenCvSharp.Size(2 * m + 1, 2 * m + 1));

                    if (margin > 0)
                        Cv2.Dilate(modMask, modMask, k, iterations: 1);
                    else
                        Cv2.Erode(modMask, modMask, k, iterations: 1);
                }

                // Safely convert chosenBg components to 0..255 integers
                int bVal = (int)Math.Round(chosenBg.Val0);
                if (bVal < 0) bVal = 0; else if (bVal > 255) bVal = 255;
                int gVal = (int)Math.Round(chosenBg.Val1);
                if (gVal < 0) gVal = 0; else if (gVal > 255) gVal = 255;
                int rVal = (int)Math.Round(chosenBg.Val2);
                if (rVal < 0) rVal = 0; else if (rVal > 255) rVal = 255;

                // Build result initialized with chosen background color (B,G,R)
                Mat result = new Mat(working.Size(), MatType.CV_8UC3, new Scalar(bVal, gVal, rVal));

                using (var invMask = new Mat())
                {
                    // invMask = содержимое (0 на бордюрах, 255 внутри страницы)
                    Cv2.BitwiseNot(modMask, invMask);

                    // копируем оригинал ТОЛЬКО там, где нет бордюров
                    working.CopyTo(result, invMask);

                    // ---- CROP по invMask ----
                    int rows2 = result.Rows;
                    int cols2 = result.Cols;

                    int cropTop = 0, cropBottom = rows2 - 1;
                    int cropLeft = 0, cropRight = cols2 - 1;

                    // сверху
                    while (cropTop <= cropBottom && Cv2.CountNonZero(invMask.Row(cropTop)) == 0)
                        cropTop++;

                    // снизу
                    while (cropBottom >= cropTop && Cv2.CountNonZero(invMask.Row(cropBottom)) == 0)
                        cropBottom--;

                    // слева
                    while (cropLeft <= cropRight && Cv2.CountNonZero(invMask.Col(cropLeft)) == 0)
                        cropLeft++;

                    // справа
                    while (cropRight >= cropLeft && Cv2.CountNonZero(invMask.Col(cropRight)) == 0)
                        cropRight--;

                    // если вдруг не нашли адекватную область — вернём просто fill-результат
                    if (cropTop >= cropBottom || cropLeft >= cropRight)
                        return result;

                    var roi = new Rect(
                        cropLeft,
                        cropTop,
                        cropRight - cropLeft + 1,
                        cropBottom - cropTop + 1);

                    Mat cropped = new Mat(result, roi).Clone();
                    result.Dispose();
                    return cropped;
                }





                // cut ++++++


                //int margin = 100; // <-- ваш n: положительное = расширить, отрицательное = врезать внутрь

                //// ensure mask is CV_8U with values 0/255
                //Mat modMask = new Mat();
                //if (selectedMask.Type() != MatType.CV_8U)
                //    selectedMask.ConvertTo(modMask, MatType.CV_8U);
                //else
                //    modMask = selectedMask.Clone();

                //if (margin > 0)
                //{
                //    // use a square kernel of size (2*margin+1)
                //    var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * margin + 1, 2 * margin + 1));
                //    Cv2.Dilate(modMask, modMask, k, iterations: 1);
                //}
                //else if (margin < 0)
                //{
                //    int m = -margin;
                //    var k = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * m + 1, 2 * m + 1));
                //    Cv2.Erode(modMask, modMask, k, iterations: 1);
                //}

                //filled.SetTo(chosenBg, selectedMask);


                //filled.SetTo(new Scalar(0, 255, 0), selectedMask); // bright green

                //Cv2.ImWrite("dbg_working.png", working);
                //Cv2.ImWrite("dbg_selectedMask.png", selectedMask);
                //Cv2.ImWrite("dbg_filled_after_setto.png", filled);

                // 4) smooth the seam: create blurred (soft) mask and do local per-pixel blend in ROI
                // create blurred mask (CV_8U -> blurred uchar)
                //using var blurred = new Mat();
                //int ksize = Math.Max(3, (featherPx / 2) * 2 + 1);
                //Cv2.GaussianBlur(selectedMask, blurred, new OpenCvSharp.Size(ksize, ksize), 0);

                //// compute bounding box of blurred mask (scan for nonzero)
                //int top = -1, bottom = -1, left = -1, right = -1;
                //for (int r = 0; r < rows; r++)
                //{
                //    for (int c = 0; c < cols; c++)
                //    {
                //        if (blurred.At<byte>(r, c) != 0)
                //        {
                //            if (top == -1 || r < top) top = r;
                //            if (bottom == -1 || r > bottom) bottom = r;
                //            if (left == -1 || c < left) left = c;
                //            if (right == -1 || c > right) right = c;
                //        }
                //    }
                //}

                //// if no nonzero found (should not), return filled
                //if (top == -1) return filled.Clone();

                //// expand ROI a bit
                //top = Math.Max(0, top - featherPx);
                //bottom = Math.Min(rows - 1, bottom + featherPx);
                //left = Math.Max(0, left - featherPx);
                //right = Math.Min(cols - 1, right + featherPx);

                //// per-pixel blend inside ROI using blurred mask as alpha (0..255)
                //for (int r = top; r <= bottom; r++)
                //{
                //    for (int c = left; c <= right; c++)
                //    {
                //        byte a = blurred.At<byte>(r, c); // 0..255
                //        if (a == 0) continue; // no change
                //        if (a == 255)
                //        {
                //            // fully filled already
                //            // ensure pixel in result is bg (it is because filled.SetTo done)
                //            continue;
                //        }
                //        // alpha normalized
                //        double alpha = a / 255.0;
                //        var origB = working.At<Vec3b>(r, c);
                //        var fillB = filled.At<Vec3b>(r, c);
                //        byte nb = (byte)Math.Round(fillB.Item0 * alpha + origB.Item0 * (1 - alpha));
                //        byte ng = (byte)Math.Round(fillB.Item1 * alpha + origB.Item1 * (1 - alpha));
                //        byte nr = (byte)Math.Round(fillB.Item2 * alpha + origB.Item2 * (1 - alpha));
                //        // write to filled result
                //        filled.Set<Vec3b>(r, c, new Vec3b(nb, ng, nr));
                //        //filled.Set<Vec3b>(r, c, new Vec3b(0, 0, 255));
                //    }
                //}


                //return filled;
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
            if (src.Type() != MatType.CV_8UC3)
            {
                srcBgr = new Mat();
                src.ConvertTo(srcBgr, MatType.CV_8UC3);
                converted = true;
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
            //Scalar white = new Scalar(255, 255, 255);
            //if (top > 0) result[new Rect(0, 0, w, top)].SetTo(white);
            //if (bottom > 0) result[new Rect(0, h - bottom, w, bottom)].SetTo(white);
            //if (left > 0) result[new Rect(0, 0, left, h)].SetTo(white);
            //if (right > 0) result[new Rect(w - right, 0, right, h)].SetTo(white);

            // trying to crop instead of fill
            int row0 = top;
            int row1 = h - bottom;
            int col0 = left;
            int col1 = w - right;
            if (row1 <= row0 || col1 <= col0) return new Mat();
            result = srcBgr.RowRange(row0, row1).ColRange(col0, col1).Clone();


            // Заменяем поле _currentImage на result (освобождая прежний Mat)
            //WorkingImage = result;

            // Освобождаем временные объекты
            central.Dispose();
            gray.Dispose();
            if (converted) srcBgr.Dispose(); // если создали новый Mat при конвертации
            //old?.Dispose();

            // (опционально) логирование — можно убрать
            Debug.WriteLine($"RemoveBordersByRowColWhite applied: cuts(top,bottom,left,right) = ({top},{bottom},{left},{right}), centralMedian={centralMedian}");
            return result;
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


        public static Mat? ManualCut(CancellationToken token, Mat src, int x,  int y, int w, int h)
        {
            token.ThrowIfCancellationRequested();
            if (src == null || src.Empty())
                return null;
            int rows = src.Rows;
            int cols = src.Cols;

            // clamp
            x = Math.Max(0, Math.Min(cols - 1, x));
            y = Math.Max(0, Math.Min(rows - 1, y));
            w = Math.Max(1, Math.Min(cols - x, w));
            h = Math.Max(1, Math.Min(rows - y, h));

            var roi = new Rect(x, y, w, h);
            Mat result = new Mat(src, roi).Clone();
            return result;
        }

    }
}
