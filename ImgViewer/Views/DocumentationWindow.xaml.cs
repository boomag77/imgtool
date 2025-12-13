using System.Windows;
using ImgViewer.Models;

namespace ImgViewer.Views
{
    public partial class DocumentationWindow : Window
    {
        private readonly Documentation _documentation;

        public DocumentationWindow()
        {
            InitializeComponent();
            _documentation = Documentation.LoadOrCreate();
            DataContext = _documentation;
            Loaded += DocumentationWindow_Loaded;
        }

        private void DocumentationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (TocList.Items.Count > 0 && TocList.SelectedIndex == -1)
            {
                TocList.SelectedIndex = 0;
            }
        }

        private void AddNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is DocSection section)
                _documentation.AddNote(section);
        }

        private void DeleteNoteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                var note = fe.DataContext as DocNote;
                var section = fe.Tag as DocSection;
                _documentation.RemoveNote(section, note);
            }
        }
    }
}
