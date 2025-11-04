using ImgViewer.Interfaces;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;


namespace ImgViewer.Models
{
    internal class MainViewModel : IViewModel, INotifyPropertyChanged
    {
        private ImageSource? _imageOnPreview;
        private string? _imageOnPreviewPath;
        private string? _lastOpenedFolder;
        private int _progress;
        private string _status = "Ready";

        private CancellationTokenSource? _cts;


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

        public ImageSource? ImageOnPreview
        {
            get => _imageOnPreview;
            set
            {
                if (_imageOnPreview == value) return;
                _imageOnPreview = value;
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

        public string? LastOpenedFolder
        {
            get => _lastOpenedFolder;
            set
            {
                if (_lastOpenedFolder == value) return;
                _lastOpenedFolder = value;
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel()
        {
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }



    }
}
