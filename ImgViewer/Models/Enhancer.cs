using System.Threading;
using OpenCvSharp;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models
{
    public static class Enhancer
    {

        public enum RetinexOutputMode
        {
            LogHighpass,   // recommended for pre-binarization
            ReconstructExp // optional / legacy
        }

        public static Mat ApplyClahe(CancellationToken token, Mat src, double clipLimit = 4.0, int gridSize = 8)
        {
            if (src == null)
                throw new ArgumentNullException(nameof(src));

            if (src.Type() == MatType.CV_8UC1)
            {
                using (var midMask = new Mat())
                {
                    Cv2.InRange(src, new Scalar(1), new Scalar(254), midMask);
                    bool hasMidtones = Cv2.CountNonZero(midMask) > 0;

                    if (!hasMidtones)
                    {
                        return src.Clone(); // ?????????? ??????? ?????, ????? ?? ??????? src
                    }
                }

                using var clahe = Cv2.CreateCLAHE(clipLimit, new OpenCvSharp.Size(gridSize, gridSize));
                var enhancedGray = new Mat();
                clahe.Apply(src, enhancedGray);
                return enhancedGray;
            }
            token.ThrowIfCancellationRequested();
            Mat bgr = null;
            bool needDisposeBgr = false;

            try
            {
                if (src.Type() == MatType.CV_8UC3)
                {
                    // ????? ???????? ????? ? ???, ?? ??????? ????? Mat
                    bgr = src;
                }
                else if (src.Type() == MatType.CV_8UC4)
                {
                    // ???? ????? ? ??????? ??, ???????? ? BGR
                    bgr = new Mat();
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
                    needDisposeBgr = true;
                }
                else if (src.Type() == MatType.CV_8UC1)
                {
                    // ????????????? (?? ?? ????????) ? ????????? ? BGR
                    bgr = new Mat();
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
                    needDisposeBgr = true;
                }
                else
                {
                    throw new ArgumentException("ApplyClahe expects 1, 3, or 4 channel Mats", nameof(src));
                }

                // BGR ? Lab
                using var lab = new Mat();
                Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

                // ????????? ?? ??????: L, a, b
                var labChannels = lab.Split();
                var l = labChannels[0]; // ???????

                var lEnhanced = new Mat();

                // CLAHE ?????? ?? L-??????
                using (var clahe = Cv2.CreateCLAHE(clipLimit, new OpenCvSharp.Size(gridSize, gridSize)))
                {
                    
                    clahe.Apply(l, lEnhanced);

                    // ?????? L ????? ??????????
                    l.Dispose();
                    labChannels[0] = lEnhanced;
                }

                // ????????? L', a, b ???????
                using var labMerged = new Mat();
                Cv2.Merge(labChannels, labMerged);
                lEnhanced.Dispose();

                // ?????? ?????? ?? ?????
                foreach (var ch in labChannels)
                {
                    if (!ch.IsDisposed)
                        ch.Dispose();
                }

                // Lab ? BGR
                var dst = new Mat();
                Cv2.CvtColor(labMerged, dst, ColorConversionCodes.Lab2BGR);

                return dst;
            }
            finally
            {
                if (needDisposeBgr && bgr != null && !bgr.IsDisposed)
                    bgr.Dispose();
            }
        }

        public static Mat HomomorphicRetinex(
                CancellationToken token,
                Mat src,
                RetinexOutputMode outputMode = RetinexOutputMode.LogHighpass,
                bool useLabL = true,            // true = extract L from Lab (best for paper), false = Gray
                double sigma = 50.0,            // blur sigma in log-domain; scale-dependent
                double gammaHigh = 1.8,         // boost details (ink strokes)
                double gammaLow = 0.6,          // keep some low-freq to avoid over-flattening
                double eps = 1e-6,
                bool robustNormalize = true,
                double pLow = 0.5,         // percent
                double pHigh = 99.5,       // percent
                int histBins = 2048,
                double expClampAbs = 4.0)              // avoid log(0))
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Mat();

            // 1) Get grayscale base: Lab.L or Gray
            using var gray8u = ExtractGray8U(src, useLabL);

            // 2) float [0..1]
            using var f = new Mat();
            gray8u.ConvertTo(f, MatType.CV_32F, 1.0 / 255.0);

            // 3) log(I + eps)
            Cv2.Max(f, eps, f); // clamp to eps
            using var logI = new Mat();
            Cv2.Log(f, logI);

            // 4) low = blur(logI)
            using var low = new Mat();
            Cv2.GaussianBlur(
                logI, low,
                ksize: new Size(0, 0),
                sigmaX: sigma, sigmaY: sigma,
                borderType: BorderTypes.Reflect101);

            // 5) high = logI - low
            using var high = new Mat();
            Cv2.Subtract(logI, low, high);

            token.ThrowIfCancellationRequested();

            if (outputMode == RetinexOutputMode.LogHighpass)
            {
                // out = gammaHigh * (logI - low)
                using var outF = new Mat();
                Cv2.Multiply(high, gammaHigh, outF);

                return RobustNormalizeTo8U(token, outF, robustNormalize, pLow, pHigh, histBins);
            }
            else
            {
                // outLog = gammaHigh*high + gammaLow*low
                using var outLog = new Mat();
                Cv2.AddWeighted(high, gammaHigh, low, gammaLow, 0.0, outLog);

                // clamp to prevent Exp from collapsing to near-zero
                Cv2.Max(outLog, -expClampAbs, outLog);
                Cv2.Min(outLog, expClampAbs, outLog);

                using var expI = new Mat();
                Cv2.Exp(outLog, expI);

                return RobustNormalizeTo8U(token, expI, robustNormalize, pLow, pHigh, histBins);
            }
        }

        private static Mat ExtractGray8U(Mat src, bool useLabL)
        {
            // Returns single-channel 8U Mat. Caller owns returned Mat.
            if (src.Channels() == 1)
            {
                if (src.Type() == MatType.CV_8U) return src.Clone();

                var g = new Mat();
                src.ConvertTo(g, MatType.CV_8U); // assume already normalized reasonably
                return g;
            }

            // If BGRA -> BGR, dispose only the temporary Mat we create.
            Mat? bgr = null;
            bool disposeBgr = false;
            if (src.Channels() == 4)
            {
                bgr = src.CvtColor(ColorConversionCodes.BGRA2BGR);
                disposeBgr = true;
            }
            else
            {
                bgr = src;
            }

            if (!useLabL)
            {
                var gray = new Mat();
                Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                if (disposeBgr)
                    bgr.Dispose();
                return gray;
            }

            // Lab L-channel tends to be more stable for paper shading
            using var lab = new Mat();
            Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

            var ch = lab.Split();
            try
            {
                // L is ch[0]
                return ch[0].Clone();
            }
            finally
            {
                foreach (var m in ch) m.Dispose();
                if (disposeBgr)
                    bgr.Dispose();
            }
        }

        private static Mat RobustNormalizeTo8U(
                                    CancellationToken token,
                                    Mat src32f,
                                    bool robust,
                                    double pLow, double pHigh,
                                    int histBins)
        {
            token.ThrowIfCancellationRequested();

            var dst8u = new Mat();

            if (!robust)
            {
                Cv2.Normalize(src32f, dst8u, 0, 255, NormTypes.MinMax);
                dst8u.ConvertTo(dst8u, MatType.CV_8U);
                return dst8u;
            }

            // Downsample for stable percentiles + speed
            using var sample = new Mat();
            if (src32f.Rows > 900 || src32f.Cols > 900)
            {
                Cv2.Resize(src32f, sample, new Size(), 0.5, 0.5, InterpolationFlags.Area);
            }
            else
            {
                src32f.CopyTo(sample); // ??? Cv2.Copy(src32f, sample)
            }

            Cv2.MinMaxLoc(sample, out double minV, out double maxV);
            if (maxV <= minV + 1e-12)
            {
                dst8u = Mat.Zeros(src32f.Size(), MatType.CV_8U);
                return dst8u;
            }

            // Histogram for percentiles
            //using var hist = new Mat();
            //var images = new[] { sample };
            //var channels = new[] { 0 };
            //var histSize = new[] { histBins };
            //var ranges = new[] { new Rangef((float)minV, (float)maxV) };

            //Cv2.CalcHist(images, channels, null, hist, 1, histSize, ranges, uniform: true, accumulate: false);

            //double total = 0;
            //for (int i = 0; i < histBins; i++) total += hist.Get<float>(i);

            //double targetLow = total * (pLow / 100.0);
            //double targetHigh = total * (pHigh / 100.0);

            //double csum = 0;
            //int idxLow = 0, idxHigh = histBins - 1;

            //for (int i = 0; i < histBins; i++)
            //{
            //    csum += hist.Get<float>(i);
            //    if (csum >= targetLow) { idxLow = i; break; }
            //}

            //csum = 0;
            //for (int i = 0; i < histBins; i++)
            //{
            //    csum += hist.Get<float>(i);
            //    if (csum >= targetHigh) { idxHigh = i; break; }
            //}

            using var hist = new Mat();
            var images = new[] { sample };
            var channels = new[] { 0 };
            var histSize = new[] { histBins };
            var ranges = new[] { new Rangef((float)minV, (float)maxV) };

            Cv2.CalcHist(images, channels, null, hist, 1, histSize, ranges, uniform: true, accumulate: false);

            // Быстрые проверки (полезно в debug)
            if (hist.Type() != MatType.CV_32F)
                throw new InvalidOperationException($"CalcHist returned {hist.Type()}, expected CV_32F.");

            if (!hist.IsContinuous())
                throw new InvalidOperationException("Histogram Mat is not continuous; pointer walk would be unsafe.");

            // total = количество выборок (пикселей) в sample, т.к. mask=null
            double total = (double)sample.Total();

            // целевые квантили
            double targetLow = total * (pLow / 100.0);
            double targetHigh = total * (pHigh / 100.0);

            // один проход по бинам
            int idxLow = 0, idxHigh = histBins - 1;
            double csum = 0;
            bool gotLow = false;

            unsafe
            {
                float* h = (float*)hist.DataPointer;

                for (int i = 0; i < histBins; i++)
                {
                    token.ThrowIfCancellationRequested();
                    csum += h[i];

                    if (!gotLow && csum >= targetLow)
                    {
                        idxLow = i;
                        gotLow = true;
                    }

                    if (csum >= targetHigh)
                    {
                        idxHigh = i;
                        break;
                    }
                }
            }

            double span = (maxV - minV);
            double lo = minV + (idxLow / (double)(histBins - 1)) * span;
            double hi = minV + (idxHigh / (double)(histBins - 1)) * span;

            if (hi <= lo + 1e-12)
            {
                Cv2.Normalize(src32f, dst8u, 0, 255, NormTypes.MinMax);
                dst8u.ConvertTo(dst8u, MatType.CV_8U);
                return dst8u;
            }

            // clip + scale
            using var clipped = new Mat();
            src32f.CopyTo(clipped);
            Cv2.Max(clipped, lo, clipped);
            Cv2.Min(clipped, hi, clipped);

            using var scaled = new Mat();
            Cv2.Subtract(clipped, lo, scaled);
            Cv2.Multiply(scaled, 255.0 / (hi - lo), scaled);

            scaled.ConvertTo(dst8u, MatType.CV_8U);
            return dst8u;
        }

        public static Mat LevelsAndGamma8U(
                            Mat src8u,
                            CancellationToken token,
                            double blackPct = 1.0,
                            double whitePct = 95.0,
                            double gamma = 0.85,      // <1 => brighten mids (background whiter)
                            byte targetWhite = 255)
        {
            if (src8u.Empty()) return new Mat();
            if (src8u.Type() != MatType.CV_8U || src8u.Channels() != 1)
                throw new ArgumentException("Expected CV_8U single-channel.");

            // histogram 256 bins
            using var hist = new Mat();
            Cv2.CalcHist(
                new[] { src8u },
                new[] { 0 },
                null,
                hist,
                1,
                new[] { 256 },
                new[] { new Rangef(0, 256) },
                uniform: true,
                accumulate: false);

            double total = src8u.Rows * (double)src8u.Cols;
            double lowTarget = total * (blackPct / 100.0);
            double highTarget = total * (whitePct / 100.0);

            int black = 0, white = 255;
            double c = 0;

            for (int i = 0; i < 256; i++)
            {
                c += hist.Get<float>(i);
                if (c >= lowTarget) { black = i; break; }
            }

            c = 0;
            for (int i = 0; i < 256; i++)
            {
                c += hist.Get<float>(i);
                if (c >= highTarget) { white = i; break; }
            }

            if (white <= black) { black = 0; white = 255; }
            token.ThrowIfCancellationRequested();
            // build LUT
            var lut = new byte[256];
            double denom = Math.Max(1.0, white - black);

            for (int i = 0; i < 256; i++)
            {
                double x = (i - black) / denom;
                if (x < 0) x = 0;
                if (x > 1) x = 1;

                x = Math.Pow(x, gamma);
                int y = (int)Math.Round(x * targetWhite);
                if (y < 0) y = 0;
                if (y > 255) y = 255;
                lut[i] = (byte)y;
            }
            token.ThrowIfCancellationRequested();
            using var lutMat = new Mat(1, 256, MatType.CV_8UC1);
            for (int i = 0; i < 256; i++)
                lutMat.Set(0, i, lut[i]);

            var dst = new Mat();
            Cv2.LUT(src8u, lutMat, dst);
            return dst;
        }

        public static Mat AdjustColor(
            CancellationToken token,
            Mat src,
            double redPercent,
            double greenPercent,
            double bluePercent,
            double hueDegrees,
            double saturationPercent)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Mat();

            Mat working = CloneAsBgr8U(src);
            try
            {
                token.ThrowIfCancellationRequested();

                double redScale = Math.Max(0.0, 1.0 + redPercent / 100.0);
                double greenScale = Math.Max(0.0, 1.0 + greenPercent / 100.0);
                double blueScale = Math.Max(0.0, 1.0 + bluePercent / 100.0);

                if (Math.Abs(redScale - 1.0) > 0.0001 ||
                    Math.Abs(greenScale - 1.0) > 0.0001 ||
                    Math.Abs(blueScale - 1.0) > 0.0001)
                {
                    using var lutB = BuildScaleLut(blueScale);
                    using var lutG = BuildScaleLut(greenScale);
                    using var lutR = BuildScaleLut(redScale);

                    var channels = working.Split();
                    try
                    {
                        Cv2.LUT(channels[0], lutB, channels[0]);
                        Cv2.LUT(channels[1], lutG, channels[1]);
                        Cv2.LUT(channels[2], lutR, channels[2]);
                        Cv2.Merge(channels, working);
                    }
                    finally
                    {
                        foreach (var ch in channels)
                        {
                            if (!ch.IsDisposed)
                                ch.Dispose();
                        }
                    }
                }

                int hueShift = (int)Math.Round(hueDegrees / 2.0);
                hueShift = Math.Max(-90, Math.Min(90, hueShift));
                double saturationScale = Math.Max(0.0, 1.0 + saturationPercent / 100.0);

                if (hueShift != 0 || Math.Abs(saturationScale - 1.0) > 0.0001)
                {
                    using var hsv = new Mat();
                    Cv2.CvtColor(working, hsv, ColorConversionCodes.BGR2HSV);

                    var hsvChannels = hsv.Split();
                    try
                    {
                        if (hueShift != 0)
                        {
                            using var lutH = BuildHueLut(hueShift);
                            Cv2.LUT(hsvChannels[0], lutH, hsvChannels[0]);
                        }

                        if (Math.Abs(saturationScale - 1.0) > 0.0001)
                        {
                            using var lutS = BuildScaleLut(saturationScale);
                            Cv2.LUT(hsvChannels[1], lutS, hsvChannels[1]);
                        }

                        Cv2.Merge(hsvChannels, hsv);
                    }
                    finally
                    {
                        foreach (var ch in hsvChannels)
                        {
                            if (!ch.IsDisposed)
                                ch.Dispose();
                        }
                    }

                    var adjusted = new Mat();
                    Cv2.CvtColor(hsv, adjusted, ColorConversionCodes.HSV2BGR);
                    working.Dispose();
                    working = adjusted;
                }

                return working;
            }
            catch
            {
                working.Dispose();
                throw;
            }
        }

        public static Mat AdjustBrightnessContrast(
            CancellationToken token,
            Mat src,
            double brightness,
            double contrast)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));
            if (src.Empty()) return new Mat();

            Mat working = CloneAsBgr8U(src);
            try
            {
                token.ThrowIfCancellationRequested();

                double alpha = (contrast + 100.0) / 100.0;
                alpha = Math.Max(0.0, Math.Min(3.0, alpha));
                double beta = Math.Max(-255.0, Math.Min(255.0, brightness));

                var dst = new Mat();
                working.ConvertTo(dst, MatType.CV_8UC3, alpha, beta);
                working.Dispose();
                return dst;
            }
            catch
            {
                working.Dispose();
                throw;
            }
        }

        private static Mat CloneAsBgr8U(Mat src)
        {
            if (src.Channels() == 3)
            {
                if (src.Type() == MatType.CV_8UC3)
                    return src.Clone();

                var bgr3 = new Mat();
                src.ConvertTo(bgr3, MatType.CV_8UC3);
                return bgr3;
            }

            if (src.Channels() == 4)
            {
                if (src.Type() == MatType.CV_8UC4)
                {
                    var bgr4 = new Mat();
                    Cv2.CvtColor(src, bgr4, ColorConversionCodes.BGRA2BGR);
                    return bgr4;
                }

                using var tmp = new Mat();
                src.ConvertTo(tmp, MatType.CV_8UC4);
                var bgrFrom4 = new Mat();
                Cv2.CvtColor(tmp, bgrFrom4, ColorConversionCodes.BGRA2BGR);
                return bgrFrom4;
            }

            if (src.Channels() == 1)
            {
                var bgr1 = new Mat();
                if (src.Type() == MatType.CV_8UC1)
                {
                    Cv2.CvtColor(src, bgr1, ColorConversionCodes.GRAY2BGR);
                    return bgr1;
                }

                using var tmp = new Mat();
                src.ConvertTo(tmp, MatType.CV_8UC1);
                Cv2.CvtColor(tmp, bgr1, ColorConversionCodes.GRAY2BGR);
                return bgr1;
            }

            throw new ArgumentException("Expected 1, 3, or 4 channel Mat.", nameof(src));
        }

        private static Mat BuildScaleLut(double scale)
        {
            var lut = new Mat(1, 256, MatType.CV_8UC1);
            for (int i = 0; i < 256; i++)
            {
                int v = (int)Math.Round(i * scale);
                if (v < 0) v = 0;
                if (v > 255) v = 255;
                lut.Set(0, i, (byte)v);
            }
            return lut;
        }

        private static Mat BuildHueLut(int shift)
        {
            var lut = new Mat(1, 256, MatType.CV_8UC1);
            for (int i = 0; i < 256; i++)
            {
                int v = i;
                if (i < 180)
                {
                    v = (i + shift) % 180;
                    if (v < 0) v += 180;
                }
                lut.Set(0, i, (byte)v);
            }
            return lut;
        }

    }

}

