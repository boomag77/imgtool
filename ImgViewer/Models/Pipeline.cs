using ImgViewer.Interfaces;
using ImgViewer.Views;
using System.Collections.ObjectModel;

namespace ImgViewer.Models
{
    public class Pipeline
    {

        private readonly IAppManager _manager;
        private readonly object _operationsLock = new();

        private ObservableCollection<PipeLineOperation> _operations = new();

        public ObservableCollection<PipeLineOperation> Operations
        {
            get
            {
                lock (_operationsLock)
                    return _operations;
            }
        }

        public int Count
        {
            get
            {
                lock (_operationsLock)
                    return _operations.Count;
            }
        }

        public Pipeline(IAppManager manager)
        {
            _manager = manager;
            InitializeDefault();
        }

        public void Clear()
        {
            lock (_operationsLock)
                _operations.Clear();
        }

        public void Add(PipeLineOperation operation)
        {   
            lock (_operationsLock)
                _operations.Add(operation);
        }

        public void Insert(int index, PipeLineOperation operation)
        {
            lock (_operationsLock)
                _operations.Insert(index, operation);
        }

        public void Remove(PipeLineOperation operation)
        {
            lock (_operationsLock)
                _operations.Remove(operation);
        }

        public bool Contains(PipeLineOperation operation)
        {
            lock (_operationsLock)
                return _operations.Contains(operation);
        }

        public int IndexOf(PipeLineOperation operation)
        {
            lock (_operationsLock)
                return _operations.IndexOf(operation);
        }

        private void ExecuteManagerCommand(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            _manager.ApplyCommandToProcessingImage(command, parameters);
        }

        //public void SetOperationsToPipeline(List<Operation> ops)
        //{
        //    _operations.Clear();

        //    if (ops == null)
        //    {
        //        InitializeDefault();
        //        return;
        //    }

        //    foreach (var op in ops)
        //    {
        //        // Определяем заголовок и режим (Preview / Run) — можно настроить по команде
        //        string displayName = op.Command;
        //        string buttonText = "Preview";
        //        var parameters = new List<PipeLineParameter>();

        //        foreach (var p in op.Parameters)
        //        {
        //            // p.Name - ключ в JSON; p.Value - object (int/double/bool/string)
        //            var plParam = CreatePipeLineParameterFromParsed(p.Name, p.Value);
        //            if (plParam != null) parameters.Add(plParam);
        //        }

        //        // Лямбда-обработчик: переводит PipeLineOperation в запуск существующего менеджера
        //        Action<MainWindow, PipeLineOperation> handler = (window, operation) =>
        //        {
        //            // предполагается, что CreateParameterDictionary существует у PipeLineOperation
        //            window.ExecuteManagerCommand(MapToProcessorCommand(op.Command), operation.CreateParameterDictionary());
        //        };

        //        var plo = new PipeLineOperation(displayName, buttonText, parameters.ToArray(), handler)
        //        {
        //            Command = MapToProcessorCommand(op.Command)
        //        };
        //        _pipeline.Add(plo);
        //    }
        //}

        //private PipeLineParameter? CreatePipeLineParameterFromParsed(string key, object? value)
        //{
        //    // Уберём лишние пробелы и приведём к lower для ключей сопоставления
        //    string lk = key.Trim();

        //    // Специфичные опции для известных ключей (воспользуйтесь теми массивами, что выше)
        //    if (string.Equals(lk, "deskewAlgorithm", StringComparison.OrdinalIgnoreCase))
        //        return new PipeLineParameter("Algorithm", "deskewAlgorithm", DeskewAlgorithmOptions, Array.IndexOf(DeskewAlgorithmOptions, (value?.ToString() ?? "Auto")));

        //    if (string.Equals(lk, "borderRemovalAlgorithm", StringComparison.OrdinalIgnoreCase))
        //        return new PipeLineParameter("Algorithm", "borderRemovalAlgorithm", BorderRemovalOptions, Array.IndexOf(BorderRemovalOptions, (value?.ToString() ?? "Auto")));

        //    if (string.Equals(lk, "binarizeAlgorithm", StringComparison.OrdinalIgnoreCase))
        //        return new PipeLineParameter("Algorithm", "binarizeAlgorithm", BinarizeAlgorithmOptions, Array.IndexOf(BinarizeAlgorithmOptions, (value?.ToString() ?? "Treshold")));

        //    // Булевы параметры
        //    if (value is bool bv)
        //    {
        //        return new PipeLineParameter(PrettyLabelFromKey(lk), lk, bv);
        //    }

        //    // Целые числа
        //    if (value is int iv)
        //    {
        //        // Подбор sensible bounds по имени параметра (если нужны особые min/max для некоторых ключей)
        //        if (string.Equals(lk, "BinarizeTreshold", StringComparison.OrdinalIgnoreCase) ||
        //            lk.IndexOf("Threshold", StringComparison.OrdinalIgnoreCase) >= 0 ||
        //            lk.IndexOf("Tresh", StringComparison.OrdinalIgnoreCase) >= 0)
        //        {
        //            return new PipeLineParameter(PrettyLabelFromKey(lk), lk, iv, 0, 255, 1);
        //        }

        //        if (string.Equals(lk, "bgColor", StringComparison.OrdinalIgnoreCase))
        //            return new PipeLineParameter(PrettyLabelFromKey(lk), lk, iv, 0, 255, 1);

        //        if (string.Equals(lk, "minAreaPx", StringComparison.OrdinalIgnoreCase))
        //            return new PipeLineParameter(PrettyLabelFromKey(lk), lk, iv, 100, 2_000_000, 1);

        //        // default для int
        //        return new PipeLineParameter(PrettyLabelFromKey(lk), lk, iv, Math.Max(0, iv - 100), iv + Math.Max(100, iv), 1);
        //    }

        //    // Вещественные числа (double)
        //    if (value is double dv)
        //    {
        //        // если значение явно в диапазоне [0..1], подставим такие границы
        //        if (dv >= 0.0 && dv <= 1.0)
        //            return new PipeLineParameter(PrettyLabelFromKey(lk), lk, dv, 0.0, 1.0, 0.01);

        //        // стандартный fallback: min=0, max=dv*10 (чтобы можно было настраивать)
        //        double max = Math.Max(1.0, dv * 10.0);
        //        double step = dv < 1.0 ? 0.01 : 1.0;
        //        return new PipeLineParameter(PrettyLabelFromKey(lk), lk, dv, 0.0, max, step);
        //    }

        //    // Если значение boxed как System.Text.Json.JsonElement, попробуем обработать:
        //    if (value is System.Text.Json.JsonElement je)
        //    {
        //        // простая попытка извлечь тип
        //        if (je.ValueKind == System.Text.Json.JsonValueKind.Number)
        //        {
        //            if (je.TryGetInt32(out var jjInt)) return CreatePipeLineParameterFromParsed(lk, jjInt);
        //            if (je.TryGetDouble(out var jjDbl)) return CreatePipeLineParameterFromParsed(lk, jjDbl);
        //        }
        //        if (je.ValueKind == System.Text.Json.JsonValueKind.True || je.ValueKind == System.Text.Json.JsonValueKind.False)
        //            return CreatePipeLineParameterFromParsed(lk, je.GetBoolean());
        //        if (je.ValueKind == System.Text.Json.JsonValueKind.String)
        //            return CreatePipeLineParameterFromParsed(lk, je.GetString());
        //    }

        //    // Строковые значения: создаём текстовый (строковый) параметр, если у вас есть конструктор для строк
        //    if (value is string sv)
        //    {
        //        // Если у вас нет явного string-конструктора — можно создать выбора с одним элементом
        //        // Использую конструктор (label, key, options[], selectedIndex)
        //        return new PipeLineParameter(PrettyLabelFromKey(lk), lk, new[] { sv }, 0);
        //    }

        //    // Null или неизвестный тип — возвращаем null (не будем создавать параметр)
        //    return null;
        //}

        public void ResetToDefault()
        {
            InitializeDefault();
        }

        public void InitializeDefault()
        {

           _operations.Clear();

            string buttonText = "Preview";

            var op1 = new PipeLineOperation(
                "Deskew",
                buttonText,
                new[]
                {
                    new PipeLineParameter("Algorithm", "deskewAlgorithm", new [] {"Auto", "ByBorders", "Hough", "Projection", "PCA" }, 0),
                    new PipeLineParameter("cannyTresh1", "cannyTresh1", 50, 10, 250, 1),
                    new PipeLineParameter("cannyTresh2", "cannyTresh2", 150, 10, 250, 1),
                    new PipeLineParameter("Morph kernel", "morphKernel", 5, 1, 10, 1),
                    new PipeLineParameter("Hough min line length", "minLineLength", 200, 0, 20000, 1),
                    new PipeLineParameter("Hough threshold", "houghTreshold", 80, 5, 250, 1)
                },
                (_, operation) => ExecuteManagerCommand(ProcessorCommand.Deskew, operation.CreateParameterDictionary()));


            op1.Command = ProcessorCommand.Deskew;
            _operations.Add(op1);

            var op2 = new PipeLineOperation(
                "Border Removal",
                buttonText,
                new[]
                {
                    new PipeLineParameter("Algorithm", "borderRemovalAlgorithm", new [] {"Auto", "By Contrast"}, 0),
                    // By contrast
                    new PipeLineParameter("Threshold Frac", "threshFrac", 0.40, 0.05, 1.00, 0.05),
                    new PipeLineParameter("Contrast Threshold", "contrastThr", 50, 1, 250, 1),
                    new PipeLineParameter("Central Sample", "centralSample", 0.10, 0.01, 1.00, 0.01),
                    new PipeLineParameter("Max remove frac", "maxRemoveFrac", 0.45, 0.01, 1.00, 0.01),
                    // Auto
                    new PipeLineParameter("Auto Threshold", "autoThresh", true),
                    new PipeLineParameter("Margin %", "marginPercent", 10, 0, 100, 1),
                    new PipeLineParameter("Shift factor txt/bg", "shiftFactor", 0.25, 0.0, 1.0, 0.01),
                    new PipeLineParameter("Threshold for dark pxls", "darkThreshold", 40, 5, 250, 1),
                    new PipeLineParameter("Background color (RGB)", "bgColor", 0, 0, 255, 1),
                    new PipeLineParameter("Min component area in pxls", "minAreaPx", 2000, 100, 2_000_000, 1),
                    new PipeLineParameter("Span fraction across w/h", "minSpanFraction", 0.6, 0.0, 1.0, 0.01),
                    new PipeLineParameter("Solidity threshold", "solidityThreshold", 0.6, 0.0, 1.0, 0.01),
                    new PipeLineParameter("Penetration depth, relative", "minDepthFraction", 0.05, 0.0, 1.0, 0.01),
                    new PipeLineParameter("Feather (cut margin)", "featherPx", 6, -10, 20, 1),

                },
                (_, operation) => ExecuteManagerCommand(ProcessorCommand.BordersRemove, operation.CreateParameterDictionary()));
            op2.Command = ProcessorCommand.BordersRemove;
            _operations.Add(op2);

            //var op3 = new PipeLineOperation(
            //    "Auto Crop",
            //    buttonText,
            //    new[]
            //    {
            //        new PipeLineParameter("Padding", "CropPadding", 8, 0, 100, 1)
            //    },
            //    (window, operation) => window.ExecuteManagerCommand(ProcessorCommands.AutoCropRectangle, operation.CreateParameterDictionary()));
            //op3.Command = ProcessorCommands.AutoCropRectangle;
            //_pipeLineOperations.Add(op3);

            var op4 = new PipeLineOperation(
                "Binarize",
                buttonText,
                new[]
                {
                    new PipeLineParameter("Method", "method", new [] {"Threshold", "Sauvola", "Adaptive", "Majority"}, 0),
                    // Treshold alg
                    new PipeLineParameter("Threshold", "threshold", 128, 0, 255, 1),
                    // Adaptive alg
                    new PipeLineParameter("BlockSize", "blockSize", 3, 3, 255, 2),
                    new PipeLineParameter("Mean C", "meanC", 14, -50.0, 50.0, 1),

                    // Sauvola
                    new PipeLineParameter("Window size", "sauvolaWindowSize", 25, 1, 500, 1),
                    new PipeLineParameter("K: ", "sauvolaK", 0.34, 0.01, 1.00, 0.01),
                    new PipeLineParameter("R: ", "sauvolaR", 180.0, 1.0, 255.0, 1.00),
                    new PipeLineParameter("Use CLAHE", "sauvolaUseClahe", true),
                    new PipeLineParameter("CLAHE Clip", "sauvolaClaheClip", 12.0, 0.01, 255.0, 1.0),
                    new PipeLineParameter("Morph radius", "sauvolaMorphRadius", 0, 0, 7, 1),
                    // Majority
                    new PipeLineParameter("MajorityOffset", "majorityOffset", 30, -120, 120, 1),

                     // new boolean checkbox parameters:
                    new PipeLineParameter("Use Gaussian", "useGaussian", false),
                    new PipeLineParameter("Apply Morphology", "useMorphology", false),

                    // morphology-specific numeric params (visible only if Apply Morphology == true — see ниже how to hide/show)
                    new PipeLineParameter("Morph kernel", "morphKernelBinarize", 3, 1, 21, 2),
                    new PipeLineParameter("Morph iterations", "morphIterationsBinarize", 1, 0, 5, 1),

                },
                (_, operation) => ExecuteManagerCommand(ProcessorCommand.Binarize, operation.CreateParameterDictionary()));
            op4.Command = ProcessorCommand.Binarize;
            _operations.Add(op4);

            var op5 = new PipeLineOperation(
                "PunchRemoval",
                buttonText,
                new[]
                {
                    new PipeLineParameter("Punch Shape", "punchShape", new [] {"Circle", "Rect"}, 0),
                    // Circle
                    new PipeLineParameter("Diameter", "diameter",50, 1, 500, 1),
                    // Rect
                    new PipeLineParameter("Height", "height", 80, 1, 500, 1),
                    new PipeLineParameter("Width", "width", 50, 1, 100, 1),
                   
                    // common
                    new PipeLineParameter("Density", "density", 1.00, 0.00, 1.00, 0.05),
                    new PipeLineParameter("Size tolerance", "sizeTolerance", 0.8, 0.0, 1.0, 0.1),
                    new PipeLineParameter("Left Offset", "leftOffset", 100, 0, 1500, 1),
                    new PipeLineParameter("Right Offset", "rightOffset", 100, 0, 1500, 1),
                    new PipeLineParameter("Top Offset", "topOffset", 100, 0, 1500, 1),
                    new PipeLineParameter("Bottom Offset", "bottomOffset", 100, 0, 1500, 1),

                     // new boolean checkbox parameters:
                    //new PipeLineParameter("Use Gaussian", "useGaussian", false),
                   


                },
                (_, operation) => ExecuteManagerCommand(ProcessorCommand.PunchHolesRemove, operation.CreateParameterDictionary()));
            op5.Command = ProcessorCommand.PunchHolesRemove;
            _operations.Add(op5);

            var op6 = new PipeLineOperation(
                "Despeckle",
                buttonText,
                new[]
                {
                        new PipeLineParameter("Small Area Relative", "smallAreaRelative", true),
                        new PipeLineParameter("Small Area Multiplier", "smallAreaMultiplier",0.25, 0.01, 2, 0.01),
                        new PipeLineParameter("Small Area Absolute Px", "smallAreaAbsolutePx", 64, 1, 1000, 1),
                        new PipeLineParameter("Max dot Height Fraction", "maxDotHeightFraction", 0.35, 0.01, 1.00, 0.01),
                        new PipeLineParameter("Proximity Radius Fraction", "ProximityRadiusFraction", 0.80, 0.01, 1.00, 0.01),
                        new PipeLineParameter("Squareness Tolerance", "squarenessTolerance", 0.60, 0.00, 1.00, 0.05),
                        new PipeLineParameter("KeepClusters", "keepClusters", true),
                        new PipeLineParameter("UseDilateBeforeCC", "useDilateBeforeCC", true),
                        new PipeLineParameter("Dilate Kernel", "dilateKernel", new [] {"1x3", "3x1", "3x3"}),
                        new PipeLineParameter("Dilate Iterations", "DilateIter", 1, 1, 5, 1),
                        new PipeLineParameter("Size tolerance", "sizeTolerance", 0.4, 0.0, 1.0, 0.1),
                        new PipeLineParameter("Show candidates", "showDespeckleDebug", true)
                },
            (_, operation) => ExecuteManagerCommand(ProcessorCommand.Despeckle, operation.CreateParameterDictionary()));
            op6.Command = ProcessorCommand.Despeckle;
            _operations.Add(op6);

            var op7 = new PipeLineOperation(
                "LinesRemove",
                buttonText,
                new[]
                {
                        new PipeLineParameter("Orientation", "orientation", Enum.GetNames(typeof(LineOrientation)), 1),
                        new PipeLineParameter("Line width (px)", "lineWidthPx", 1, 1, 20, 1),
                        new PipeLineParameter("Min Length Fraction", "minLengthFraction", 0.5, 0.05, 1, 0.01),
                        new PipeLineParameter("Start offset (px)", "offsetStartPx", -1, -1, 500, 1),
                        new PipeLineParameter("Line color (Red)", "lineColorRed", -1, -1, 255, 1),
                        new PipeLineParameter("Line color (Green)", "lineColorGreen", -1, -1, 255, 1),
                        new PipeLineParameter("Line color (Blue)", "lineColorBlue", -1, -1, 255, 1),
                        new PipeLineParameter("Color tolerance", "colorTolerance", 40, 0, 255, 1)
                },
            (_, operation) => ExecuteManagerCommand(ProcessorCommand.LineRemove, operation.CreateParameterDictionary()));
            op7.Command = ProcessorCommand.LineRemove;
            _operations.Add(op7);

        }

    }
}
