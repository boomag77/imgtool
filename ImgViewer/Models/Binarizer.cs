using OpenCvSharp;
using System.Diagnostics;
using System.Web.SessionState;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models
{
    internal static class Binarizer
    {

        public enum PreBinarizationMethod
        {
            None,
            HomomorphicRetinex
        }

        public struct PreBinarizationParameters
        {
            public PreBinarizationMethod Method { get; set; }
            public bool UseLabLChannel { get; set;  }
            public double HomomorphicSigma { get; set; }
            public double HomomorphicGammaHigh { get; set; }
            public double HomomorphicGammaLow { get; set; }
            public double HomomorphicEps { get; set; }
            public bool HomomorphicApplyClahe { get; set; }
            public double HomomorphicClaheClipLimit { get; set; }
            public Size? HomomorphicClaheTileSize { get; set; }

            public PreBinarizationParameters(
                PreBinarizationMethod method = PreBinarizationMethod.None,
                bool useLabLChannel = true,
                double homomorphicSigma = 50.0,
                double homomorphicGammaHigh = 1.6,
                double homomorphicGammaLow = 0.7,
                double homomorphicEps = 1e-6,
                bool homomorphicApplyClahe = false,
                double homomorphicClaheClipLimit = 2.0,
                Size? homomorphicClaheTileSize = null)
            {
                Method = method;
                UseLabLChannel = useLabLChannel;
                HomomorphicSigma = homomorphicSigma;
                HomomorphicGammaHigh = homomorphicGammaHigh;
                HomomorphicGammaLow = homomorphicGammaLow;
                HomomorphicEps = homomorphicEps;
                HomomorphicApplyClahe = homomorphicApplyClahe;
                HomomorphicClaheClipLimit = homomorphicClaheClipLimit;
                HomomorphicClaheTileSize = homomorphicClaheTileSize;
            }
        }



        public static Mat Binarize(Mat src, BinarizeMethod binMethod, BinarizeParameters binParams, PreBinarizationParameters preBinParams = default)
        {
            
            if (preBinParams.Method != PreBinarizationMethod.None)
            {
                switch (preBinParams.Method)
                {
                    case PreBinarizationMethod.HomomorphicRetinex:
                        {
                            using var preBinMat = PreBinarization.HomomorphicRetinex(
                                                                        src,
                                                                        preBinParams.UseLabLChannel,
                                                                        preBinParams.HomomorphicSigma,
                                                                        preBinParams.HomomorphicGammaHigh,
                                                                        preBinParams.HomomorphicGammaLow,
                                                                        preBinParams.HomomorphicEps,
                                                                        preBinParams.HomomorphicApplyClahe,
                                                                        preBinParams.HomomorphicClaheClipLimit,
                                                                        preBinParams.HomomorphicClaheTileSize);
                            return Binarize(preBinMat, binMethod, binParams);
                        }
                    default:
                        throw new NotImplementedException($"Pre-binarization method {preBinParams.Method} is not implemented.");
                }
            }
            switch (binMethod)
            {
                case BinarizeMethod.Sauvola:
                    return SauvolaBinarize(src, binParams);
                case BinarizeMethod.Threshold:
                    return BinarizeThreshold(src, binParams.Threshold);
                case BinarizeMethod.Adaptive:
                    return BinarizeAdaptive(src, binParams);
                default:
                    throw new NotImplementedException($"Binarization method {binMethod} is not implemented.");
            }
        }

        private static Mat BinarizeAdaptive(Mat src, BinarizeParameters p, bool invert = false)
        {
            using var gray = Helper.MatToGray(src);

            int bs;
            if (p.BlockSize.HasValue && p.BlockSize > 0)
            {
                bs = p.BlockSize.Value;
            }
            else
            {
                // heuristic: блок ~min(width, height) / 30, clamp to[3..201]
                int baseBs = Math.Max(3, Math.Min(201, Math.Min(gray.Cols, gray.Rows) / 30));
                if ((baseBs & 1) == 0) baseBs++; // сделать нечётным
                bs = baseBs;
            }
            if (bs < 3) bs = 3;
            if ((bs & 1) == 0) bs++; // сделать нечётным

            using var blur = new Mat();
            Cv2.GaussianBlur(gray, blur, new OpenCvSharp.Size(1, 1), 0);

            using var bin = new Mat();
            var adaptiveType = p.UseGaussian ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC;
            var threshType = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            Cv2.AdaptiveThreshold(blur, bin, 255, adaptiveType, threshType, bs, p.MeanC);

            if (p.UseMorphology)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(p.MorphKernelBinarize, p.MorphKernelBinarize));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel, iterations: p.MorphIterationsBinarize);
            }

            using var color = new Mat();
            Cv2.CvtColor(bin, color, ColorConversionCodes.GRAY2BGR);

            return color.Clone();

        }

        private static Mat BinarizeThreshold(Mat src, int threshold = 128)
        {
            if (src == null || src.Empty()) return new Mat();

            using var gray = Helper.MatToGray(src);


            Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);

            // Конвертируем обратно в BGR — тогда весь pipeline, ожидающий 3 канала, продолжит работать
            using var color = new Mat();
            Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);

            return color.Clone(); // сохраняем результат как 3-канальную матрицу
        }

        private static Mat Sauvola(Mat src, int windowSize = 25, double k = 0.34, double R = 180.0, int pencilStrokeBoost = 0)
        {

            Mat gray = Helper.MatToGray(src);

            if (windowSize % 2 == 0) windowSize++; // ensure odd

            // Convert to double for precision
            Mat srcD = new Mat();
            gray.ConvertTo(srcD, MatType.CV_64F);

            // mean = boxFilter(src, ksize) normalized
            Mat mean = new Mat();
            Cv2.BoxFilter(srcD, mean, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // meanSq: compute boxFilter(src*src)
            Mat sq = new Mat();
            Cv2.Multiply(srcD, srcD, sq);
            Mat meanSq = new Mat();
            Cv2.BoxFilter(sq, meanSq, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // std = sqrt(meanSq - mean*mean)
            Mat std = new Mat();
            Cv2.Subtract(meanSq, mean.Mul(mean), std); // std now holds variance
            Cv2.Max(std, 0.0, std); // clamp small negatives
            Cv2.Sqrt(std, std);

            // threshold = mean * (1 + k * (std/R - 1))
            Mat thresh = new Mat();
            Cv2.Divide(std, R, thresh);                 // thresh = std / R
            Cv2.Subtract(thresh, 1.0, thresh);          // thresh = std/R - 1
            Cv2.Multiply(thresh, k, thresh);            // thresh = k*(std/R -1)
            Cv2.Add(thresh, 1.0, thresh);               // thresh = 1 + k*(std/R -1)
            Cv2.Multiply(mean, thresh, thresh);         // thresh = mean * (...)

            double pencilMargin = (double)pencilStrokeBoost; // TODO: вынести в BinarizeParameters (SauvolaPencilMargin)
            using var threshShifted = new Mat();
            Cv2.Add(thresh, new Scalar(pencilMargin), threshShifted);
            Cv2.Min(threshShifted, new Scalar(255.0), threshShifted);

            // binarize: srcD > thresh -> 255 else 0
            Mat bin = new Mat();
            Cv2.Compare(srcD, threshShifted, bin, CmpType.GT); // bin = 0 or 255 (CV_8U after convert)
            bin.ConvertTo(bin, MatType.CV_8UC1, 255.0);  // ensure 0/255

            // Clean-up mats
            srcD.Dispose(); mean.Dispose(); sq.Dispose(); meanSq.Dispose(); std.Dispose(); thresh.Dispose();

            return bin;
        }

        private static Mat SauvolaBinarize(Mat src, BinarizeParameters p)
        {
            //Debug.WriteLine($"clahe grid size {p.SauvolaClaheGridSize}");
            using var binMat = BinarizeForHandwritten(src,
                                                      p.SauvolaUseClahe,
                                                      p.SauvolaClaheClip,
                                                      p.SauvolaClaheGridSize,
                                                      p.SauvolaWindowSize,
                                                      p.SauvolaK,
                                                      p.SauvolaR,
                                                      p.SauvolaMorphRadius,
                                                      p.PencilStrokeBoost);

            Mat bin8;
            if (binMat.Type() != MatType.CV_8UC1)
            {
                bin8 = new Mat();
                binMat.ConvertTo(bin8, MatType.CV_8UC1);
            }
            else
            {
                bin8 = binMat.Clone(); // сделаем клон, чтобы безопасно Dispose оригинала ниже
            }

            try
            {
                var colorMat = new Mat();
                Cv2.CvtColor(bin8, colorMat, ColorConversionCodes.GRAY2BGR);
                return colorMat;
            }
            finally
            {
                bin8.Dispose();
            }

        }

        private static Mat BinarizeForHandwritten(Mat src, bool useClahe = true, double claheClip = 12.0, int claheGridSize = 8,
                                             int sauvolaWindow = 35, double sauvolaK = 0.34, double sauvolaR = 180, int morphRadius = 0, int pencilStrokeBoost = 0)
        {
            var claheGrid = new OpenCvSharp.Size(claheGridSize, claheGridSize);
            Mat gray = src;
            if (src.Type() != MatType.CV_8UC1)
            {
                gray = new Mat();
                Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            }
            Mat pre = gray;
            if (useClahe)
            {
                var clahe = Cv2.CreateCLAHE(claheClip, claheGrid);
                pre = new Mat();
                clahe.Apply(gray, pre);
                clahe.Dispose();
                if (!ReferenceEquals(gray, src)) gray.Dispose();
            }

            var bin = Sauvola(pre, sauvolaWindow, sauvolaK, sauvolaR, pencilStrokeBoost);

            // optional morphological cleaning (open to remove small noise, close to fill holes)
            if (morphRadius > 0)
            {
                var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(2 * morphRadius + 1, 2 * morphRadius + 1));
                var cleaned = new Mat();
                Cv2.MorphologyEx(bin, cleaned, MorphTypes.Open, kernel);
                Cv2.MorphologyEx(cleaned, bin, MorphTypes.Close, kernel);
                kernel.Dispose();
                cleaned.Dispose();
            }

            if (!ReferenceEquals(pre, gray) && pre != src) pre.Dispose();
            if (gray != src && gray != pre) gray.Dispose();

            return bin;
        }

        private static class PreBinarization
        {
            /// <summary>
            /// Homomorphic filtering / Retinex-like normalization for document images.
            /// Returns 8-bit single-channel Mat suitable for Sauvola/Adaptive/Otsu.
            /// </summary>
            public static Mat HomomorphicRetinex(
                Mat src,
                bool useLabL = true,            // true = extract L from Lab (best for paper), false = Gray
                double sigma = 50.0,            // blur sigma in log-domain; scale-dependent
                double gammaHigh = 1.6,         // boost details (ink strokes)
                double gammaLow = 0.7,          // keep some low-freq to avoid over-flattening
                double eps = 1e-6,              // avoid log(0)
                bool applyClahe = false,        // optional: mild local contrast on result
                double claheClipLimit = 2.0,
                Size? claheTile = null)
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

                // 6) outLog = gammaHigh*high + gammaLow*low
                using var outLog = new Mat();
                Cv2.AddWeighted(high, gammaHigh, low, gammaLow, 0.0, outLog);

                // 7) exp(outLog)
                using var expI = new Mat();
                Cv2.Exp(outLog, expI);

                // 8) normalize -> 8U
                var out8u = new Mat();
                Cv2.Normalize(expI, out8u, 0, 255, NormTypes.MinMax);
                out8u.ConvertTo(out8u, MatType.CV_8U);

                // Optional: CLAHE (usually only if you still have weak strokes)
                if (applyClahe)
                {
                    var tile = claheTile ?? new Size(8, 8);
                    using var clahe = Cv2.CreateCLAHE(claheClipLimit, tile);
                    using var tmp = new Mat();
                    clahe.Apply(out8u, tmp);
                    out8u.Dispose();
                    return tmp.Clone();
                }

                return out8u;
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

                // If BGRA -> BGR
                using var bgr = (src.Channels() == 4)
                    ? src.CvtColor(ColorConversionCodes.BGRA2BGR)
                    : src;

                if (!useLabL)
                {
                    var gray = new Mat();
                    Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
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
                }
            }
        }

        private static class Helper
        {
            public static void DumpStruct<T>(T strct)
            {
                var props = typeof(T).GetProperties();
                foreach (var prop in props)
                {
                    var value = prop.GetValue(strct);
                    System.Diagnostics.Debug.WriteLine($"{prop.Name}: {value}");
                }
            }

            public static Mat MatToGray(Mat src)
            {
                if (src == null || src.Empty()) return null; // уже в градациях серого
                var gray = new Mat();
                try
                {
                    switch (src.Channels())
                    {
                        case 1:
                            // уже в градациях серого
                            src.CopyTo(gray);
                            break;
                        case 3:
                            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
                            break;
                        case 4:
                            Cv2.CvtColor(src, gray, ColorConversionCodes.BGRA2GRAY);
                            break;
                        default:
                            throw new ArgumentException("MatToGray supports only 1, 3, or 4 channel Mats", nameof(src));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MatToGray error: {ex}");
                    return gray;
                }


                return gray;
            }
        }
    }
}
