using OpenCvSharp;
using System.Diagnostics;
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
            public bool UseLabLChannel { get; set; }
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



        public static Mat Binarize(Mat src, BinarizeMethod binMethod, BinarizeParameters binParams)
        {
            if (src.Channels() != 1)
            {
                src = Helper.MatToGray(src);
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

            var bin = new Mat();
            var adaptiveType = p.UseGaussian ? AdaptiveThresholdTypes.GaussianC : AdaptiveThresholdTypes.MeanC;
            var threshType = invert ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
            Cv2.AdaptiveThreshold(blur, bin, 255, adaptiveType, threshType, bs, p.MeanC);

            if (p.UseMorphology)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new OpenCvSharp.Size(p.MorphKernelBinarize, p.MorphKernelBinarize));
                Cv2.MorphologyEx(bin, bin, MorphTypes.Open, kernel, iterations: p.MorphIterationsBinarize);
            }

            //var color = new Mat();
            //Cv2.CvtColor(bin, color, ColorConversionCodes.GRAY2BGR);

            return bin;

        }

        private static Mat BinarizeThreshold(Mat src, int threshold = 128)
        {
            if (src == null || src.Empty()) return new Mat();

            //var gray = Helper.MatToGray(src);
            var bin = new Mat();


            Cv2.Threshold(src, bin, threshold, 255, ThresholdTypes.Binary);

            // Конвертируем обратно в BGR — тогда весь pipeline, ожидающий 3 канала, продолжит работать
            //var color = new Mat();
            //Cv2.CvtColor(gray, color, ColorConversionCodes.GRAY2BGR);

            return bin;
        }

        private static Mat Sauvola(Mat src, int windowSize = 25, double k = 0.34, double R = 180.0, int pencilStrokeBoost = 0)
        {

            //using Mat gray = Helper.MatToGray(src);

            if (windowSize % 2 == 0) windowSize++; // ensure odd

            // Convert to double for precision
            using var srcD = new Mat();
            src.ConvertTo(srcD, MatType.CV_64F);

            // mean = boxFilter(src, ksize) normalized
            using var mean = new Mat();
            Cv2.BoxFilter(srcD, mean, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // meanSq: compute boxFilter(src*src)
            using var sq = new Mat();
            Cv2.Multiply(srcD, srcD, sq);
            using var meanSq = new Mat();
            Cv2.BoxFilter(sq, meanSq, MatType.CV_64F, new OpenCvSharp.Size(windowSize, windowSize), anchor: new OpenCvSharp.Point(-1, -1), normalize: true, borderType: BorderTypes.Reflect101);

            // std = sqrt(meanSq - mean*mean)
            using var std = new Mat();

            //using var meanMul = mean.Mul(mean);
            //Cv2.Subtract(meanSq, meanMul, std); // std now holds variance

            Cv2.Multiply(mean, mean, sq); // std = mean*mean
            Cv2.Subtract(meanSq, sq, std); // std = meanSq - mean*mean (variance)

            Cv2.Max(std, 0.0, std); // clamp small negatives
            Cv2.Sqrt(std, std);

            // threshold = mean * (1 + k * (std/R - 1))
            using var thresh = new Mat();
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
            Mat bin = new();
            Cv2.Compare(srcD, threshShifted, bin, CmpType.GT); // bin = 0 or 255 (CV_8U after convert)
            bin.ConvertTo(bin, MatType.CV_8UC1, 255.0);  // ensure 0/255


            return bin;
        }

        private static Mat SauvolaBinarize(Mat src, BinarizeParameters p)
        {
            //Debug.WriteLine($"clahe grid size {p.SauvolaClaheGridSize}");
            var binMat = BinarizeForHandwritten(src,
                                                      p.SauvolaUseClahe,
                                                      p.SauvolaClaheClip,
                                                      p.SauvolaClaheGridSize,
                                                      p.SauvolaWindowSize,
                                                      p.SauvolaK,
                                                      p.SauvolaR,
                                                      p.SauvolaMorphRadius,
                                                      p.PencilStrokeBoost);

            return binMat;

        }

        private static Mat BinarizeForHandwritten(Mat src, bool useClahe = true, double claheClip = 12.0, int claheGridSize = 8,
                                             int sauvolaWindow = 35, double sauvolaK = 0.34, double sauvolaR = 180, int morphRadius = 0, int pencilStrokeBoost = 0)
        {

            //Mat gray = src;
            //bool ownsGray = false;
            //if (src.Type() != MatType.CV_8UC1)
            //{
            //    gray = new Mat();
            //    Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            //    ownsGray = true;
            //}
            //Mat pre = src;
            //bool ownsPre = false;
            if (useClahe)
            {
                var claheGrid = new OpenCvSharp.Size(claheGridSize, claheGridSize);
                using var clahe = Cv2.CreateCLAHE(claheClip, claheGrid);
                //pre = new Mat();
                clahe.Apply(src, src);
                //ownsPre = true;
            }

            var bin = Sauvola(src, sauvolaWindow, sauvolaK, sauvolaR, pencilStrokeBoost);

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

            //if (ownsPre) pre.Dispose();
            //if (ownsGray) gray.Dispose();

            return bin;
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
                if (src == null) throw new ArgumentNullException(nameof(src));
                if (src.Empty()) throw new InvalidOperationException("MatToGray: src is empty");

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
                    throw;
                }


                return gray;
            }
        }
    }
}
