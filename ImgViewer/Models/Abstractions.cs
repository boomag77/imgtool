using ImgViewer.Interfaces;

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

    public class PipeLineStep
    {
        private readonly ProcessorCommand type;

    }

    public class Pipeline
    {
        private PipeLineStep[] _operations;

    }


    enum DeskewAlgorithm
    {
        Auto,
        ByBorders,
        Hough,
        Projection,
        PCA

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


