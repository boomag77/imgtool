using ImgViewer.Interfaces;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;


namespace ImgViewer.Models
{
    internal class MainViewModel : IViewModel, INotifyPropertyChanged
    {
        private readonly AppSettings _appSettings;
        private ImageSource? _originalImage;
        private ImageSource? _imageOnPreview;
        private string? _imageOnPreviewPath;
        private string _tiffCompressionLabel;
        //private string? _lastOpenedFolder;
        private int _progress;
        private string _status = "Ready";

        private CancellationTokenSource? _cts;


        public string TiffCompressionLabel
        {
            get => _appSettings.TiffCompression.ToString();
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

        public MainViewModel(AppSettings appSettings)
        {
            _appSettings = appSettings;


        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }



    }
}
