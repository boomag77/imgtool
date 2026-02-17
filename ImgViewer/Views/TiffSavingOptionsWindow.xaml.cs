using ImgViewer.Interfaces;
using ImgViewer.Models;
using System.Collections.Frozen;
using System.Windows;

namespace ImgViewer.Views
{
    public partial class TiffSavingOptionsWindow : Window
    {
        private static readonly FrozenSet<string> BatchSaveFormatExtensions =
            new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff" }
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        public TiffCompression SelectedCompression { get; private set; }
        public int SelectedJpegQuality { get; private set; }
        public SubSamplingMode SelectedSubSamplingMode { get; private set; }
        public int SelectedDpi { get; private set; } = 300;
        public BatchSavingFileFormat SelectedBatchSavingFileFormat { get; private set; } = BatchSavingFileFormat.Tiff;
        public JpegSettings SelectedJpegSettings { get; private set; } = new JpegSettings
        {
            Quality = 75,
            SubSampling = SubSamplingMode.SubSampling422
        };

        public TiffSavingOptionsWindow(
            BatchSavingFileFormat defaultBatchSavingFileFormat = BatchSavingFileFormat.Tiff,
            JpegSettings? defaultJpegSettings = null,
            TiffCompression? defaultCompression = null,
            int defaultDpi = 300,
            int defaultJpegQuality = 75,
            SubSamplingMode defaultSubSamplingMode = SubSamplingMode.SubSampling422)
        {
            InitializeComponent();

            var formatItems = new List<string>();
            foreach (var ext in new[] { ".tif", ".jpg", ".png" })
            {
                if (BatchSaveFormatExtensions.Contains(ext))
                    formatItems.Add(ext);
            }
            FileFormatComboBox.ItemsSource = formatItems;
            var normalizedDefaultExt = GetExtensionFromBatchSavingFileFormat(defaultBatchSavingFileFormat);
            FileFormatComboBox.SelectedItem = formatItems.Contains(normalizedDefaultExt, StringComparer.OrdinalIgnoreCase)
                ? normalizedDefaultExt
                : ".tif";

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

            var initialJpegSettings = defaultJpegSettings ?? new JpegSettings { Quality = defaultJpegQuality, SubSampling = defaultSubSamplingMode };
            var clampedQuality = Math.Clamp(initialJpegSettings.Quality <= 0 ? defaultJpegQuality : initialJpegSettings.Quality, 1, 100);
            JpegQualityTextBox.Text = clampedQuality.ToString();
            SelectedDpi = defaultDpi > 0 ? defaultDpi : 300;
            DpiTextBox.Text = SelectedDpi.ToString();
            var initialJpegSubsamplingIndex = subsamplingItems.FindIndex(i => i.Value == initialJpegSettings.SubSampling);
            if (initialJpegSubsamplingIndex >= 0)
                SubsamplingComboBox.SelectedIndex = initialJpegSubsamplingIndex;

            UpdateTiffOptionsVisibility();
            UpdateJpegOptionsVisibility();
        }

        private void FileFormatComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateTiffOptionsVisibility();
            UpdateJpegOptionsVisibility();
        }

        private void CompressionComboBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            UpdateJpegOptionsVisibility();
        }

        private bool IsTiffFormatSelected()
        {
            if (FileFormatComboBox.SelectedItem is not string ext)
                return true;

            return ext.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                   ext.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private bool IsJpegFormatSelected()
        {
            if (FileFormatComboBox.SelectedItem is not string ext)
                return false;

            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateTiffOptionsVisibility()
        {
            TiffOptionsPanel.Visibility = IsTiffFormatSelected() ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateJpegOptionsVisibility()
        {
            var isJpeg = IsJpegFormatSelected() ||
                         (IsTiffFormatSelected() && GetSelectedCompression() == TiffCompression.JPEG);
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
            var selectedExt = NormalizeBatchFormatExtension(FileFormatComboBox.SelectedItem as string);
            SelectedBatchSavingFileFormat = GetBatchSavingFileFormatFromExtension(selectedExt);
            SelectedCompression = GetSelectedCompression();

            if (!int.TryParse(DpiTextBox.Text?.Trim(), out var dpi) || dpi <= 0)
            {
                MessageBox.Show("DPI must be a positive integer.",
                                "Invalid value",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                DpiTextBox.Focus();
                return;
            }
            SelectedDpi = dpi;

            var needsJpegOptions = IsJpegFormatSelected() ||
                                   (IsTiffFormatSelected() && SelectedCompression == TiffCompression.JPEG);

            if (needsJpegOptions)
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

            SelectedJpegSettings = new JpegSettings
            {
                Quality = SelectedJpegQuality,
                SubSampling = SelectedSubSamplingMode
            };

            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string NormalizeBatchFormatExtension(string? ext)
        {
            if (string.IsNullOrWhiteSpace(ext))
                return ".tif";

            var normalized = ext.Trim().ToLowerInvariant();
            if (!normalized.StartsWith("."))
                normalized = "." + normalized;

            return normalized switch
            {
                ".jpg" => ".jpg",
                ".png" => ".png",
                ".tif" => ".tif",
                _ => ".tif"
            };
        }

        private static BatchSavingFileFormat GetBatchSavingFileFormatFromExtension(string ext)
        {
            return ext switch
            {
                ".jpg" => BatchSavingFileFormat.Jpeg,
                ".png" => BatchSavingFileFormat.Png,
                _ => BatchSavingFileFormat.Tiff
            };
        }

        private static string GetExtensionFromBatchSavingFileFormat(BatchSavingFileFormat format)
        {
            return format switch
            {
                BatchSavingFileFormat.Jpeg => ".jpg",
                BatchSavingFileFormat.Png => ".png",
                _ => ".tif"
            };
        }
    }
}
