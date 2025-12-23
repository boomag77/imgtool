using ImgViewer.Interfaces;
using ImgViewer.Views;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using OpenCvSharp;


namespace ImgViewer.Models
{
    public class PipelineOperation : INotifyPropertyChanged
    {
        private readonly ObservableCollection<PipeLineParameter> _parameters;
        private readonly Action<PipelineOperation>? _execute;

        public event Action<PipelineOperation, PipeLineParameter?>? ParameterChanged;

        

        private bool _inPipeline = true;
        private bool _live = false;
        private PipelineOperationType _type;
        private string _displayName;
        private ProcessorCommand _processorCommand;
        private bool _isExpanded = true;

        private bool _isManualBordersRemove;

        private bool _applyToLeftPage = false;
        private bool _applyToRightPage = false;

        public PipelineOperation(PipelineOperationType type, ProcessorCommand procCommand, string displayName, string actionLabel, IEnumerable<PipeLineParameter> parameters, Action<PipelineOperation>? execute = null)
        {
            _displayName = displayName;
            _type = type;
            ActionLabel = actionLabel;
            _parameters = new ObservableCollection<PipeLineParameter>(parameters ?? Enumerable.Empty<PipeLineParameter>());
            _execute = execute;
            _processorCommand = procCommand;


            InitializeParameterVisibilityRules();
            HookParameterChanges();
        }

        public bool ApplyToLeftPage
        {
            get
            {
                return _applyToLeftPage;
            }
            set
            {
                _applyToLeftPage = value;
                Debug.WriteLine($"[PipelineOperation] {DisplayName}: ApplyToLeftPage = {_applyToLeftPage}");
                OnPropertyChanged();
            }
        }

        public bool ApplyToRightPage
        {
            get
            {
                return _applyToRightPage;
            }
            set
            {
                _applyToRightPage = value;
                Debug.WriteLine($"[PipelineOperation] {DisplayName}: ApplyToRightPage = {_applyToRightPage}");
                OnPropertyChanged();
            }
        }

        public bool IsManualBordersRemove
        {
            get
            {
                return _isManualBordersRemove;
            }
            set
            {
                _isManualBordersRemove = value;
                Debug.WriteLine($"[PipelineOperation] {DisplayName}: IsManualBordersRemove = {_isManualBordersRemove}");
                OnPropertyChanged();
            }
        }
        
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded == value) return;
                _isExpanded = value;
                OnPropertyChanged(); // ??? RaisePropertyChanged, ??? ? ???? ???????
            }
        }

        public ProcessorCommand Command
        {
            get
            {
                return _processorCommand;
            }
        }

        public event Action<PipelineOperation>? LiveChanged;

        public string DisplayName
        { get
            {
                return _displayName;
            }
        }



        public PipelineOperationType Type
        {
            get
            {
                return _type;
            }
        }

        public string ActionLabel { get; }

        public ObservableCollection<PipeLineParameter> Parameters => _parameters;

        public bool InPipeline
        {
            get => _inPipeline;
            set
            {
                if (_inPipeline != value)
                {
                    _inPipeline = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool Live
        {
            get => _live;
            set
            {
                if (_live != value)
                {
                    _live = value;
                    OnPropertyChanged();
                    LiveChanged?.Invoke(this); // notify subscribers (optional)
                }
            }
        }

        public string? DocumentationSectionId => Type switch
        {
            PipelineOperationType.Deskew => "deskew",
            PipelineOperationType.BordersRemove => "borders",
            PipelineOperationType.Binarize => "binarize",
            PipelineOperationType.PunchHolesRemove => "punchholes",
            PipelineOperationType.Despeckle => "despeckle",
            PipelineOperationType.LinesRemove => "lines",
            PipelineOperationType.SmartCrop => "smartcrop",
            PipelineOperationType.SplitPage => "pagesplit",
            PipelineOperationType.Enhance => "enhance",
            _ => null
        };

        private void HookParameterChanges()
        {
            foreach (var p in _parameters)
            {
                // attach a lightweight handler to forward notifications
                p.PropertyChanged += (s, e) =>
                {
                    // forward (operation, parameter) to listeners
                    ParameterChanged?.Invoke(this, p);
                    
                };
            }
        }

        public void Execute()
        {
            _execute?.Invoke(this);


        }

        private void InitializeParameterVisibilityRules()
        {
            // Deskew algorithm rules
            var algo = _parameters.FirstOrDefault(p => p.Key == "deskewAlgorithm");
            if (algo != null)
            {
                // apply initial visibility
                ApplyDeskewVisibility(algo.SelectedOption);

                // listen for changes on the combo parameter
                algo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        ApplyDeskewVisibility(algo.SelectedOption);
                    }
                };
            }

            var shape = _parameters.FirstOrDefault(p => p.Key == "punchShape");
            if (shape != null)
            {
                ApplyPunchRemoveVisibility(shape.SelectedOption);
                shape.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        ApplyPunchRemoveVisibility(shape.SelectedOption);
                    }
                };
            }


            // Binarize algorithm rules example
            var binAlgo = _parameters.FirstOrDefault(p => p.Key == "method");
            if (binAlgo != null)
            {
                // initial apply
                ApplyBinarizeVisibility(binAlgo.SelectedOption);

                var claheFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "sauvolaUseClahe");
                if (claheFlagImmediate != null)
                {
                    // set initial visibility of use CLAHE fields based on current checkbox value
                    //ApplySauvolaClaheClipVisibility(claheFlagImmediate.BoolValue);
                    // subscribe once so further toggles update visibility
                    claheFlagImmediate.PropertyChanged -= ClaheFlag_PropertyChanged;
                    claheFlagImmediate.PropertyChanged += ClaheFlag_PropertyChanged;
                }

                // ensure morph fields reflect the current state right away
                var morphFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "useMorphology");
                if (morphFlagImmediate != null)
                {
                    // set initial visibility of morph fields based on current checkbox value
                    //ApplyMorphVisibility(morphFlagImmediate.BoolValue);
                    // subscribe once so further toggles update visibility
                    morphFlagImmediate.PropertyChanged -= MorphFlag_PropertyChanged;
                    morphFlagImmediate.PropertyChanged += MorphFlag_PropertyChanged;
                }

                // Also listen for changes of the algorithm selection and re-evaluate
                binAlgo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        // re-evaluate which controls are visible based on chosen algorithm
                        ApplyBinarizeVisibility(binAlgo.SelectedOption);
                    }
                };
            }

            var autoCropMethod = _parameters.FirstOrDefault(p => p.Key == "autoCropMethod");
            if (autoCropMethod != null)
            {
                // initial apply
                ApplyAutoCropVisibility(autoCropMethod.SelectedOption);
                // Also listen for changes of the algorithm selection and re-evaluate
                autoCropMethod.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        // re-evaluate which controls are visible based on chosen algorithm
                        ApplyAutoCropVisibility(autoCropMethod.SelectedOption);
                    }
                };
            }


            // Border removal algorithm rules
            var bordersAlgo = _parameters.FirstOrDefault(p => p.Key == "borderRemovalAlgorithm");
            if (bordersAlgo != null)
            {
                ApplyBorderRemovalVisibility(bordersAlgo.SelectedOption);

                var autoThreshFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "autoThresh");
                if (autoThreshFlagImmediate != null)
                {
                    //ApplyAutoThreshVisibility(autoThreshFlagImmediate.BoolValue);
                    autoThreshFlagImmediate.PropertyChanged -= AutoThreshFlag_PropertyChanged;
                    autoThreshFlagImmediate.PropertyChanged += AutoThreshFlag_PropertyChanged;
                }

                var autoMaxBorderDepthFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "autoMaxBorderDepthFrac");
                if (autoMaxBorderDepthFlagImmediate != null)
                {
                    //ApplyAutoMaxBorderDepthVisibility(autoMaxBorderDepthFlagImmediate.BoolValue);
                    autoMaxBorderDepthFlagImmediate.PropertyChanged -= AutoMaxBorderDepthFlag_PropertyChanged;
                    autoMaxBorderDepthFlagImmediate.PropertyChanged += AutoMaxBorderDepthFlag_PropertyChanged;
                }

                var applyToLeftPageFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "applyToLeftPage");
                if (applyToLeftPageFlagImmediate != null)
                {
                    // subscribe once so further toggles update visibility
                    applyToLeftPageFlagImmediate.PropertyChanged -= ApplyToPageFlag_PropertyChanged;
                    applyToLeftPageFlagImmediate.PropertyChanged += ApplyToPageFlag_PropertyChanged;
                }
                var applyToRightPageFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "applyToRightPage");
                if (applyToRightPageFlagImmediate != null)
                {
                    // subscribe once so further toggles update visibility
                    applyToRightPageFlagImmediate.PropertyChanged -= ApplyToPageFlag_PropertyChanged;
                    applyToRightPageFlagImmediate.PropertyChanged += ApplyToPageFlag_PropertyChanged;
                }

                bordersAlgo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        // re-evaluate which controls are visible based on chosen algorithm
                        ApplyBorderRemovalVisibility(bordersAlgo.SelectedOption);
                    }
                };
            }

            var despeckleRelativeFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "smallAreaRelative");
            if (despeckleRelativeFlagImmediate != null)
            {
                // ??? Despeckle-????????, ????? ????????? ????????? ???? ???????
                ApplyDespeckleVisibility();

                despeckleRelativeFlagImmediate.PropertyChanged -= DespeckleRelativeFlag_PropertyChanged;
                despeckleRelativeFlagImmediate.PropertyChanged += DespeckleRelativeFlag_PropertyChanged;
            }


            var enhanceMethodParam = _parameters.FirstOrDefault(p => p.Key == "enhanceMethod");
            if (enhanceMethodParam != null)
            {
                ApplyEnhanceVisibility(enhanceMethodParam.SelectedOption);
                enhanceMethodParam.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        ApplyEnhanceVisibility(enhanceMethodParam.SelectedOption);
                    }
                };
            }

            var retinexRobustFlagImmediate = _parameters.FirstOrDefault(p => p.Key == "retinexRobustNormalize");
            if (retinexRobustFlagImmediate != null)
            {
                ApplyRetinexRobustVisibility(retinexRobustFlagImmediate.BoolValue);
                retinexRobustFlagImmediate.PropertyChanged -= RetinexRobustFlag_PropertyChanged;
                retinexRobustFlagImmediate.PropertyChanged += RetinexRobustFlag_PropertyChanged;
            }

        }

        private void ApplyMorphVisibility(bool enabled)
        {
            foreach (var p in _parameters)
            {
                if (p.Key == "morphKernelBinarize" || p.Key == "morphIterationsBinarize")
                    p.IsVisible = enabled;
            }
        }

        private void ApplySauvolaClaheClipVisibility(bool enabled)
        {
            foreach (var p in _parameters)
            {
                if (p.Key == "sauvolaClaheClip")
                    p.IsVisible = enabled;
            }
        }



        private void ApplyAutoThreshVisibility(bool enabled)
        {
            foreach (var p in _parameters)
            {
                if (p.Key == "marginPercent" || p.Key == "shiftFactor")
                    p.IsVisible = enabled;
            }
        }

        private void ApplyDespeckleVisibility()
        {
            var smallAreaRelativeFlag = _parameters.FirstOrDefault(x => x.Key == "smallAreaRelative");
            if (smallAreaRelativeFlag == null)
                return;
            bool useRelative = smallAreaRelativeFlag != null && smallAreaRelativeFlag.IsBool && smallAreaRelativeFlag.BoolValue;
            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "smallAreaRelative":
                        p.IsVisible = true;
                        break;
                    case "smallAreaMultiplier":
                        p.IsVisible = useRelative;
                        break;
                    case "smallAreaAbsolutePx":
                        p.IsVisible = !useRelative;
                        break;
                }
            }
        }

        // auto crop visibility rules
        private void ApplyAutoCropVisibility(string? selectedOption)
        {
            var selected = (selectedOption ?? "U-net").Trim();
            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "autoCropMethod":
                        p.IsVisible = true; // mode selector always visible
                        break;
                    // show these only for Content mode
                    case "cropLevel":
                        p.IsVisible = selected.Equals("U-net", StringComparison.OrdinalIgnoreCase);
                        break;
                    // show these only for Fixed mode
                    case "preset":
                    case "eastInputWidth":
                    case "eastInputHeight":
                    case "eastScoreThreshold":
                    case "eastNmsThreshold":
                    case "tesseractMinConfidence":
                    case "paddingPx":
                    case "downscaleMaxWidth":
                    case "includeHandwritten":
                    case "handwrittenSensitivity":
                    case "includeStamps":
                    case "eastDebug":
                        p.IsVisible = selected.Equals("EAST", StringComparison.OrdinalIgnoreCase);
                        break;
                    default:
                        // keep other parameters visible by default
                        p.IsVisible = true;
                        break;
                }
            }
        }


        private void ApplyDeskewVisibility(string? selectedOption)
        {
            // default to Auto if null
            var selected = (selectedOption ?? "Auto").Trim();

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "deskewAlgorithm":
                        p.IsVisible = true; // algorithm selector always visible
                        break;

                    // show these only for ByBorders
                    case "cannyTresh1":
                    case "cannyTresh2":
                    case "morphKernel":
                        p.IsVisible = selected.Equals("ByBorders", StringComparison.OrdinalIgnoreCase);
                        break;

                    // show these only for Hough
                    case "minLineLength":
                    case "houghTreshold":
                        p.IsVisible = selected.Equals("Hough", StringComparison.OrdinalIgnoreCase);
                        break;

                    default:
                        // keep other parameters visible by default
                        p.IsVisible = true;
                        break;
                }
            }
        }

        private void ApplyPunchRemoveVisibility(string? selectedOption)
        {

            var selected = (selectedOption ?? "Circle").Trim();

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "punchShape":
                        p.IsVisible = true; // algorithm selector always visible
                        break;

                    // show these only for Circle shape
                    case "diameter":
                    case "roundness":
                        p.IsVisible = selected.Equals("Circle", StringComparison.OrdinalIgnoreCase) ||
                                        selected.Equals("Both", StringComparison.OrdinalIgnoreCase);
                        break;

                    // show these only for Rect Shape
                    case "width":
                    case "height":
                    case "fillRatio":
                        p.IsVisible = selected.Equals("Rect", StringComparison.OrdinalIgnoreCase) ||
                                       selected.Equals("Both", StringComparison.OrdinalIgnoreCase);
                        break;

                    default:
                        p.IsVisible = true;
                        break;
                }
            }
        }

        

        private void ApplyBinarizeVisibility(string? selectedOption)
        {
            // find the binarizeAlgorithm parameter (source of truth)
            var binAlgo = _parameters.FirstOrDefault(x => x.Key == "method");

            // robust "isAdaptive" detection:
            // 1) prefer SelectedOption string if available
            // 2) otherwise fallback to SelectedIndex (index 2 means "Adaptive" in your options order)
            bool isAdaptive = false;
            bool isMajority = false;
            bool isThreshold = false;
            bool isSauvola = false;

            if (binAlgo != null)
            {
                var opt = (binAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                {
                    isAdaptive = opt.Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
                    isMajority = opt.Equals("Majority", StringComparison.OrdinalIgnoreCase);
                    isThreshold = opt.Equals("Threshold", StringComparison.OrdinalIgnoreCase);
                    isSauvola = opt.Equals("Sauvola", StringComparison.OrdinalIgnoreCase);
                }
                else
                {
                    isAdaptive = true;
                }
                    
                
            }
            else
            {
                // fallback if binAlgo missing — keep previous behaviour
                isAdaptive = (selectedOption ?? "").Trim().Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
            }



            // find useMorphology flag once
            var morphFlag = _parameters.FirstOrDefault(x => x.Key == "useMorphology");
            bool useMorph = morphFlag != null && morphFlag.IsBool && morphFlag.BoolValue;
            
            var sauvolaClaheFlag = _parameters.FirstOrDefault(x => x.Key == "sauvolaUseClahe");
            bool useClahe = sauvolaClaheFlag != null && sauvolaClaheFlag.IsBool && sauvolaClaheFlag.BoolValue;

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "method":
                        p.IsVisible = true;
                        break;

                    case "threshold":
                        p.IsVisible = isThreshold || isMajority;
                        break;
                    case "majorityOffset":
                        p.IsVisible = isMajority;
                        break;

                    case "blockSize":
                    case "meanC":
                    case "useGaussian":
                    case "useMorphology":
                        p.IsVisible = isAdaptive;
                        break;

                    case "morphKernelBinarize":
                    case "morphIterationsBinarize":
                        // visible only when algorithm == Adaptive AND ApplyMorphology checked
                        p.IsVisible = isAdaptive && useMorph;
                        break;
                    case "sauvolaWindowSize":
                    case "sauvolaK":
                    case "sauvolaR":
                    case "sauvolaUseClahe":
                    case "sauvolaMorphRadius":
                    case "pencilStrokeBoost":
                        p.IsVisible = isSauvola;
                        break;
                    case "sauvolaClaheClip":
                    case "sauvolaClaheGridSize":
                        p.IsVisible= isSauvola && useClahe;
                        break;
                    default:
                        p.IsVisible = false;
                        break;
                }
            }
        }

        private void ApplyEnhanceVisibility(string? selectedOption)
        {
            var mode = GetEnhanceMode(selectedOption);
            var robustFlag = _parameters.FirstOrDefault(p => p.Key == "retinexRobustNormalize");
            bool retinexRobust = robustFlag != null && robustFlag.BoolValue;

            foreach (var parameter in _parameters)
            {
                switch (parameter.Key)
                {
                    case "claheClipLimit":
                    case "claheGridSize":
                        parameter.IsVisible = mode == EnhanceMode.Clahe;
                        break;
                    case "retinexOutputMode":
                    case "retinexUseLabL":
                    case "retinexSigma":
                    case "retinexGammaHigh":
                    case "retinexGammaLow":
                    case "retinexEps":
                    case "retinexRobustNormalize":
                    case "retinexExpClamp":
                        parameter.IsVisible = mode == EnhanceMode.Retinex;
                        break;
                    case "retinexPercentLow":
                    case "retinexPercentHigh":
                    case "retinexHistBins":
                        parameter.IsVisible = mode == EnhanceMode.Retinex && retinexRobust;
                        break;
                    case "levelsBlackPercent":
                    case "levelsWhitePercent":
                    case "levelsGamma":
                    case "levelsTargetWhite":
                        parameter.IsVisible = mode == EnhanceMode.Levels;
                        break;
                    default:
                        break;
                }
            }

            ApplyRetinexRobustVisibility(retinexRobust);
        }

        private void ApplyRetinexRobustVisibility(bool retinexRobustFlag)
        {
            var mode = GetEnhanceMode();
            bool visible = (mode == EnhanceMode.Retinex) && retinexRobustFlag;

            foreach (var parameter in _parameters)
            {
                if (parameter.Key == "retinexPercentLow" || parameter.Key == "retinexPercentHigh" || parameter.Key == "retinexHistBins")
                {
                    parameter.IsVisible = visible;
                }
            }
        }

        private EnhanceMode GetEnhanceMode(string? overrideOption = null)
        {
            var methodParam = _parameters.FirstOrDefault(p => p.Key == "enhanceMethod");
            string method = overrideOption ?? methodParam?.SelectedOption ?? string.Empty;
            method = method?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(method) && methodParam != null)
            {
                return methodParam.SelectedIndex switch
                {
                    0 => EnhanceMode.Clahe,
                    1 => EnhanceMode.Retinex,
                    2 => EnhanceMode.Levels,
                    _ => EnhanceMode.Clahe
                };
            }

            if (method.Equals("Homomorphic Retinex", StringComparison.OrdinalIgnoreCase))
                return EnhanceMode.Retinex;
            if (method.Equals("CLAHE", StringComparison.OrdinalIgnoreCase))
                return EnhanceMode.Clahe;
            if (method.Equals("Levels & Gamma", StringComparison.OrdinalIgnoreCase) || method.Equals("Levels and Gamma", StringComparison.OrdinalIgnoreCase))
                return EnhanceMode.Levels;

            return EnhanceMode.Unknown;
        }

        private enum EnhanceMode
        {
            Unknown,
            Clahe,
            Retinex,
            Levels
        }


        private void ApplyBorderRemovalVisibility(string? selectedOption)
        {
            var bordersAlgo = _parameters.FirstOrDefault(x => x.Key == "borderRemovalAlgorithm");
            bool isAuto = false;
            bool isManual = false;
            bool isIntegral = false;
            bool isByContrast = false;
            var autoThreshFlag = _parameters.FirstOrDefault(x => x.Key == "autoThresh");
            bool autoThresh = autoThreshFlag != null && autoThreshFlag.IsBool && autoThreshFlag.BoolValue;
            var autoMaxBorderDepthFracFlag = _parameters.FirstOrDefault(x => x.Key == "autoMaxBorderDepthFrac");
            bool autoMaxBorderDepthFrac = autoMaxBorderDepthFracFlag != null && autoMaxBorderDepthFracFlag.IsBool && autoMaxBorderDepthFracFlag.BoolValue;
            if (bordersAlgo != null)
            {
                var opt = (bordersAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                {
                    isAuto = opt.Equals("Auto", StringComparison.OrdinalIgnoreCase);
                    isManual = opt.Equals("Manual", StringComparison.OrdinalIgnoreCase);
                    isIntegral = opt.Equals("Integral", StringComparison.OrdinalIgnoreCase);
                    isByContrast = opt.Equals("By Contrast", StringComparison.OrdinalIgnoreCase);
                }
                    
                else
                    isAuto = bordersAlgo.SelectedIndex == 0; // defensive fallback: index 0 = Auto
            }
            else
            {
                // fallback if bordersAlgo missing — keep previous behaviour
                isAuto = (selectedOption ?? "").Trim().Equals("Auto", StringComparison.OrdinalIgnoreCase);
            }

            IsManualBordersRemove = isManual;

            foreach (var p in _parameters)
            {
                switch (p.Key)
                {
                    case "borderRemovalAlgorithm":
                        p.IsVisible = true;
                        break;
                    case "autoThresh":
                    case "useTeleaHybrid":
                        p.IsVisible = isAuto;
                        break;
                    case "marginPercent":
                    case "shiftFactor":
                        p.IsVisible = isAuto && autoThresh;
                        break;
                    case "threshFrac":
                        
                    case "contrastThr":
                    case "centralSample":
                    case "maxRemoveFrac":
                        p.IsVisible = isByContrast;
                        break;
                    case "bgColor":
                    case "darkThreshold":
                        p.IsVisible = isAuto && !autoThresh;
                        break;
                    
                    case "minAreaPx":
                    case "minSpanFraction":
                    case "solidityThreshold":
                    case "minDepthFraction":
                    case "featherPx":
                        p.IsVisible = isAuto;
                        break;
                    case "manualLeft":
                    case "manualTop":
                    case "manualRight":
                    case "manualBottom":
                    case "cutMethod":
                    case "manualCutDebug":
                    case "applyToLeftPage":
                    case "applyToRightPage":
                        p.IsVisible = isManual;
                        break;
                    case "seedContrastStrictness":
                    case "seedBrightnessStrictness":
                    case "textureAllowance":
                    case "scanStepPx":
                    case "inpaintRadius":
                    case "inpaintMode":
                    case "borderColorVariation":
                    case "borderSafetyOffsetPx":
                    case "autoMaxBorderDepthFrac":
                    case "kInterpolation":
                        p.IsVisible = isIntegral;
                        break;
                    case "maxBorderDepthFracLeft":
                    case "maxBorderDepthFracRight":
                    case "maxBorderDepthFracTop":
                    case "maxBorderDepthFracBottom":
                        p.IsVisible = isIntegral && !autoMaxBorderDepthFrac;
                        break;

                    default:
                        p.IsVisible = true;
                        break;
                }
            }
        }

        private void DespeckleRelativeFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            ApplyDespeckleVisibility();
        }

                private void RetinexRobustFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter flag) return;
            ApplyRetinexRobustVisibility(flag.BoolValue);
        }

        private void ClaheFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter sauvolaUseClahe) return;

            // compute current algorithm -> isAdaptive (same logic as ApplyBinarizeVisibility)
            var binAlgo = _parameters.FirstOrDefault(x => x.Key == "method");
            bool isSauvola = false;
            if (binAlgo != null)
            {
                var opt = (binAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isSauvola = opt.Equals("Sauvola", StringComparison.OrdinalIgnoreCase);
                else
                    isSauvola = binAlgo.SelectedIndex == 1;
            }

            bool useClahe = sauvolaUseClahe.BoolValue;

            foreach (var q in _parameters)
            {
                if (q.Key == "sauvolaClaheClip" || q.Key == "sauvolaClaheGridSize")
                    q.IsVisible = isSauvola && useClahe;
                
            }
        }

        // property changed handler for Apply to Left Page / Apply to Right Page checkboxes
        private void ApplyToPageFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            // apply the logic to the PipelineOperation properties
            if (sender is not PipeLineParameter pageFlagParam) return;
            if (pageFlagParam.Key == "applyToLeftPage")
            {
                ApplyToLeftPage = pageFlagParam.BoolValue;
                if (!ApplyToLeftPage && !ApplyToRightPage)
                {
                    var rightParam = _parameters.FirstOrDefault(p => p.Key == "applyToRightPage");
                    if (rightParam != null && rightParam.IsBool && !rightParam.BoolValue)
                        rightParam.BoolValue = true;
                }
            }
            else if (pageFlagParam.Key == "applyToRightPage")
            {

                ApplyToRightPage = pageFlagParam.BoolValue;

                if (!ApplyToLeftPage && !ApplyToRightPage)
                {
                    var leftParam = _parameters.FirstOrDefault(p => p.Key == "applyToLeftPage");
                    if (leftParam != null && leftParam.IsBool && !leftParam.BoolValue)
                    {
                        leftParam.BoolValue = true;
                    }
                }
            }


        }



        private void MorphFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter morphParam) return;

            // compute current algorithm -> isAdaptive (same logic as ApplyBinarizeVisibility)
            var binAlgo = _parameters.FirstOrDefault(x => x.Key == "method");
            bool isAdaptive = false;
            if (binAlgo != null)
            {
                var opt = (binAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isAdaptive = opt.Equals("Adaptive", StringComparison.OrdinalIgnoreCase);
                else
                    isAdaptive = binAlgo.SelectedIndex == 2;
            }

            bool useMorph = morphParam.BoolValue;

            foreach (var q in _parameters)
            {
                if (q.Key == "morphKernelBinarize" || q.Key == "morphIterationsBinarize")
                    q.IsVisible = isAdaptive && useMorph;
            }
        }

        private void AutoThreshFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter autoThreshParam) return;

            // compute current algorithm -> isAdaptive (same logic as ApplyBinarizeVisibility)
            var bordersRemoveAlgo = _parameters.FirstOrDefault(x => x.Key == "borderRemovalAlgorithm");
            bool isAuto = false;
            if (bordersRemoveAlgo != null)
            {
                var opt = (bordersRemoveAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isAuto = opt.Equals("Auto", StringComparison.OrdinalIgnoreCase);
                else
                    isAuto = bordersRemoveAlgo.SelectedIndex == 0;
            }

            bool autoThresh = autoThreshParam.BoolValue;

            foreach (var q in _parameters)
            {
                if (q.Key == "marginPercent" || q.Key == "shiftFactor")
                    q.IsVisible = isAuto && autoThresh;
                if (q.Key == "darkThreshold" || q.Key == "bgColor")
                {
                    q.IsVisible = isAuto && !autoThresh;
                }
            }
        }

        private void AutoMaxBorderDepthFlag_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(PipeLineParameter.BoolValue)) return;
            if (sender is not PipeLineParameter autoMaxBorderDepthFracParam) return;
            // compute current algorithm -> isIntegral (same logic as ApplyBorderRemovalVisibility)
            var bordersRemoveAlgo = _parameters.FirstOrDefault(x => x.Key == "borderRemovalAlgorithm");
            bool isIntegral = false;
            if (bordersRemoveAlgo != null)
            {
                var opt = (bordersRemoveAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                    isIntegral = opt.Equals("Integral", StringComparison.OrdinalIgnoreCase);
                else
                    isIntegral = bordersRemoveAlgo.SelectedIndex == 2;
            }
            bool autoMaxBorderDepthFrac = autoMaxBorderDepthFracParam.BoolValue;
            foreach (var q in _parameters)
            {
                if (q.Key == "maxBorderDepthFracLeft" ||
                    q.Key == "maxBorderDepthFracRight" ||
                    q.Key == "maxBorderDepthFracTop" ||
                    q.Key == "maxBorderDepthFracBottom")
                {
                    q.IsVisible = isIntegral && !autoMaxBorderDepthFrac;
                }
            }
        }


        public Dictionary<string, object> CreateParameterDictionary()
        {
            return _parameters.ToDictionary(
                parameter => parameter.Key,
                parameter => 
                    parameter.IsCombo ? (object?)parameter.SelectedOption ?? string.Empty :
                    parameter.IsBool ? parameter.BoolValue :
                                        parameter.Value
                
            );
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
