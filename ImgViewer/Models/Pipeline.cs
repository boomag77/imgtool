using ImgViewer.Interfaces;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace ImgViewer.Models
{


    public class Pipeline
    {



        private sealed class PipelineSaveItem
        {
            public PipelineOperationType Type { get; set; }
            public string? DisplayName { get; set; }
            public Dictionary<string, object?>? Parameters { get; set; }
        }


        private readonly IAppManager _manager;


        private readonly object _operationsLock = new();

        private ObservableCollection<PipelineOperation> _operations = new();

        public ObservableCollection<PipelineOperation> Operations
        {
            get
            {
                lock (_operationsLock)
                    return _operations;
            }
            set
            {
                Debug.WriteLine($"Operations: {_operations.Count}");
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

        public void Add(PipelineOperationType operationType)
        {
            lock (_operationsLock)
            {
                _operations.Add(CreatePipelineOperation(operationType));

            }

        }

        public void Insert(int index, PipelineOperation operation)
        {
            lock (_operationsLock)
            {
                _operations.Insert(index, operation);
            }

        }

        public void Remove(PipelineOperation operation)
        {
            lock (_operationsLock)
                _operations.Remove(operation);
        }

        public bool Contains(PipelineOperation operation)
        {
            lock (_operationsLock)
                return _operations.Contains(operation);
        }

        public int IndexOf(PipelineOperation operation)
        {
            lock (_operationsLock)
                return _operations.IndexOf(operation);
        }

        private void ExecuteManagerCommand(ProcessorCommand command, Dictionary<string, object> parameters)
        {
            _manager.ApplyCommandToProcessingImage(command, parameters);
        }

        public PipelineOperation CreatePipelineOperation(PipelineOperationType type, string? nameSuffix = null)
        {

            PipelineOperation operation;
            string displayName = nameSuffix == null ? type.ToString() : $"{type.ToString()} {nameSuffix}";
            string buttonText = "Preview"; // или возьми из поля/ресурса

            switch (type)
            {
                case PipelineOperationType.Deskew:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.Deskew,
                            ProcessorCommand.Deskew,
                            displayName,
                            buttonText,
                            new[]
                            {
                                new PipeLineParameter("Algorithm", "deskewAlgorithm", new [] { "Auto", "ByBorders", "Hough", "Projection", "PCA" }, 0),
                                new PipeLineParameter("cannyTresh1", "cannyTresh1", 50, 10, 250, 1),
                                new PipeLineParameter("cannyTresh2", "cannyTresh2", 150, 10, 250, 1),
                                new PipeLineParameter("Morph kernel", "morphKernel", 5, 1, 10, 1),
                                new PipeLineParameter("Hough min line length", "minLineLength", 200, 0, 20000, 1),
                                new PipeLineParameter("Hough threshold", "houghTreshold", 80, 5, 250, 1)
                            },
                            op => ExecuteManagerCommand(
                                ProcessorCommand.Deskew,
                                op.CreateParameterDictionary())
                        );
                    }

                    break;
                case PipelineOperationType.BordersRemove:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.BordersRemove,
                            ProcessorCommand.BordersRemove,
                            displayName,
                            buttonText,
                            new[]
                            {
                                new PipeLineParameter("Algorithm", "borderRemovalAlgorithm", new [] {"Auto", "By Contrast", "Manual"}, 0),
                                // By contrast
                                new PipeLineParameter("Threshold Frac", "threshFrac", 0.40, 0.05, 1.00, 0.05),
                                new PipeLineParameter("Contrast Threshold", "contrastThr", 50, 1, 255, 1),
                                new PipeLineParameter("Central Sample", "centralSample", 0.10, 0.01, 1.00, 0.01),
                                new PipeLineParameter("Max remove frac", "maxRemoveFrac", 0.45, 0.01, 1.00, 0.01),
                                // Auto
                                new PipeLineParameter("Auto Threshold", "autoThresh", true),
                                new PipeLineParameter("Margin %", "marginPercent", 10, 0, 100, 1),
                                new PipeLineParameter("Shift factor txt/bg", "shiftFactor", 0.25, 0.0, 1.0, 0.01),
                                new PipeLineParameter("Threshold for dark pxls", "darkThreshold", 40, 0, 255, 1),
                                new PipeLineParameter("Background color (RGB)", "bgColor", 0, 0, 255, 1),
                                new PipeLineParameter("Min component area in pxls", "minAreaPx", 2000, 0, 2_000_000, 1),
                                new PipeLineParameter("Span fraction across w/h", "minSpanFraction", 0.6, 0.0, 1.0, 0.01),
                                new PipeLineParameter("Solidity threshold", "solidityThreshold", 0.6, 0.0, 1.0, 0.01),
                                new PipeLineParameter("Penetration depth, relative", "minDepthFraction", 0.20, 0.0, 1.0, 0.01),
                                new PipeLineParameter("Feather (cut margin)", "featherPx", 6, -10, 200, 1),
                                new PipeLineParameter("Use TeleaHybrid", "useTeleaHybrid", true),
                                // Manual
                                new PipeLineParameter("Left", "manualLeft", 0, 0, 10000, 1),
                                new PipeLineParameter("Right", "manualRight", 0, 0, 10000, 1),
                                new PipeLineParameter("Top", "manualTop", 0, 0, 10000, 1),
                                new PipeLineParameter("Bottom", "manualBottom", 0, 0, 10000, 1),
                                new PipeLineParameter("Cut", "cutMethod", false),
                                new PipeLineParameter("Preview cut", "manualCutDebug", false)

                            },
                            operation => ExecuteManagerCommand(ProcessorCommand.BordersRemove, operation.CreateParameterDictionary()));
                    }
                    break;
                case PipelineOperationType.Binarize:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.Binarize,
                            ProcessorCommand.Binarize,
                            displayName,
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
                                new PipeLineParameter("K: ", "sauvolaK", 0.30, 0.01, 1.00, 0.01),
                                new PipeLineParameter("R: ", "sauvolaR", 180.0, 1.0, 255.0, 1.00),
                                new PipeLineParameter("Use CLAHE", "sauvolaUseClahe", true),
                                new PipeLineParameter("CLAHE Clip", "sauvolaClaheClip", 2.0, 0.01, 255.0, 1.0),
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
                            operation => ExecuteManagerCommand(ProcessorCommand.Binarize, operation.CreateParameterDictionary()));
                    }
                    break;
                case PipelineOperationType.PunchHolesRemove:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.PunchHolesRemove,
                            ProcessorCommand.PunchHolesRemove,
                            displayName,
                            buttonText,
                            new[]
                            {
                                new PipeLineParameter("Punch Shape", "punchShape", new [] {"Circle", "Rect", "Both"}, 0),
                                // Circle
                                new PipeLineParameter("Diameter", "diameter",50, 1, 500, 1),
                                new PipeLineParameter("Roundness", "roundness", 0.9, 0.01, 1.00, 0.01),
                                // Rect
                                new PipeLineParameter("Height", "height", 80, 1, 500, 1),
                                new PipeLineParameter("Width", "width", 50, 1, 500, 1),
                                new PipeLineParameter("Fill ratio", "fillRatio", 0.9, 0.01, 1.0, 0.01),
                   
                                // common
                                new PipeLineParameter("Density", "density", 1.00, 0.00, 1.00, 0.05),
                                new PipeLineParameter("Size tolerance", "sizeTolerance", 0.8, 0.0, 2.0, 0.1),
                                new PipeLineParameter("Left Offset", "leftOffset", 100, 0, 1500, 1),
                                new PipeLineParameter("Right Offset", "rightOffset", 100, 0, 1500, 1),
                                new PipeLineParameter("Top Offset", "topOffset", 100, 0, 1500, 1),
                                new PipeLineParameter("Bottom Offset", "bottomOffset", 100, 0, 1500, 1),

                                 // new boolean checkbox parameters:
                                //new PipeLineParameter("Use Gaussian", "useGaussian", false),
                   


                            },
                            operation => ExecuteManagerCommand(ProcessorCommand.PunchHolesRemove, operation.CreateParameterDictionary()));
                    }
                    break;
                case PipelineOperationType.Despeckle:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.Despeckle,
                            ProcessorCommand.Despeckle,
                            displayName,
                            buttonText,
                            new[]
                            {
                                new PipeLineParameter("Small Area Relative", "smallAreaRelative", true),
                                new PipeLineParameter("Small Area Multiplier", "smallAreaMultiplier",0.50, 0.01, 2, 0.01),
                                new PipeLineParameter("Small Area Absolute Px", "smallAreaAbsolutePx", 64, 1, 1000, 1),
                                new PipeLineParameter("Max dot Height Fraction", "maxDotHeightFraction", 0.35, 0.01, 1.00, 0.01),
                                new PipeLineParameter("Proximity Radius Fraction", "proximityRadiusFraction", 0.80, 0.01, 1.00, 0.01),
                                new PipeLineParameter("Squareness Tolerance", "squarenessTolerance", 0.60, 0.00, 1.00, 0.05),
                                new PipeLineParameter("KeepClusters", "keepClusters", true),
                                new PipeLineParameter("UseDilateBeforeCC", "useDilateBeforeCC", false),
                                new PipeLineParameter("Dilate Kernel", "dilateKernel", new [] {"1x3", "3x1", "3x3"}),
                                new PipeLineParameter("Dilate Iterations", "dilateIter", 1, 1, 5, 1),
                                new PipeLineParameter("Size tolerance", "sizeTolerance", 0.4, 0.0, 1.0, 0.1),
                                new PipeLineParameter("Show candidates", "showDespeckleDebug", true)
                            },
                            operation => ExecuteManagerCommand(ProcessorCommand.Despeckle, operation.CreateParameterDictionary()));
                    }
                    break;
                case PipelineOperationType.LinesRemove:
                    {
                        operation = new PipelineOperation(
                            PipelineOperationType.LinesRemove,
                            ProcessorCommand.LinesRemove,
                        displayName,
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
                        operation => ExecuteManagerCommand(ProcessorCommand.LinesRemove, operation.CreateParameterDictionary()));
                    }
                    break;
                case PipelineOperationType.SmartCrop:
                    {
                        operation = new PipelineOperation(
                        PipelineOperationType.SmartCrop,
                        ProcessorCommand.SmartCrop,
                        displayName,
                        buttonText,
                        new[]
                        {
                            new PipeLineParameter("Method", "autoCropMethod", new [] { "U-net", "EAST" }, 0),
                            new PipeLineParameter("Crop level [0..100]", "cropLevel", 62, 0, 100, 1),
                            new PipeLineParameter("Preset", "preset", new [] { "Fast", "Balance", "Quality" }),
                            new PipeLineParameter("EAST input width", "eastInputWidth", 704, 1, 1600, 64),
                            new PipeLineParameter("EAST input height", "eastInputHeight", 704, 1, 1600, 64),
                            new PipeLineParameter("EAST score Threshold", "eastScoreThreshold", 0.45, 0.1, 1.0, 0.0),
                            new PipeLineParameter("EAST NMS Threshold", "eastNmsThreshold", 0.45, 0.1, 0.7, 0.05),
                            new PipeLineParameter("TESSERACT min confidence", "tesseractMinConfidence", 50, 30, 60, 1),
                            new PipeLineParameter("Padding Px", "paddingPx", 20, 20, 160, 5),
                            new PipeLineParameter("Downscale max width", "downscaleMaxWidth", 1600, -1, 2400, 100),
                            new PipeLineParameter("East debug", "eastDebug", true)
                        },
                        operation => ExecuteManagerCommand(ProcessorCommand.SmartCrop, operation.CreateParameterDictionary()));
                    }
                    break;
                default:
                    throw new ArgumentOutOfRangeException(
                                     nameof(type),
                                     type,
                                     "Unsupported pipeline operation type in CreateDeskewOperation");
            }



            //op.Command = ProcessorCommand.Deskew;
            return operation;
        }





        public void LoadPipelineFromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return;

            List<PipelineSaveItem>? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<List<PipelineSaveItem>>(json,
                    new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
            }
            catch (JsonException ex)
            {
                Debug.WriteLine($"Failed to parse pipeline json: {ex}");
                return;
            }

            if (snapshot == null || snapshot.Count == 0)
                return;

            lock (_operationsLock)
            {
                _operations.Clear();

                foreach (var item in snapshot)
                {
                    // 1. Создаём операцию по enum-типу
                    var op = CreatePipelineOperation(item.Type);

                    // 2. Восстанавливаем значения параметров
                    if (item.Parameters != null)
                    {
                        foreach (var param in op.Parameters)
                        {
                            if (item.Parameters.TryGetValue(param.Key, out var rawValue))
                            {
                                ApplySavedValueToParameter(param, rawValue);
                            }
                        }
                    }

                    _operations.Add(op);
                }
            }
        }


        private static void ApplySavedValueToParameter(PipeLineParameter param, object? rawValue)
        {
            // Основной сценарий: System.Text.Json кладёт в Dictionary<string, object?>
            // значения типа JsonElement
            if (rawValue is JsonElement je)
            {
                // ---- BOOL-параметр (CheckBox) ----
                if (param.IsBool)
                {
                    switch (je.ValueKind)
                    {
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            param.BoolValue = je.GetBoolean();
                            return;

                        case JsonValueKind.Number:
                            // условно: 0 -> false, всё остальное -> true
                            if (je.TryGetInt32(out var i))
                                param.BoolValue = (i != 0);
                            return;

                        case JsonValueKind.String:
                            var sBool = je.GetString();
                            if (bool.TryParse(sBool, out var bParsed))
                                param.BoolValue = bParsed;
                            return;

                        default:
                            return;
                    }
                }

                // ---- COMBO-параметр (Options/SelectedIndex) ----
                if (param.IsCombo)
                {
                    if (param.Options == null || param.Options.Count == 0)
                        return;

                    string? option = null;

                    switch (je.ValueKind)
                    {
                        case JsonValueKind.String:
                            option = je.GetString();
                            break;

                        case JsonValueKind.Number:
                            // на всякий случай поддержим вариант с индексом
                            if (je.TryGetInt32(out var idxNum) &&
                                idxNum >= 0 && idxNum < param.Options.Count)
                            {
                                param.SelectedIndex = idxNum;
                                return;
                            }
                            break;

                        default:
                            return;
                    }

                    if (!string.IsNullOrEmpty(option))
                    {
                        // сначала точное совпадение
                        int idx = param.Options.IndexOf(option);
                        if (idx < 0)
                        {
                            // затем case-insensitive поиск
                            for (int i = 0; i < param.Options.Count; i++)
                            {
                                if (string.Equals(param.Options[i], option, StringComparison.OrdinalIgnoreCase))
                                {
                                    idx = i;
                                    break;
                                }
                            }
                        }

                        if (idx >= 0)
                            param.SelectedIndex = idx;
                    }

                    return;
                }

                // ---- Числовой параметр (Slider / Numeric) ----
                if (je.ValueKind == JsonValueKind.Number)
                {
                    if (je.TryGetDouble(out var d))
                    {
                        param.Value = d; // внутри PipeLineParameter зажмётся Clamp-ом
                    }
                }
                else if (je.ValueKind == JsonValueKind.String)
                {
                    var sNum = je.GetString();
                    if (double.TryParse(sNum, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    {
                        param.Value = d;
                    }
                }

                return;
            }

            // --- fallback: если вдруг rawValue не JsonElement (другой десериализатор/ручная подготовка) ---

            if (param.IsBool)
            {
                if (rawValue is bool b)
                {
                    param.BoolValue = b;
                }
                else if (rawValue is string s && bool.TryParse(s, out var parsed))
                {
                    param.BoolValue = parsed;
                }
                else if (rawValue is IConvertible conv)
                {
                    try
                    {
                        param.BoolValue = conv.ToBoolean(CultureInfo.InvariantCulture);
                    }
                    catch
                    {
                        // игнор, оставляем дефолт
                    }
                }

                return;
            }

            if (param.IsCombo)
            {
                if (param.Options == null || param.Options.Count == 0)
                    return;

                var s = rawValue?.ToString();
                if (string.IsNullOrEmpty(s))
                    return;

                int idx = param.Options.IndexOf(s);
                if (idx < 0)
                {
                    for (int i = 0; i < param.Options.Count; i++)
                    {
                        if (string.Equals(param.Options[i], s, StringComparison.OrdinalIgnoreCase))
                        {
                            idx = i;
                            break;
                        }
                    }
                }

                if (idx >= 0)
                    param.SelectedIndex = idx;

                return;
            }

            // ---- числовой параметр ----
            switch (rawValue)
            {
                case double d:
                    param.Value = d;
                    break;
                case float f:
                    param.Value = f;
                    break;
                case int i:
                    param.Value = i;
                    break;
                case long l:
                    param.Value = l;
                    break;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d2):
                    param.Value = d2;
                    break;
            }
        }



        public string BuildPipelineForSave()
        {
            List<PipelineSaveItem> snapshot;

            lock (_operationsLock)
            {
                if (_operations.Count == 0)
                    return string.Empty;

                snapshot = new List<PipelineSaveItem>(_operations.Count);

                foreach (var op in _operations)
                {
                    var parameters = op.CreateParameterDictionary();

                    snapshot.Add(new PipelineSaveItem
                    {
                        Type = op.Type,                 // берём тип из PipelineOperation.Type
                        DisplayName = op.DisplayName,   // чисто информативно, при загрузке не нужен
                        Parameters = parameters
                    });
                }
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };

            return JsonSerializer.Serialize(snapshot, options);
        }







        public void ResetToDefault()
        {
            InitializeDefault();
        }

        public void InitializeDefault()
        {

            Clear();
            Add(PipelineOperationType.BordersRemove);
            Add(PipelineOperationType.Binarize);


        }

    }
}
