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
        Horizontal
    }

    internal class MainViewModel : IViewModel, INotifyPropertyChanged
    {
        private readonly IAppManager _manager;
        private ImageSource? _originalImage;
        private ImageSource? _imageOnPreview;
        private string? _imageOnPreviewPath;
        private string _tiffCompressionLabel;

        private bool _isSelectionAvailable = true;

        private bool _originalImageIsExpanded = true;

        //private string? _lastOpenedFolder;
        private int _progress;
        private string _status = "Ready";

        private CancellationTokenSource? _cts;
        private PreviewSplitMode _previewSplitMode = PreviewSplitMode.Single;

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

        public bool IsDefaultPreview => _previewSplitMode == PreviewSplitMode.Single;

        public bool IsVerticalSplitPreview
        {
            get => _previewSplitMode == PreviewSplitMode.Vertical;
            set
            {
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
                var newMode = value
                    ? PreviewSplitMode.Horizontal
                    : (_previewSplitMode == PreviewSplitMode.Horizontal ? PreviewSplitMode.Single : _previewSplitMode);
                UpdatePreviewSplitMode(newMode);
            }
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

            _previewSplitMode = newMode;
            OnPropertyChanged(nameof(IsVerticalSplitPreview));
            OnPropertyChanged(nameof(IsHorizontalSplitPreview));
            OnPropertyChanged(nameof(IsDefaultPreview));
        }


    }
}
