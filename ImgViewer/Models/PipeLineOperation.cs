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

                bordersAlgo.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(PipeLineParameter.SelectedIndex))
                    {
                        // re-evaluate which controls are visible based on chosen algorithm
                        ApplyBorderRemovalVisibility(bordersAlgo.SelectedOption);
                    }
                };
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
                        p.IsVisible = isSauvola;
                        break;
                    case "sauvolaClaheClip":
                        p.IsVisible= isSauvola && useClahe;
                        break;
                    default:
                        p.IsVisible = false;
                        break;
                }
            }
        }

        private void ApplyBorderRemovalVisibility(string? selectedOption)
        {
            var bordersAlgo = _parameters.FirstOrDefault(x => x.Key == "borderRemovalAlgorithm");
            bool isAuto = false;
            bool isManual = false;
            var autoThreshFlag = _parameters.FirstOrDefault(x => x.Key == "autoThresh");
            bool autoThresh = autoThreshFlag != null && autoThreshFlag.IsBool && autoThreshFlag.BoolValue;
            if (bordersAlgo != null)
            {
                var opt = (bordersAlgo.SelectedOption ?? string.Empty).Trim();
                if (!string.IsNullOrEmpty(opt))
                {
                    isAuto = opt.Equals("Auto", StringComparison.OrdinalIgnoreCase);
                    isManual = opt.Equals("Manual", StringComparison.OrdinalIgnoreCase);
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
                        p.IsVisible = bordersAlgo != null
                            ? (bordersAlgo.SelectedOption ?? "").Equals("By Contrast", StringComparison.OrdinalIgnoreCase)
                                || bordersAlgo.SelectedIndex == 1
                            : (selectedOption ?? "").Trim().Equals("By Contrast", StringComparison.OrdinalIgnoreCase);
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
                    case "manualCutDebug":
                        p.IsVisible = isManual;
                        break;
                    default:
                        p.IsVisible = true;
                        break;
                }
            }
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
                if (q.Key == "sauvolaClaheClip")
                    q.IsVisible = isSauvola && useClahe;
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
