using System;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class BatchProgressWindow : Window
    {
        public event Action<string>? CancelRequested;

        public BatchProgressWindow()
        {
            InitializeComponent();
        }

        private void CancelRow_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element &&
                element.DataContext is ImgViewer.Models.BatchTaskItem item)
            {
                CancelRequested?.Invoke(item.Id);
            }
        }

        private void CancelAll_Click(object sender, RoutedEventArgs e)
        {
            CancelRequested?.Invoke(string.Empty);
        }
    }
}
