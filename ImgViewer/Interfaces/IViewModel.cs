using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IViewModel
    {
        public ImageSource? ImageOnPreview { get; set; }
        public ImageSource? OriginalImage {  get; set; }
        public string Status { get; set; }
        public string? CurrentImagePath { get; set; }
        public string? LastOpenedFolder { get; set; }
    }
}
