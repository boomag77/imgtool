using Leadtools;
using Leadtools.Codecs;
using Leadtools.ImageProcessing;
using Leadtools.ImageProcessing.Core;
using Leadtools.Windows.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ImgProcessor.Abstractions;

namespace LeadImgProcessor  
{
    public class LeadImgProcessorFactory : IImageProcessorFactory
    {
        private readonly string _licensePath;
        private readonly string _licenseKey;
        public LeadImgProcessorFactory(string licensePath, string licenseKey)
        {
            _licensePath = licensePath;
            _licenseKey = licenseKey;
        }
        public IImageProcessor CreateProcessor()
        {
            return new LeadImageProcessor(_licensePath, _licenseKey);
        }
    }

    public class LeadImageProcessor : IImageProcessor
    {
        public event Action<Stream> ImageUpdated;
        public event Action<string> ErrorOccured;

        private readonly RasterCodecs _codecs;
        private RasterImage? _currentImage;

        public void SaveImage(string path, Stream stream)
        {
            // TO-DO: implement saving image
        }

        private Stream saveToStream(RasterImage image)
        {
            var stream = new MemoryStream();
            _codecs.Save(image, stream, RasterImageFormat.Png, 24);
            stream.Position = 0;
            return stream;
        }

        private void updateImagePreview()         
        {
            if (_currentImage != null)
            {
                var stream = saveToStream(_currentImage);
                ImageUpdated?.Invoke(stream);
            }
        }

        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            if (_currentImage != null) {
                switch (command)
                {
                    case ProcessorCommands.Binarize:
                        ApplyAutoBinarizeCurrent(); { break; }
                    case ProcessorCommands.Deskew:
                        ApplyDeskewCurrent(); { break; }

                }
                updateImagePreview();
            }
        }


        public void ApplyAutoBinarizeCurrent()
        {
            if (_currentImage != null)
            {
                var command = new AutoBinarizeCommand();
                command.Flags = AutoBinarizeCommandFlags.UsePercentileThreshold | AutoBinarizeCommandFlags.DontUsePreProcessing;
                command.Factor = 7000; // use 20% threshold 
                command.Run(_currentImage);
            }
        }   

        public BitmapSource ApplyDespeckleCurrent()
        {
            if (_currentImage != null)
            {
                var command = new DespeckleCommand();
                command.Run(_currentImage);
            }
            return ToBitmapSource(_currentImage);
        }

        public void ApplyDeskewCurrent()
        {
            if (_currentImage != null)
            {
                var command = new DeskewCommand();
                command.FillColor = new RasterColor(255, 255, 255); // white background
                command.Run(_currentImage);
            }
        }


        public BitmapSource ApplyBorderRemoveCurrent()
        {
            if (_currentImage != null)
            {
                var command = new BorderRemoveCommand();
                command.Border = BorderRemoveBorderFlags.All;
                command.Flags = BorderRemoveCommandFlags.UseVariance;
                command.Percent = 20;
                command.Variance = 3;
                command.WhiteNoiseLength = 9;
                command.Run(_currentImage);
            }
            return ToBitmapSource(_currentImage);
        }

        public BitmapSource ApplyAutoCropRectangleCurrent()
        {
            if (_currentImage != null)
            {
                var autoCrop = new AutoCropRectangleCommand();
                autoCrop.Run(_currentImage);

                // Check if a valid rectangle was found
                if (!autoCrop.Rectangle.IsEmpty)
                {
                    // Crop the image using the found rectangle
                    var crop = new CropCommand
                    {
                        Rectangle = autoCrop.Rectangle
                    };
                    crop.Run(_currentImage);
                }
            }
            return ToBitmapSource(_currentImage);
        }

        public LeadImageProcessor(string licensePath, string key)
        {
            RasterSupport.SetLicense(licensePath, key);
            _codecs = new RasterCodecs();
        }

        public void LoadImage(string path)
        {
            try
            {
                _currentImage = _codecs.Load(path);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка LEADTOOLS при загрузке {path}: {ex.Message}", ex);
            }
            updateImagePreview();
        }

        private BitmapSource ToBitmapSource(RasterImage image)
        {
            using (var stream = new MemoryStream())
            {
                _codecs.Save(image, stream, RasterImageFormat.Png, 24);
                stream.Seek(0, SeekOrigin.Begin);

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = stream;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
        }
    }
}
