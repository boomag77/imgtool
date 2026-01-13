using ImgViewer.Models;
using OpenCvSharp;
using System.Diagnostics;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

public static class PunchHoleRemover
{
    /// <summary>
    /// Remove punch holes located near page edges.
    /// </summary>
    /// <param name="src">Input BGR or Gray Mat (unchanged if null)</param>
    /// <param name="specs">List of punch specs (circle or rect)</param>
    /// <param name="offsetTop">pixels from top</param>
    /// <param name="offsetBottom">pixels from bottom</param>
    /// <param name="offsetLeft">pixels from left</param>
    /// <param name="offsetRight">pixels from right</param>
    /// <returns>Restored Mat (BGR)</returns>
    /// 

    public static Mat RemovePunchHoles(CancellationToken token, Mat src,
                                      List<PunchSpec> specs, double roundness, double fr,
                                      int offsetTop, int offsetBottom,
                                      int offsetLeft, int offsetRight)
    {
        if (src == null || src.Empty())
            return src;

        token.ThrowIfCancellationRequested();
        // ensure color image for inpainting output
        Mat srcColor = src.Channels() == 3 ? src.Clone() : new Mat();
        if (srcColor.Empty())
            Cv2.CvtColor(src, srcColor, ColorConversionCodes.GRAY2BGR);

        // 1) Preprocess: grayscale + mild blur to reduce noise
        Mat gray = new Mat();
        if (src.Channels() == 3) Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
        else gray = src.Clone();

        token.ThrowIfCancellationRequested();
        Cv2.GaussianBlur(gray, gray, new Size(9, 9), 2, 2);

        // 2) Build search mask (areas near edges where holes expected)
        Mat searchMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);

        int w = gray.Width, h = gray.Height;
        // top strip
        if (offsetTop > 0)
        {
            var r = new Rect(offsetLeft, 0, w - offsetLeft - offsetRight, Math.Min(offsetTop, h));
            if (r.Width > 0 && r.Height > 0) searchMask[r].SetTo(255);
            token.ThrowIfCancellationRequested();
        }
        // bottom strip
        if (offsetBottom > 0)
        {
            var r = new Rect(offsetLeft, Math.Max(0, h - offsetBottom), w - offsetLeft - offsetRight, Math.Min(offsetBottom, h));
            if (r.Width > 0 && r.Height > 0) searchMask[r].SetTo(255);
            token.ThrowIfCancellationRequested();
        }
        // left strip
        if (offsetLeft > 0)
        {
            var r = new Rect(0, offsetTop, Math.Min(offsetLeft, w), h - offsetTop - offsetBottom);
            if (r.Width > 0 && r.Height > 0) searchMask[r].SetTo(255);
            token.ThrowIfCancellationRequested();
        }
        // right strip
        if (offsetRight > 0)
        {
            var r = new Rect(Math.Max(0, w - offsetRight), offsetTop, Math.Min(offsetRight, w), h - offsetTop - offsetBottom);
            if (r.Width > 0 && r.Height > 0) searchMask[r].SetTo(255);
            token.ThrowIfCancellationRequested();
        }

        // optional: dilate search mask slightly to include border artifacts
        Cv2.Dilate(searchMask, searchMask, Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3)));
        token.ThrowIfCancellationRequested();

        // 3) Candidate detection & mask construction
        Mat holesMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);

        foreach (var spec in specs)
        {
            token.ThrowIfCancellationRequested();
            if (spec.Shape == PunchShape.Circle)
            {
                // Use HoughCircles on masked ROI
                // Prepare ROI image where searchMask != 0
                Mat maskedGray = new Mat();
                gray.CopyTo(maskedGray, searchMask);


                /// ??????
                if (maskedGray.Type() != MatType.CV_8UC1)
                {
                    var tmp = new Mat();
                    maskedGray.ConvertTo(tmp, MatType.CV_8UC1);
                    maskedGray.Dispose();
                    maskedGray = tmp;
                }

                // ???????

                // если по маске вообще нет данных — смысла вызывать Hough нет
                if (Cv2.CountNonZero(maskedGray) == 0)
                {
                    maskedGray.Dispose();
                    continue; // к следующему spec
                }

                // HoughCircles expects 8-bit single-channel blurred image
                // determine min/max radius from spec.Diameter with tolerance

                int radius = (int)(spec.Diameter / 2.0);
                // --- old version - tolerance was to both sides
                //int minR = Math.Max(1, (int)(radius * (1.0 - spec.SizeToleranceFraction)));
                //int maxR = Math.Max(minR, (int)(radius * (1.0 + spec.SizeToleranceFraction)));
                // ---

                // allow only larger radii, not smaller:
                // minR = radius (do not accept circles smaller than expected)
                // maxR = radius * (1 + tol)
                int minR = Math.Max(1, radius);
                int maxR = Math.Max(minR, (int)Math.Ceiling(radius * (1.0 + spec.SizeToleranceFraction)));

                // parameters tuned as initial guesses — may need adjustment
                double dp = 1;
                double minDist = Math.Max(10, radius * 2.0);
                //double param1 = 100; // Canny higher threshold
                //double param2 = 30;  // accumulator threshold

                double param1 = 300;
                double param2 = roundness;

                CircleSegment[] circles;
                try
                {
                    circles = Cv2.HoughCircles(maskedGray, HoughModes.GradientAlt, dp, minDist, param1, param2, minR, maxR);
                }
                catch (OpenCVException ex)
                {
                    Debug.WriteLine($"Circle Hough exeption: {ex}");
                    circles = new CircleSegment[0];
                }

                // validate circles by intensity (density)
                foreach (var c in circles)
                {
                    var center = new Point((int)c.Center.X, (int)c.Center.Y);
                    int rfound = (int)Math.Round(c.Radius);
                    // skip if center outside searchMask (safety)
                    if (searchMask.At<byte>(center.Y, center.X) == 0) continue;

                    // compute mean intensity inside circle
                    Mat circleMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
                    Cv2.Circle(circleMask, center, rfound, Scalar.White, -1);
                    Scalar meanVal = Cv2.Mean(gray, circleMask);
                    double meanIntensity = meanVal.Val0; // 0..255

                    // estimate background intensity by sampling nearby annulus
                    int annR1 = Math.Min(w + h, rfound + 6);
                    int annR0 = rfound + 3;
                    Mat annMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
                    Cv2.Circle(annMask, center, annR1, Scalar.White, -1);
                    Cv2.Circle(annMask, center, annR0, Scalar.Black, -1);
                    Scalar bgMean = Cv2.Mean(gray, annMask);

                    // hole is usually darker than background (or lighter) - compare
                    // expected: abs(meanIntensity - bgMean) should be significant
                    double contrast = bgMean.Val0 - meanIntensity; // positive if hole darker
                    // interpret spec.Density: 1.0 -> prefer darker holes, 0.0 -> prefer light holes
                    bool densityOk = (spec.Density >= 0.5) ? (contrast > 8) : (contrast < -8 || Math.Abs(contrast) > 8);

                    if (densityOk)
                    {
                        // mark this circle into holesMask
                        Cv2.Circle(holesMask, center, (int)(rfound * 1.1), Scalar.White, -1);
                    }

                    circleMask.Dispose();
                    annMask.Dispose();
                }

                maskedGray.Dispose();
            }
            else if (spec.Shape == PunchShape.Rect)
            {
                // Rect detection: threshold + findContours in searchMask region
                Mat masked = new Mat();
                gray.CopyTo(masked, searchMask);

                Mat thr = new Mat();
                // adaptive or Otsu depending on image; try adaptive for nonuniform illumination
                token.ThrowIfCancellationRequested();
                Cv2.AdaptiveThreshold(masked, thr, 255, AdaptiveThresholdTypes.GaussianC, ThresholdTypes.BinaryInv, 11, 2);
                token.ThrowIfCancellationRequested();

                // morphological closing to fill holes inside the punch ring
                //Mat kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
                //Cv2.MorphologyEx(thr, thr, MorphTypes.Close, kernel, iterations: 1);

                // find contours
                Point[][] contours;
                HierarchyIndex[] hier;
                token.ThrowIfCancellationRequested();
                Cv2.FindContours(thr, out contours, out hier, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
                token.ThrowIfCancellationRequested();

                foreach (var cnt in contours)
                {
                    var rect = Cv2.BoundingRect(cnt);
                    double area = Cv2.ContourArea(cnt);
                    if (area < 10) continue;

                    //double expectedArea = spec.RectSize.Width * spec.RectSize.Height;
                    // --- old version - tolerance works to both sides
                    //double tol = expectedArea * spec.SizeToleranceFraction;
                    //if (Math.Abs(area - expectedArea) > tol) continue;
                    // ---

                    // accept only areas not smaller than expected, and not larger than expected*(1+tol)
                    //double tolUp = expectedArea * spec.SizeToleranceFraction;
                    //if (area < expectedArea) continue;
                    //if (area > expectedArea + tolUp) continue;

                    //double aspect = rect.Width / (double)rect.Height;
                    //double expAspect = spec.RectSize.Width / (double)spec.RectSize.Height;
                    //if (Math.Abs(aspect - expAspect) > 0.6) continue;

                    // new tolerance to width and height

                    double expW = spec.RectSize.Width;
                    double expH = spec.RectSize.Height;
                    double tolFrac = spec.SizeToleranceFraction; // e.g. 0.2 for +20%

                    // accept only widths/heights not smaller than expected, and not larger than expected*(1+tol)
                    double maxW = expW * (1.0 + tolFrac);
                    double maxH = expH * (1.0 + tolFrac);

                    if (rect.Width < expW) continue;
                    if (rect.Height < expH) continue;
                    if (rect.Width > maxW) continue;
                    if (rect.Height > maxH) continue;

                    double rectArea = rect.Width * rect.Height;
                    double fillRatio = area / rectArea; // 0..1
                    if (fillRatio < fr) continue;

                    // center check inside searchMask region
                    var center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
                    if (searchMask.At<byte>(center.Y, center.X) == 0) continue;

                    // mean intensity check (similar to circle)
                    Mat rectMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
                    Cv2.Rectangle(rectMask, rect, Scalar.White, -1);
                    Scalar meanVal = Cv2.Mean(gray, rectMask);
                    // sample small border around rect
                    var big = rect;
                    big.X = Math.Max(0, big.X - 4);
                    big.Y = Math.Max(0, big.Y - 4);
                    big.Width = Math.Min(w - big.X, big.Width + 8);
                    big.Height = Math.Min(h - big.Y, big.Height + 8);
                    Mat borderMask = Mat.Zeros(gray.Size(), MatType.CV_8UC1);
                    Cv2.Rectangle(borderMask, big, Scalar.White, -1);
                    Cv2.Rectangle(borderMask, rect, Scalar.Black, -1);
                    Scalar bgMean = Cv2.Mean(gray, borderMask);

                    double contrast = bgMean.Val0 - meanVal.Val0;
                    bool densityOk = (spec.Density >= 0.5) ? (contrast > 8) : (contrast < -8 || Math.Abs(contrast) > 8);
                    if (densityOk)
                    {
                        Cv2.Rectangle(holesMask, rect, Scalar.White, -1);
                    }

                    rectMask.Dispose();
                    borderMask.Dispose();
                }

                thr.Dispose();
                masked.Dispose();
                //kernel.Dispose();
            }
        }

        // 4) Postprocess mask: dilate and feather (blur) for smooth inpainting mask
        if (Cv2.CountNonZero(holesMask) == 0)
        {
            // nothing found: return original
            gray.Dispose();
            searchMask.Dispose();
            holesMask.Dispose();
            srcColor.Dispose();
            return src.Clone();
        }

        //Mat kernelDil = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(7, 7));
        //Cv2.Dilate(holesMask, holesMask, kernelDil, iterations: 2);



        // optionally blur mask to feather edges (inpaint expects 8U mask; we threshold after blur)
        Mat holesMaskF = new Mat();

        // TO-DO move out size to the UI
        Cv2.GaussianBlur(holesMask, holesMaskF, new Size(15, 15), 0);
        Cv2.Threshold(holesMaskF, holesMaskF, 10, 255, ThresholdTypes.Binary);

        /// --- OLD INPAINT START
        // 5) Inpaint (Telea) — radius depends on average hole size
        int avgRadius = Math.Max(3, (int)((specs.Where(s => s.Shape == PunchShape.Circle).Select(s => s.Diameter).DefaultIfEmpty(10).Average() / 2.0)));
        int inpaintRadius = Math.Max(3, avgRadius);

        Mat dst = new Mat();
        //Cv2.Inpaint(srcColor, holesMaskF, dst, inpaintRadius, InpaintMethod.Telea);
        Cv2.Inpaint(srcColor, holesMaskF, dst, inpaintRadius, InpaintMethod.NS);
        //----- OLD INPAINT END



        // cleanup
        gray.Dispose();
        searchMask.Dispose();
        holesMask.Dispose();
        holesMaskF.Dispose();
        //kernelDil.Dispose();
        srcColor.Dispose();

        return dst;
    }
}
