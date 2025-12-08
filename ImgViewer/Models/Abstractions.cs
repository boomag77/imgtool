using ImgViewer.Interfaces;
using System.ComponentModel;

namespace ImgViewer.Models
{

    public enum PipelineOperationType
    {
        Deskew,
        BordersRemove,
        Binarize,
        PunchHolesRemove,
        Despeckle,
        LinesRemove,
        SmartCrop
    }

    public enum SourceFileLayout
    {
        Left,
        Right
    }

    public struct SourceImageFile
    {
        public string Path { get; set; }
        public SourceFileLayout? Layout { get; set; }
    }

    public class SourceImageFolder
    {
        public string Path { get; set; }
        public string ParentPath { get; set; }
        public SourceImageFile[] Files { get; set; }
    }

    enum OperationType
    {
        [Description("Deskew")]
        Deskew,
        [Description("Border Removal")]
        BorderRemoval,
        [Description("Binarize")]
        Binarize
    }


    internal struct BinarizeParameters
    {
        public BinarizeMethod Method;
        public double SauvolaK;
        public double SauvolaR;
        public double SauvolaClaheClip;
        public int SauvolaMorphRadius;
        public int SauvolaWindowSize;
        public int Threshold;
        public int? BlockSize;
        public int PencilStrokeBoost;
        public int MeanC;
        public int MorphKernelBinarize;
        public int MorphIterationsBinarize;
        public int MajorityOffset;
        public bool UseGaussian;
        public bool UseMorphology;
        public bool SauvolaUseClahe;
        public int SauvolaClaheGridSize;



        public static BinarizeParameters Default => new BinarizeParameters
        {
            Method = BinarizeMethod.Threshold,
            SauvolaK = 0.34,
            SauvolaR = 180.0,
            SauvolaClaheClip = 12.0,
            SauvolaWindowSize = 25,
            SauvolaMorphRadius = 0,
            Threshold = 128,
            BlockSize = null,
            MeanC = 14,
            MorphKernelBinarize = 3,
            MorphIterationsBinarize = 1,
            MajorityOffset = 20,
            UseGaussian = false,
            UseMorphology = false,
            SauvolaUseClahe = true,
            SauvolaClaheGridSize = 8,
            PencilStrokeBoost = 0
        };


    }

    public enum BinarizeMethod
    {
        Threshold,
        Adaptive,
        Sauvola,
        Majority
    }

    public struct Offsets
    {
        public int left;
        public int right;
        public int top;
        public int bottom;
    }

    public struct DoubleOperationParameter
    {
        public string DisplayName;
        public string Key;
        public double Value;
        public double MinValue;
        public double MaxValue;
        public double Step;
    }
    public struct IntOperationParameter
    {
        public string DisplayName;
        public string Key;
        public double Value;
        public double MinValue;
        public double MaxValue;
        public double Step;
    }

    public enum PunchShape
    {
        Circle,
        Rect,
        Both
    }

    public enum BordersRemovalMethod
    {
        Auto,
        ByContrast
    }

    public class PunchSpec
    {
        public PunchShape Shape { get; set; }
        // For Circle: use Diameter; for Rect: use Size
        public int Diameter { get; set; } = 20; // px, for circle
        public OpenCvSharp.Size RectSize { get; set; } = new OpenCvSharp.Size(20, 20); // for rect
        public int Count { get; set; } = 1; // expected count (best-effort)
                                            // density: 0..1 where 0 -> light hole, 1 -> dark hole (helps validation)
        public double Density { get; set; } = 0.5;
        // if you want extra tolerance in size matching (fraction)
        public double SizeToleranceFraction { get; set; } = 0.4; // ±40%

        public double FillRatio { get; set; } = 0.9;

        public double Roundness { get; set; } = 0.9;
    }

    public class DeskewOperation
    {
        OperationType _type = OperationType.Deskew;
        DeskewAlgorithm _algo;

    }

    public class Process
    {
        public string Name { get; set; }

    }



    public class PipeLineStep
    {
        private readonly ProcessorCommand type;

    }


    public struct Operation
    {
        public string Command { get; set; }
        public Parameter[] Parameters { get; set; }
    }

    public struct Parameter
    {
        public string Name { get; set; }
        public object Value { get; set; } // int, double, bool or string

        public Parameter(string name, object value)
        {
            Name = name;
            Value = value;
        }

        // Удобный помощник для безопасного приведения
        public T As<T>() => (T)Value;
    }



    enum DeskewAlgorithm
    {
        [Description("Auto")]
        Auto,
        [Description("ByBorders")]
        ByBorders,
        [Description("Hough")]
        Hough,
        [Description("Projection")]
        Projection,
        [Description("PCA")]
        PCA
    }

    enum DeskewParameter
    {
        [Description("CannyThresh1")]
        CannyThresh1,
        [Description("CannyThresh2")]
        CannyThresh2,
        [Description("Morph Kernel")]
        MorphKernel,
        [Description("Min Line Length")]
        MinLineLength,
        [Description("Hough Threshold")]
        HoughThreshold

    }

    enum BordersRemovalAlgorithm
    {
        Auto,
        ByContrast
    }

    public enum DeskewMethod
    {

    }




}


