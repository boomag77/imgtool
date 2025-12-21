using System;
using System.Linq;
using System.Threading;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models
{
public sealed class PageSplitter
{
    public sealed class Settings
    {
        // --- Search band (fraction of width) where we expect the gutter ---
        public double CentralBandStart = 0.35; // 35% of width
        public double CentralBandEnd = 0.65; // 65% of width

        // Extra pixels added to each side from the split line (to avoid cutting near-gutter text)
        public int PadPx = 24;

        // Performance: downscale for analysis only (cropping is done on original)
        public int AnalysisMaxWidth = 1400;

        // --- Ink mask (content) ---
        public bool UseClahe = true;
        public double ClaheClipLimit = 2.0;
        public int ClaheTileGrid = 8;

        // AdaptiveThreshold params (BinaryInv)
        public int AdaptiveBlockSize = 31; // must be odd
        public double AdaptiveC = 10;

        // Morph close to connect characters into blobs (horizontal bias)
        public double CloseKernelWidthFrac = 0.025; // kernelWidth = W * frac
        public int CloseKernelHeightPx = 3;

        // Smooth projection curve
        public int SmoothWindowPx = 41; // should be odd-ish (we'll auto-fix)

        // --- Confidence thresholds ---
        public double MinConfidence = 0.28;   // if final confidence < this => return Failure (or you can ignore)
        public bool ThrowIfLowConfidence = false;

        // --- Lab confirmation ---
        public bool UseLabConfirmation = true;

        // half-width around splitX treated as gutter for Lab stats
        public int LabGutterHalfWidthPx = 18;

        // neighbor width (on each side) to compare with gutter
        public int LabNeighborWidthPx = 70;

        // Minimum brightness delta in L channel: gutter should be brighter than neighbors by at least this
        // L is [0..255] in OpenCV after conversion to 8-bit.
        public double MinLDiff = 6.0;

        // Texture ratio: std(gutter) should be <= std(neighbors) * thisRatio
        // e.g. 0.85 means gutter is at least 15% less textured.
        public double MaxGutterStdRatio = 0.88;

        // Blend weights for final confidence
        public double WeightProjection = 0.70;
        public double WeightLab = 0.30;
    }

    public sealed class SplitResult : IDisposable
    {
        public bool Success;
        public Mat? Left;
        public Mat? Right;

        public int SplitX;          // in original image coordinates
        public int SplitX_Analysis; // in analysis image coordinates
        public double ProjectionConfidence;
        public double LabConfidence;
        public double FinalConfidence;

        public string? Reason;

        // Optional overlay for debugging (line + band), null unless requested
        public Mat? DebugOverlay;

        public void Dispose()
        {
            Left?.Dispose();
            Right?.Dispose();
            DebugOverlay?.Dispose();
        }
    }

    private readonly Settings _s;

    public PageSplitter(Settings? settings = null)
    {
        _s = settings ?? new Settings();
        NormalizeSettings(_s);
    }

    public SplitResult Split(Mat src, bool createDebugOverlay = false, CancellationToken token = default)
    {
        if (src == null || src.Empty())
            throw new ArgumentException("src is null or empty");

        token.ThrowIfCancellationRequested();

        // 1) Normalize input to BGR for analysis; keep original for cropping (but we crop from src itself).
        using var srcBgr = EnsureBgr(src);

        int origW = srcBgr.Cols;
        int origH = srcBgr.Rows;

        // 2) Create analysis image (downscaled copy for speed)
        double scale = 1.0;
        using var analysis = new Mat();
        if (_s.AnalysisMaxWidth > 0 && origW > _s.AnalysisMaxWidth)
        {
            scale = (double)_s.AnalysisMaxWidth / origW;
            Cv2.Resize(srcBgr, analysis, new Size(_s.AnalysisMaxWidth, (int)Math.Round(origH * scale)), 0, 0, InterpolationFlags.Area);
        }
        else
        {
            srcBgr.CopyTo(analysis);
        }

        token.ThrowIfCancellationRequested();

        int W = analysis.Cols;
        int H = analysis.Rows;

        // 3) Build ink mask (BinaryInv): text/ink => white(255), background => black(0)
        using var inkMask = BuildInkMask(analysis, token);

        token.ThrowIfCancellationRequested();

        // 4) Vertical projection: fast via Reduce(SUM) over rows
        var colSum = ComputeColumnSum(inkMask); // length W, proportional to ink density

        // 5) Smooth curve
        var smooth = SmoothMovingAverage(colSum, FixOdd(_s.SmoothWindowPx));

        // 6) Find valley (min) in central band
        int bandStart = Clamp((int)Math.Round(W * _s.CentralBandStart), 0, W - 1);
        int bandEnd = Clamp((int)Math.Round(W * _s.CentralBandEnd), 0, W - 1);
        if (bandEnd <= bandStart) (bandStart, bandEnd) = (Math.Max(0, W / 3), Math.Min(W - 1, 2 * W / 3));

        int splitX_A = ArgMin(smooth, bandStart, bandEnd);

        // 7) Projection confidence: compare valley vs median level in band
        double projConf = ComputeProjectionConfidence(smooth, bandStart, bandEnd, splitX_A);

        token.ThrowIfCancellationRequested();

        // 8) Lab confirmation (L brightness + texture)
        double labConf = 0.0;
        if (_s.UseLabConfirmation)
            labConf = ComputeLabConfidence(analysis, splitX_A, token);

        // 9) Blend final confidence
        double finalConf = Clamp01(_s.WeightProjection * projConf + _s.WeightLab * labConf);

        // 10) Map splitX to original coords
        int splitX_Orig = scale == 1.0 ? splitX_A : (int)Math.Round(splitX_A / scale);

        // 11) Crop from ORIGINAL src (not analysis!) with padding
        int pad = Math.Max(0, _s.PadPx);
        int leftW = Clamp(splitX_Orig + pad, 1, origW);
        int rightX = Clamp(splitX_Orig - pad, 0, origW - 1);
        int rightW = Clamp(origW - rightX, 1, origW);

        var result = new SplitResult
        {
            SplitX = splitX_Orig,
            SplitX_Analysis = splitX_A,
            ProjectionConfidence = projConf,
            LabConfidence = labConf,
            FinalConfidence = finalConf
        };

        if (finalConf < _s.MinConfidence)
        {
            result.Success = false;
            result.Reason = $"Low confidence: final={finalConf:F3}, proj={projConf:F3}, lab={labConf:F3}";

            if (_s.ThrowIfLowConfidence)
                throw new InvalidOperationException(result.Reason);

            if (createDebugOverlay)
                result.DebugOverlay = BuildDebugOverlay(analysis, splitX_A, bandStart, bandEnd);

            return result;
        }

        // Clone() so the returned Mats are independent from src lifetime
        result.Left = new Mat(src, new Rect(0, 0, leftW, origH)).Clone();
        result.Right = new Mat(src, new Rect(rightX, 0, rightW, origH)).Clone();
        result.Success = true;

        if (createDebugOverlay)
            result.DebugOverlay = BuildDebugOverlay(analysis, splitX_A, bandStart, bandEnd);

        return result;
    }

    // ----------------------------
    // Helpers
    // ----------------------------

    private static Mat EnsureBgr(Mat src)
    {
        // Return a Mat that is BGR (CV_8UC3). Caller disposes it.
        if (src.Type() == MatType.CV_8UC3)
            return src.Clone();

        if (src.Type() == MatType.CV_8UC4)
        {
            var bgr = new Mat();
            Cv2.CvtColor(src, bgr, ColorConversionCodes.BGRA2BGR);
            return bgr;
        }

        if (src.Type() == MatType.CV_8UC1)
        {
            var bgr = new Mat();
            Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
            return bgr;
        }

        throw new ArgumentException($"Unsupported Mat type: {src.Type()} (expected CV_8UC1/3/4)");
    }

    private Mat BuildInkMask(Mat bgr, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        using var gray = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);

        // optional CLAHE to stabilize thresholding under shadows/gradients
        if (_s.UseClahe)
        {
            using var clahe = Cv2.CreateCLAHE(_s.ClaheClipLimit, new Size(_s.ClaheTileGrid, _s.ClaheTileGrid));
            clahe.Apply(gray, gray);
        }

        // slight blur to suppress sensor noise
        Cv2.GaussianBlur(gray, gray, new Size(5, 5), 0);

        token.ThrowIfCancellationRequested();

        // Adaptive threshold to get "ink"
        var bin = new Mat();
        Cv2.AdaptiveThreshold(
            gray, bin, 255,
            AdaptiveThresholdTypes.GaussianC,
            ThresholdTypes.BinaryInv,
            FixOdd(_s.AdaptiveBlockSize),
            _s.AdaptiveC);

        token.ThrowIfCancellationRequested();

        // Morph close (horizontal bias) to connect letters into blobs
        int kW = Math.Max(15, (int)Math.Round(bgr.Cols * _s.CloseKernelWidthFrac));
        int kH = Math.Max(1, _s.CloseKernelHeightPx);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(kW, kH));
        Cv2.MorphologyEx(bin, bin, MorphTypes.Close, kernel);

        return bin; // caller disposes
    }

    private static double[] ComputeColumnSum(Mat binaryInvMask)
    {
        // binaryInvMask is 0 or 255; we sum over rows -> 1xW
        // ReduceDimension.Row => reduce rows, keep columns
        using var proj = new Mat();
        Cv2.Reduce(binaryInvMask, proj, ReduceDimension.Row, ReduceTypes.Sum, (int)MatType.CV_32SC1);

        int W = proj.Cols;
        var colSum = new double[W];

        // proj is 1xW, int sums of 0/255
        for (int x = 0; x < W; x++)
        {
            int s = proj.At<int>(0, x);
            colSum[x] = s / 255.0; // convert to "count of ink pixels"
        }

        return colSum;
    }

    private static double[] SmoothMovingAverage(double[] data, int window)
    {
        if (data.Length == 0) return Array.Empty<double>();
        if (window <= 1) return (double[])data.Clone();

        window = Math.Min(window, data.Length);
        if (window % 2 == 0) window++;

        int n = data.Length;
        var prefix = new double[n + 1];
        for (int i = 0; i < n; i++)
            prefix[i + 1] = prefix[i] + data[i];

        int r = window / 2;
        var outArr = new double[n];

        for (int i = 0; i < n; i++)
        {
            int a = Math.Max(0, i - r);
            int b = Math.Min(n - 1, i + r);
            double sum = prefix[b + 1] - prefix[a];
            outArr[i] = sum / (b - a + 1);
        }

        return outArr;
    }

    private static int ArgMin(double[] data, int start, int end)
    {
        start = Clamp(start, 0, data.Length - 1);
        end = Clamp(end, 0, data.Length - 1);
        if (end < start) (start, end) = (end, start);

        int best = start;
        double bestVal = data[start];

        for (int i = start + 1; i <= end; i++)
        {
            double v = data[i];
            if (v < bestVal)
            {
                bestVal = v;
                best = i;
            }
        }
        return best;
    }

    private static double ComputeProjectionConfidence(double[] smooth, int bandStart, int bandEnd, int splitX)
    {
        // Confidence based on how deep the valley is compared to typical ink level in the band.
        // If the valley is much smaller than median => strong confidence.
        var band = smooth.Skip(bandStart).Take(bandEnd - bandStart + 1).ToArray();
        double median = Median(band);
        double minVal = smooth[splitX];

        // Avoid divide by zero; if almost no ink everywhere, confidence should be low.
        if (median < 1.0) return 0.0;

        double ratio = minVal / median;        // smaller is better
        double conf = 1.0 - Clamp01(ratio);    // 1 when minVal~0, 0 when minVal>=median
        return Clamp01(conf);
    }

    private double ComputeLabConfidence(Mat bgr, int splitX, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        // Convert to Lab, take L channel
        using var lab = new Mat();
        Cv2.CvtColor(bgr, lab, ColorConversionCodes.BGR2Lab);

        using var L = new Mat();
        Cv2.ExtractChannel(lab, L, 0); // L channel

        int W = L.Cols;
        int H = L.Rows;

        int gHalf = Math.Max(4, _s.LabGutterHalfWidthPx);
        int nW = Math.Max(12, _s.LabNeighborWidthPx);

        // gutter band [gx0..gx1]
        int gx0 = Clamp(splitX - gHalf, 0, W - 1);
        int gx1 = Clamp(splitX + gHalf, 0, W - 1);

        // left neighbor band [lx0..lx1], right neighbor band [rx0..rx1]
        int lx1 = Clamp(gx0 - 1, 0, W - 1);
        int lx0 = Clamp(lx1 - nW + 1, 0, W - 1);

        int rx0 = Clamp(gx1 + 1, 0, W - 1);
        int rx1 = Clamp(rx0 + nW - 1, 0, W - 1);

        // If neighbors are degenerate (near edges), Lab confirm becomes weak.
        if (lx1 <= lx0 || rx1 <= rx0 || gx1 <= gx0)
            return 0.0;

        // Compute mean and std of L in each band
        (double meanG, double stdG) = MeanStd(L, gx0, gx1, H);
        (double meanL, double stdL) = MeanStd(L, lx0, lx1, H);
        (double meanR, double stdR) = MeanStd(L, rx0, rx1, H);

        token.ThrowIfCancellationRequested();

        double neighMean = (meanL + meanR) * 0.5;
        double neighStd = (stdL + stdR) * 0.5;

        // brightness score: gutter should be brighter than neighbors
        double lDiff = meanG - neighMean;
        double brightnessScore = Clamp01((lDiff - _s.MinLDiff) / 20.0); // scale: 20 L-levels to reach 1

        // texture score: gutter should be less textured (lower std)
        // If neighbors are very smooth too, this cue is weak.
        if (neighStd < 2.0)
            neighStd = 2.0;

        double stdRatio = stdG / neighStd; // smaller is better
        double textureScore = stdRatio <= _s.MaxGutterStdRatio
            ? Clamp01((_s.MaxGutterStdRatio - stdRatio) / _s.MaxGutterStdRatio)
            : 0.0;

        // Combine Lab cues (simple AND-like blend)
        double labConf = 0.65 * brightnessScore + 0.35 * textureScore;
        return Clamp01(labConf);
    }

    private static (double mean, double std) MeanStd(Mat L8u, int x0, int x1, int H)
    {
        // Compute mean/std for sub-rect [x0..x1] over all rows.
        int w = x1 - x0 + 1;
        var roi = new Rect(x0, 0, w, H);

        using var sub = new Mat(L8u, roi);
        Cv2.MeanStdDev(sub, out Scalar mean, out Scalar std);
        return (mean.Val0, std.Val0);
    }

    private static Mat BuildDebugOverlay(Mat analysisBgr, int splitX, int bandStart, int bandEnd)
    {
        var overlay = analysisBgr.Clone();

        int H = overlay.Rows;
        // central band markers
        Cv2.Line(overlay, new Point(bandStart, 0), new Point(bandStart, H - 1), new Scalar(0, 255, 255), 2);
        Cv2.Line(overlay, new Point(bandEnd, 0), new Point(bandEnd, H - 1), new Scalar(0, 255, 255), 2);

        // split line
        Cv2.Line(overlay, new Point(splitX, 0), new Point(splitX, H - 1), new Scalar(0, 0, 255), 2);

        return overlay;
    }

    private static void NormalizeSettings(Settings s)
    {
        s.CentralBandStart = Clamp(s.CentralBandStart, 0.0, 1.0);
        s.CentralBandEnd = Clamp(s.CentralBandEnd, 0.0, 1.0);

        if (s.CentralBandEnd <= s.CentralBandStart)
        {
            s.CentralBandStart = 0.35;
            s.CentralBandEnd = 0.65;
        }

        s.AdaptiveBlockSize = FixOdd(Math.Max(9, s.AdaptiveBlockSize));
        s.SmoothWindowPx = FixOdd(Math.Max(5, s.SmoothWindowPx));

        s.CloseKernelWidthFrac = Clamp(s.CloseKernelWidthFrac, 0.005, 0.12);
        s.CloseKernelHeightPx = Clamp(s.CloseKernelHeightPx, 1, 25);

        s.WeightProjection = Math.Max(0, s.WeightProjection);
        s.WeightLab = Math.Max(0, s.WeightLab);
        double sum = s.WeightProjection + s.WeightLab;
        if (sum <= 1e-9) { s.WeightProjection = 1; s.WeightLab = 0; }
        else { s.WeightProjection /= sum; s.WeightLab /= sum; }
    }

    private static int FixOdd(int v) => (v % 2 == 0) ? v + 1 : v;

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

    private static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

    private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    private static double Median(double[] arr)
    {
        if (arr.Length == 0) return 0;
        var tmp = (double[])arr.Clone();
        Array.Sort(tmp);
        int mid = tmp.Length / 2;
        return (tmp.Length % 2 == 1) ? tmp[mid] : 0.5 * (tmp[mid - 1] + tmp[mid]);
    }
}
}
