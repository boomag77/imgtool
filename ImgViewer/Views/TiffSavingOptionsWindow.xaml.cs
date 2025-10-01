using ImgViewer.Interfaces;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class TiffSavingOptionsWindow : Window
    {

        public TiffCompression SelectedCompression { get; private set; }

        public TiffSavingOptionsWindow(TiffCompression? defaultValue = null)
        {
            InitializeComponent();

            var items = new List<KeyValuePair<string, TiffCompression>>
        {
            new KeyValuePair<string, TiffCompression>("CCITT Group 4 (fax, 1-bit)", TiffCompression.CCITTG4),
            new KeyValuePair<string, TiffCompression>("CCITT Group 3 (fax)", TiffCompression.CCITTG3),
            new KeyValuePair<string, TiffCompression>("LZW (lossless)", TiffCompression.LZW),
            new KeyValuePair<string, TiffCompression>("Deflate/ZIP (lossless)", TiffCompression.Deflate),
            new KeyValuePair<string, TiffCompression>("JPEG (lossy, for color photos)", TiffCompression.JPEG),
            new KeyValuePair<string, TiffCompression>("PackBits (simple RLE)", TiffCompression.PackBits),
            new KeyValuePair<string, TiffCompression>("None (uncompressed)", TiffCompression.None)
        };

            CompressionComboBox.ItemsSource = items;

            // select default
            if (defaultValue.HasValue)
            {
                for (int i = 0; i < items.Count; i++)
                    if (items[i].Value.Equals(defaultValue.Value)) { CompressionComboBox.SelectedIndex = i; break; }
            }

            if (CompressionComboBox.SelectedIndex < 0) CompressionComboBox.SelectedIndex = 0;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            var kv = (KeyValuePair<string, TiffCompression>)CompressionComboBox.SelectedItem;
            SelectedCompression = kv.Value;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            this.DialogResult = false;
            this.Close();
        }
    }


}
