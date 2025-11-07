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
        Deskew,
        BorderRemoval,
        Binarize
    }

    public class Operation
    {
        private readonly OperationType type;

    }

    public class Pipeline
    {
        private Operation[] _operations;

    }

    public struct DeskewOperation
    {
        OperationType type;

    }

    enum DeskewAlgorithm
    {
        Auto,
        ByBorders,

    }

}


