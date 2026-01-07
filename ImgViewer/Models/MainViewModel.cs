using ImgViewer.Interfaces;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;


namespace ImgViewer.Models
{
    internal enum PreviewSplitMode
    {
        Single,
        Vertical,
        Horizontal,
        Grid4,
        Grid6
    }

    internal class MainViewModel : IViewModel, INotifyPropertyChanged
    {
        private readonly IAppManager _manager;
        private ImageSource? _originalImage;
        private ImageSource? _imageOnPreview;
        private string? _imageOnPreviewPath;
        private string _tiffCompressionLabel;
        private ImageSource? _splitPreviewLeft;
        private ImageSource? _splitPreviewRight;

        private bool _isSelectionAvailable = true;

        private bool _originalImageIsExpanded = true;

        //private string? _lastOpenedFolder;
        private int _progress;
        private string _status = "Ready";

        private CancellationTokenSource? _cts;
        private PreviewSplitMode _previewSplitMode = PreviewSplitMode.Single;
        private PreviewSplitMode _twoPaneOrientation = PreviewSplitMode.Vertical;
        private PreviewSplitMode? _previewSplitModeBeforePageSplit;
        private int _selectedPreviewPaneCount = 2;
        private int _focusedSplitPreviewIndex = -1;

        public bool SavePipelineToMd
        {
            get
            {
                return _manager.IsSavePipelineToMd;
            }
            set
            {
                _manager.IsSavePipelineToMd = value;
            }
        }

        public bool IsSelectionAvaliable
        {
                       get
            {
                return _isSelectionAvailable;
            }
            set
            {
                _isSelectionAvailable = value;
            }
        }

        public bool OriginalImageIsExpanded
        {
            get
            {
                return _originalImageIsExpanded;
            }
            set
            {
                _originalImageIsExpanded = value;
            }
        }


        public string TiffCompressionLabel
        {
            get => _manager.CurrentTiffCompression.ToString();
            set
            {
                _tiffCompressionLabel = value;
                OnPropertyChanged();
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                if (_status == value) return;
                _status = value;
                OnPropertyChanged();
            }
        }

        public int Progress
        {
            get => _progress;
            set
            {
                if (_progress == value) return;
                _progress = value;
                OnPropertyChanged();
            }
        }

        public Visibility IsProcessingImageOnPreview
        {
            get => _imageOnPreview != null ? Visibility.Visible : Visibility.Hidden;
        }

        public ImageSource? ImageOnPreview
        {
            get => _imageOnPreview;
            set
            {
                if (_imageOnPreview == value) return;
                _imageOnPreview = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsProcessingImageOnPreview));
                OnPropertyChanged(nameof(PrimaryPreviewImage));
            }
        }

        public ImageSource? OriginalImage
        {
            get => _originalImage;
            set
            {
                if (_originalImage == value) return;
                _originalImage = value;
                OnPropertyChanged();
            }
        }

        public string? CurrentImagePath
        {
            get => _imageOnPreviewPath;
            set
            {
                _imageOnPreviewPath = value;
                OnPropertyChanged();
            }
        }

        public ImageSource? SplitPreviewLeft
        {
            get => _splitPreviewLeft;
            private set
            {
                if (_splitPreviewLeft == value) return;
                _splitPreviewLeft = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PrimaryPreviewImage));
            }
        }

        public ImageSource? SplitPreviewRight
        {
            get => _splitPreviewRight;
            private set
            {
                if (_splitPreviewRight == value) return;
                _splitPreviewRight = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PrimaryPreviewImage));
            }
        }

        public bool IsPageSplitPreview => _splitPreviewLeft != null && _splitPreviewRight != null;

        public bool IsDefaultPreview => _previewSplitMode == PreviewSplitMode.Single;
        public bool IsQuadSplitPreview => _previewSplitMode == PreviewSplitMode.Grid4;
        public bool IsSixSplitPreview => _previewSplitMode == PreviewSplitMode.Grid6;
        public string PreviewSplitCountLabel => _selectedPreviewPaneCount.ToString();
        public bool AreSplitOrientationButtonsEnabled => _selectedPreviewPaneCount == 2;
        public int FocusedSplitPreviewIndex => _focusedSplitPreviewIndex;
        public bool HasFocusedSplitPreview => _focusedSplitPreviewIndex >= 0;
        public bool ShowPrimaryPreview
        {
            get
            {
                if (IsPageSplitPreview)
                    return HasFocusedSplitPreview;
                return IsDefaultPreview || HasFocusedSplitPreview;
            }
        }

        public ImageSource? PrimaryPreviewImage
        {
            get
            {
                if (IsPageSplitPreview && HasFocusedSplitPreview)
                {
                    return _focusedSplitPreviewIndex switch
                    {
                        0 => _splitPreviewLeft,
                        1 => _splitPreviewRight,
                        _ => _imageOnPreview
                    };
                }

                return _imageOnPreview;
            }
        }

        public bool IsVerticalSplitPreview
        {
            get => _previewSplitMode == PreviewSplitMode.Vertical;
            set
            {
                if (!AreSplitOrientationButtonsEnabled)
                    return;

                var newMode = value
                    ? PreviewSplitMode.Vertical
                    : (_previewSplitMode == PreviewSplitMode.Vertical ? PreviewSplitMode.Single : _previewSplitMode);
                UpdatePreviewSplitMode(newMode);
            }
        }

        public bool IsHorizontalSplitPreview
        {
            get => _previewSplitMode == PreviewSplitMode.Horizontal;
            set
            {
                if (!AreSplitOrientationButtonsEnabled)
                    return;

                var newMode = value
                    ? PreviewSplitMode.Horizontal
                    : (_previewSplitMode == PreviewSplitMode.Horizontal ? PreviewSplitMode.Single : _previewSplitMode);
                UpdatePreviewSplitMode(newMode);
            }
        }

        public void SetPreviewSplitCount(int paneCount)
        {
            if (paneCount != 2 && paneCount != 4 && paneCount != 6)
                return;

            _selectedPreviewPaneCount = paneCount;
            OnPropertyChanged(nameof(PreviewSplitCountLabel));
            OnPropertyChanged(nameof(AreSplitOrientationButtonsEnabled));

            PreviewSplitMode modeToApply = _previewSplitMode;

            switch (paneCount)
            {
                case 2:
                    modeToApply = _twoPaneOrientation;
                    break;
                case 4:
                    modeToApply = PreviewSplitMode.Grid4;
                    break;
                case 6:
                    modeToApply = PreviewSplitMode.Grid6;
                    break;
            }

            UpdatePreviewSplitMode(modeToApply);
        }

        public void SetSplitPreviewImages(ImageSource left, ImageSource right)
        {
            if (left == null || right == null)
                return;

            SplitPreviewLeft = left;
            SplitPreviewRight = right;

            if (!_previewSplitModeBeforePageSplit.HasValue)
                _previewSplitModeBeforePageSplit = _previewSplitMode;

            ClearFocusedSplitPreview();

            var desiredMode = _twoPaneOrientation;
            if (desiredMode != PreviewSplitMode.Vertical && desiredMode != PreviewSplitMode.Horizontal)
                desiredMode = PreviewSplitMode.Vertical;

            UpdatePreviewSplitMode(desiredMode);

            OnPropertyChanged(nameof(IsPageSplitPreview));
            OnPropertyChanged(nameof(ShowPrimaryPreview));
            OnPropertyChanged(nameof(PrimaryPreviewImage));
        }

        public void ClearSplitPreviewImages()
        {
            if (_splitPreviewLeft == null && _splitPreviewRight == null)
                return;

            SplitPreviewLeft = null;
            SplitPreviewRight = null;

            OnPropertyChanged(nameof(IsPageSplitPreview));
            OnPropertyChanged(nameof(ShowPrimaryPreview));
            OnPropertyChanged(nameof(PrimaryPreviewImage));

            if (_previewSplitModeBeforePageSplit.HasValue)
            {
                UpdatePreviewSplitMode(_previewSplitModeBeforePageSplit.Value);
                _previewSplitModeBeforePageSplit = null;
            }
            else
            {
                UpdatePreviewSplitMode(PreviewSplitMode.Single);
            }
        }

        public void ToggleFocusedSplitPreview(int tileIndex)
        {
            if (_previewSplitMode == PreviewSplitMode.Single)
                return;

            if (_focusedSplitPreviewIndex == tileIndex)
            {
                SetFocusedSplitPreviewIndex(-1);
            }
            else
            {
                SetFocusedSplitPreviewIndex(tileIndex);
            }
        }

        public void ClearFocusedSplitPreview()
        {
            SetFocusedSplitPreviewIndex(-1);
        }

        //public string? LastOpenedFolder
        //{
        //    get => _lastOpenedFolder;
        //    set
        //    {
        //        if (_lastOpenedFolder == value) return;
        //        _lastOpenedFolder = value;
        //    }
        //}

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel(IAppManager manager)
        {
            _manager = manager;


        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void UpdatePreviewSplitMode(PreviewSplitMode newMode)
        {
            if (_previewSplitMode == newMode)
                return;

            if (newMode == PreviewSplitMode.Vertical || newMode == PreviewSplitMode.Horizontal)
            {
                _twoPaneOrientation = newMode;
                _selectedPreviewPaneCount = 2;
                OnPropertyChanged(nameof(PreviewSplitCountLabel));
                OnPropertyChanged(nameof(AreSplitOrientationButtonsEnabled));
            }

            if (newMode == PreviewSplitMode.Grid4 && _selectedPreviewPaneCount != 4)
            {
                _selectedPreviewPaneCount = 4;
                OnPropertyChanged(nameof(PreviewSplitCountLabel));
                OnPropertyChanged(nameof(AreSplitOrientationButtonsEnabled));
            }

            if (newMode == PreviewSplitMode.Grid6 && _selectedPreviewPaneCount != 6)
            {
                _selectedPreviewPaneCount = 6;
                OnPropertyChanged(nameof(PreviewSplitCountLabel));
                OnPropertyChanged(nameof(AreSplitOrientationButtonsEnabled));
            }

            if (newMode == PreviewSplitMode.Single)
            {
                ClearFocusedSplitPreview();
            }

            _previewSplitMode = newMode;
            OnPropertyChanged(nameof(IsVerticalSplitPreview));
            OnPropertyChanged(nameof(IsHorizontalSplitPreview));
            OnPropertyChanged(nameof(IsDefaultPreview));
            OnPropertyChanged(nameof(IsQuadSplitPreview));
            OnPropertyChanged(nameof(IsSixSplitPreview));
            OnPropertyChanged(nameof(ShowPrimaryPreview));

            EnsureFocusedSplitPreviewInRange();
        }

        private void SetFocusedSplitPreviewIndex(int index)
        {
            if (_focusedSplitPreviewIndex == index)
                return;

            _focusedSplitPreviewIndex = index;
            OnPropertyChanged(nameof(FocusedSplitPreviewIndex));
            OnPropertyChanged(nameof(HasFocusedSplitPreview));
            OnPropertyChanged(nameof(ShowPrimaryPreview));
            OnPropertyChanged(nameof(PrimaryPreviewImage));
        }

        private void EnsureFocusedSplitPreviewInRange()
        {
            int paneCount = GetPaneCountForMode(_previewSplitMode);
            if (_focusedSplitPreviewIndex >= paneCount)
            {
                ClearFocusedSplitPreview();
            }
        }

        private static int GetPaneCountForMode(PreviewSplitMode mode)
        {
            return mode switch
            {
                PreviewSplitMode.Single => 1,
                PreviewSplitMode.Vertical => 2,
                PreviewSplitMode.Horizontal => 2,
                PreviewSplitMode.Grid4 => 4,
                PreviewSplitMode.Grid6 => 6,
                _ => 1
            };
        }


    }
}
