namespace ImgViewer.Models
{
    public class DespeckleSettings
    {

        public bool SmallAreaRelative = true;
        public double SmallAreaMultiplier = 0.25;
        public int SmallAreaAbsolutePx = 64;
        public double MaxDotHeightFraction = 0.35;
        public double ProximityRadiusFraction = 0.8;
        public double SquarenessTolerance = 0.6;
        public bool KeepClusters = true;
        public bool UseDilateBeforeCC = true;
        public string DilateKernel = "1x3"; // "1x3", "3x1" or "3x3"
        public int DilateIter = 1;
        public bool EnableDustRemoval = false;
        public int DustMedianKsize = 3; // odd
        public int DustOpenKernel = 3;
        public int DustOpenIter = 1;
        public bool EnableDustShapeFilter = false;
        public double DustMinSolidity = 0.6;
        public double DustMaxAspectRatio = 3.0;
        public bool ShowDespeckleDebug = false;
    }

}
