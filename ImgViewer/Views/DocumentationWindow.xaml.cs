using ImgViewer.Models;
using System.Windows;
using System.Windows.Controls;

namespace ImgViewer.Views
{
    public partial class DocumentationWindow : Window
    {
        public DocumentationWindow()
        {
            InitializeComponent();
            DataContext = Documentation.CreateDefault();
            Loaded += DocumentationWindow_Loaded;
        }

        private void DocumentationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (TocList.Items.Count > 0 && TocList.SelectedIndex == -1)
            {
                TocList.SelectedIndex = 0;
            }
        }

        private void TocList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TocList.SelectedItem is DocSection section)
            {
                ScrollToSection(section);
            }
        }

        private void ScrollToSection(DocSection section)
        {
            var container = ContentItemsControl.ItemContainerGenerator.ContainerFromItem(section) as FrameworkElement;
            container?.BringIntoView();
        }
    }
}
