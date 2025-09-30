using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;
using System.Windows;


namespace ImgViewer.Models
{
    internal class MainViewModel : INotifyPropertyChanged
    {
        private BitmapImage? _imageOnPreview;
        private string? _imageOnPreviewPath;
        private int _progress;
        private string _status = "Ready";
            

        private readonly IFileProcessor _exlorer;
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

        public BitmapImage? ImageOnPreview
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

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainViewModel()
        {
            _cts = new CancellationTokenSource();
            _exlorer = new FileExplorer(_cts.Token);
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public async Task LoadImagAsync(string path)
        {
            Status = $"Loading image preview...";
            var bmp = await Task.Run(() => _exlorer.Load<BitmapImage>(path));
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                ImageOnPreview = bmp;
                CurrentImagePath = path;
                Status = $"Ready";
            });
        }

    }
}
