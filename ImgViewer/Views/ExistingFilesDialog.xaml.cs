using System.Windows;

namespace ImgViewer.Views
{
    public enum ExistingFilesChoice
    {
        Yes,
        YesToAll,
        No,
        NoToAll,
        Cancel
    }

    public partial class ExistingFilesDialog : Window
    {
        public ExistingFilesChoice Choice { get; private set; } = ExistingFilesChoice.Cancel;

        public ExistingFilesDialog(string message)
        {
            InitializeComponent();
            MessageText.Text = message;
        }

        private void Yes_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFilesChoice.Yes;
            Close();
        }

        private void YesToAll_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFilesChoice.YesToAll;
            Close();
        }

        private void No_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFilesChoice.No;
            Close();
        }

        private void NoToAll_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFilesChoice.NoToAll;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFilesChoice.Cancel;
            Close();
        }
    }
}
