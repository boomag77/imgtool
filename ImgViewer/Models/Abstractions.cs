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


