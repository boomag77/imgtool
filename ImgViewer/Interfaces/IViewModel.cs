using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IViewModel
    {

        public string TiffCompressionLabel { get; set; }
        public ImageSource? ImageOnPreview { get; set; }
        public ImageSource? OriginalImage {  get; set; }
        public string Status { get; set; }
        public string? CurrentImagePath { get; set; }
        //public string? LastOpenedFolder { get; set; }
    }
}
