using System.IO;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class ResizeProgressWindow : Window
    {
        public ResizeProgressWindow()
        {
            InitializeComponent();
        }

        public void UpdateProgress(int processed, int total, string? currentFile)
        {
            int safeTotal = total <= 0 ? 1 : total;
            int percent = (int)Math.Round(processed * 100.0 / safeTotal);
            percent = Math.Max(0, Math.Min(100, percent));

            MainProgressBar.Value = percent;
            StatusTextBlock.Text = $"Processed {processed} of {total}";
            CurrentFileTextBlock.Text = string.IsNullOrWhiteSpace(currentFile)
                ? string.Empty
                : $"Current: {Path.GetFileName(currentFile)}";
        }
    }
}
