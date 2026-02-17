using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class TiffSavingOptionsWindow : Window
    {
        public TiffCompression SelectedCompression { get; private set; }
        public int SelectedJpegQuality { get; private set; }
        public SubSamplingMode SelectedSubSamplingMode { get; private set; }

        public TiffSavingOptionsWindow(
            TiffCompression? defaultCompression = null,
            int defaultJpegQuality = 75,
            SubSamplingMode defaultSubSamplingMode = SubSamplingMode.SubSampling422)
        {
            InitializeComponent();

            var compressionItems = new List<KeyValuePair<string, TiffCompression>>
            {
                new("CCITT Group 4 (fax, 1-bit)", TiffCompression.CCITTG4),
                new("CCITT Group 3 (fax)", TiffCompression.CCITTG3),
                new("LZW (lossless)", TiffCompression.LZW),
                new("Deflate/ZIP (lossless)", TiffCompression.Deflate),
                new("JPEG (lossy, for color photos)", TiffCompression.JPEG),
                new("PackBits (simple RLE)", TiffCompression.PackBits),
                new("None (uncompressed)", TiffCompression.None)
            };

            var subsamplingItems = new List<KeyValuePair<string, SubSamplingMode>>
            {
                new("No subsampling (4:4:4)", SubSamplingMode.NoSubsampling),
                new("4:2:2", SubSamplingMode.SubSampling422),
                new("4:2:0", SubSamplingMode.SubSampling420)
            };

            CompressionComboBox.ItemsSource = compressionItems;
            SubsamplingComboBox.ItemsSource = subsamplingItems;

            var initialCompression = defaultCompression ?? TiffCompression.CCITTG4;
            var compressionIndex = compressionItems.FindIndex(i => i.Value == initialCompression);
            CompressionComboBox.SelectedIndex = compressionIndex >= 0 ? compressionIndex : 0;

            var initialSubsamplingIndex = subsamplingItems.FindIndex(i => i.Value == defaultSubSamplingMode);
            SubsamplingComboBox.SelectedIndex = initialSubsamplingIndex >= 0 ? initialSubsamplingIndex : 1;

            var clampedQuality = Math.Clamp(defaultJpegQuality, 1, 100);
            JpegQualityTextBox.Text = clampedQuality.ToString();

            UpdateJpegOptionsVisibility();
        }

        private void CompressionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateJpegOptionsVisibility();
        }

        private void UpdateJpegOptionsVisibility()
        {
            var isJpeg = GetSelectedCompression() == TiffCompression.JPEG;
            JpegOptionsPanel.Visibility = isJpeg ? Visibility.Visible : Visibility.Collapsed;
        }

        private TiffCompression GetSelectedCompression()
        {
            if (CompressionComboBox.SelectedItem is KeyValuePair<string, TiffCompression> kv)
                return kv.Value;

            return TiffCompression.CCITTG4;
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            SelectedCompression = GetSelectedCompression();

            if (SelectedCompression == TiffCompression.JPEG)
            {
                if (!int.TryParse(JpegQualityTextBox.Text?.Trim(), out var quality) || quality < 1 || quality > 100)
                {
                    MessageBox.Show("JPEG quality must be an integer from 1 to 100.",
                                    "Invalid value",
                                    MessageBoxButton.OK,
                                    MessageBoxImage.Warning);
                    JpegQualityTextBox.Focus();
                    return;
                }

                SelectedJpegQuality = quality;

                if (SubsamplingComboBox.SelectedItem is KeyValuePair<string, SubSamplingMode> ss)
                    SelectedSubSamplingMode = ss.Value;
                else
                    SelectedSubSamplingMode = SubSamplingMode.SubSampling422;
            }
            else
            {
                SelectedJpegQuality = Math.Clamp(SelectedJpegQuality <= 0 ? 75 : SelectedJpegQuality, 1, 100);
                SelectedSubSamplingMode = SubSamplingMode.SubSampling422;
            }

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
