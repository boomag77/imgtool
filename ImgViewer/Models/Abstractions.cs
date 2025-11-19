using ImgViewer.Interfaces;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Web.UI.WebControls;

namespace ImgViewer.Models
{



    public class SourceImageFolder
    {
        public string Path { get; set; }
        public string ParentPath { get; set; }
        public string[] Files { get; set; }
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

    public enum OperationParameterDataType
    {
        Bool,
        Int,
        Double,
        String
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
        Rect
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
    }

    public class DeskewOperation
    {
        OperationType _type = OperationType.Deskew;
        DeskewAlgorithm _algo;

    }



    public class PipeLineStep
    {
        private readonly ProcessorCommand type;

    }

    public class Pipeline
    {
        private PipeLineStep[] _operations;


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

    enum BinarizationAlgorithm
    {
        Treshold,
        Sauvola,
        Adaptive
    }

}


