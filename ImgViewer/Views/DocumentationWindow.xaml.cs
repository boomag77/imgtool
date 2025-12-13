using System;
using System.Linq;
using System.Windows;
using ImgViewer.Models;

namespace ImgViewer.Views
{
    public partial class DocumentationWindow : Window
    {
        private readonly Documentation _documentation;
        private string? _pendingSectionId;

        public DocumentationWindow()
        {
            InitializeComponent();
            _documentation = Documentation.LoadOrCreate();
            DataContext = _documentation;
            Loaded += DocumentationWindow_Loaded;
        }

        private void DocumentationWindow_Loaded(object sender, RoutedEventArgs e)
        {
            NavigateToRequestedSection();
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

        public void ShowSection(string? sectionId)
        {
            _pendingSectionId = sectionId;
            if (IsLoaded)
                NavigateToRequestedSection();
        }

        private void NavigateToRequestedSection()
        {
            DocSection? section = null;
            if (!string.IsNullOrWhiteSpace(_pendingSectionId))
            {
                section = _documentation.Sections
                    .FirstOrDefault(s => string.Equals(s.Id, _pendingSectionId, StringComparison.OrdinalIgnoreCase))
                    ?? _documentation.Sections
                        .FirstOrDefault(s => string.Equals(s.Title, _pendingSectionId, StringComparison.OrdinalIgnoreCase));
            }

            if (section == null && _documentation.Sections.Count > 0)
                section = _documentation.Sections[0];

            if (section != null)
            {
                TocList.SelectedItem = section;
                TocList.ScrollIntoView(section);
            }

            _pendingSectionId = null;
        }
    }
}
