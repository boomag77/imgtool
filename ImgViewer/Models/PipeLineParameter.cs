using System.ComponentModel;
using System.Runtime.CompilerServices;


namespace ImgViewer.Models
{
    public class PipeLineParameter : INotifyPropertyChanged
    {
        private readonly double _min;
        private readonly double _max;
        private double _value;

        private bool _isVisible = true;
        private bool _isBool = false;
        private bool _boolValue = false;

        private IList<string>? _options;
        private int _selectedIndex;

        public PipeLineParameter(string label, string key, double value, double min, double max, double step)
        {
            Label = label;
            Key = key;
            _min = min;
            _max = max;
            Step = step <= 0 ? 1 : step;
            _value = Clamp(value);
            _options = null;
            _selectedIndex = -1;
        }

        // constructor for ComboBox parameter
        public PipeLineParameter(string label, string key, IEnumerable<string> options, int selectedIndex = 0)
        {
            Label = label;
            Key = key;
            Step = 1;
            _min = double.NaN;
            _max = double.NaN;
            _value = double.NaN;

            _options = options?.ToList() ?? new List<string>();
            SelectedIndex = Math.Max(0, Math.Min(_options.Count - 1, selectedIndex));
        }

        // constructor for CheckBox
        public PipeLineParameter(string label, string key, bool boolValue)
        {
            Label = label;
            Key = key;
            Step = 1;
            _min = double.NaN;
            _max = double.NaN;
            _value = double.NaN;
            _options = null;
            _selectedIndex = -1;

            _isBool = true;
            _boolValue = boolValue;
        }

        public bool IsBool => _isBool;



        public string Label { get; }

        public string Key { get; }

        public double Step { get; }

        public double Value
        {
            get => _value;
            set
            {
                var clamped = Clamp(value);
                if (!AreClose(_value, clamped))
                {
                    _value = clamped;
                    OnPropertyChanged();
                }
            }
        }

        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (_boolValue != value)
                {
                    _boolValue = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool IsVisible
        {
            get => _isVisible;
            set
            {
                if (_isVisible != value)
                {
                    _isVisible = value;
                    OnPropertyChanged();
                }
            }
        }

        public void Increment()
        {
            Value += Step;
        }

        public void Decrement()
        {
            Value -= Step;
        }

        // --- Combo properties ---
        public IList<string>? Options
        {
            get => _options;
            // rarely changed at runtime; if you set it, update IsCombo
            set
            {
                _options = value;
                OnPropertyChanged(nameof(Options));
                OnPropertyChanged(nameof(IsCombo));
            }
        }

        public int SelectedIndex
        {
            get => _selectedIndex;
            set
            {
                if (_options == null || _options.Count == 0)
                {
                    _selectedIndex = -1;
                }
                else
                {
                    int idx = Math.Max(0, Math.Min(_options.Count - 1, value));
                    if (_selectedIndex != idx)
                    {
                        _selectedIndex = idx;
                        OnPropertyChanged();
                        OnPropertyChanged(nameof(SelectedOption));
                    }
                }
            }
        }

        public string? SelectedOption => Options != null && SelectedIndex >= 0 && SelectedIndex < Options.Count ? Options[SelectedIndex] : null;

        // convenience flag for XAML
        public bool IsCombo => Options != null && Options.Count > 0;

        private double Clamp(double value)
        {
            if (!double.IsNaN(_min))
            {
                value = Math.Max(_min, value);
            }

            if (!double.IsNaN(_max))
            {
                value = Math.Min(_max, value);
            }

            return value;
        }



        private static bool AreClose(double value1, double value2)
        {
            return Math.Abs(value1 - value2) < 0.0001;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
