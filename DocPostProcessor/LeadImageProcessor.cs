using ImgProcessor.Abstractions;
using Leadtools;
using Leadtools.Codecs;
using Leadtools.ImageProcessing;
using Leadtools.ImageProcessing.Core;
using System.IO;
using System.Windows.Media.Imaging;

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
        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;

        private readonly RasterCodecs _codecs;
        private RasterImage? _currentImage;

        public LeadImageProcessor(string licensePath, string key)
        {
            RasterSupport.SetLicense(licensePath, key);
            _codecs = new RasterCodecs();
        }

        public void SaveImage(string path, Stream stream)
        {
            // TO-DO: implement saving image
        }

        private Stream RasterImageToStream(RasterImage image)
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
                var stream = RasterImageToStream(_currentImage);
                ImageUpdated?.Invoke(stream);
            }
        }

        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            if (_currentImage != null)
            {
                switch (command)
                {
                    case ProcessorCommands.Binarize:
                        applyAutoBinarizeCurrent();
                        break;
                    case ProcessorCommands.Deskew:
                        applyDeskewCurrent();
                        break;
                    case ProcessorCommands.BorderRemove:
                        applyBorderRemoveCurrent();
                        break;
                    case ProcessorCommands.Despeckle:
                        applyDespeckleCurrent();
                        break;
                    case ProcessorCommands.AutoCropRectangle:
                        applyAutoCropRectangleCurrent();
                        break;

                }
                updateImagePreview();
            }
        }


        private void applyAutoBinarizeCurrent()
        {   
            var command = new AutoBinarizeCommand();
            command.Flags = AutoBinarizeCommandFlags.UsePercentileThreshold | AutoBinarizeCommandFlags.DontUsePreProcessing;
            command.Factor = 7000; // use 20% threshold 
            command.Run(_currentImage);
        }

        private void applyDespeckleCurrent()
        {
            var command = new DespeckleCommand();
            command.Run(_currentImage);
        }

        private void applyDeskewCurrent()
        {
            var command = new DeskewCommand();
            //command.FillColor = new RasterColor(255, 255, 255); // white background
            //command.FillColor = RasterColor.FromKnownColor(RasterKnownColor.White);
            command.Run(_currentImage);

            var paper = new RasterColor(255, 255, 255);

            // 3) Точки-«семена» по углам (немного отступаем от границы)
            var seeds = new[]
            {
                new LeadPoint(2, 2),
                new LeadPoint(_currentImage.Width - 3, 2),
                new LeadPoint(2, _currentImage.Height - 3),
                new LeadPoint(_currentImage.Width - 3, _currentImage.Height - 3),
            };

            // 4) Толерантность побольше из-за JPEG-шума
            var tol = new RasterColor(48, 48, 48);

            foreach (var s in seeds)
            {
                // Если вдруг в этом углу не чёрный (бывает), пропускаем
                var p = _currentImage.GetPixelColor(s.X, s.Y);
                if (!IsDark(p)) continue;

                _currentImage.MakeRegionEmpty();

                // НИКАКИХ клип-прямоугольников — нам нужен весь связный чёрный фон
                _currentImage.AddMagicWandToRegion(s.X, s.Y, tol, tol, RasterRegionCombineMode.Set);

                // Заливаем найденный регион цветом бумаги
                new FillCommand { Color = paper }.Run(_currentImage);
            }

            _currentImage.MakeRegionEmpty();

        }

        private static bool IsDark(RasterColor c)
        {
            // простая яркость по sRGB
            int y = (int)(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
            return y < 96; // порог под себя
        }


        private void applyBorderRemoveCurrent()
        {
            var command = new BorderRemoveCommand();
            command.Border = BorderRemoveBorderFlags.All;
            command.Flags = BorderRemoveCommandFlags.UseVariance;
            command.Percent = 20;
            command.Variance = 3;
            command.WhiteNoiseLength = 9;
            command.Run(_currentImage);
        }

        private void applyAutoCropRectangleCurrent()
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

        public void SaveCurrentImage(string path)
        {
            if (_currentImage != null)
            {
                try
                {
                    _codecs.Save(_currentImage, path, RasterImageFormat.Jpeg, 24);
                }
                catch (Exception ex)
                {
                    ErrorOccured?.Invoke($"Ошибка LEADTOOLS при сохранении {path}: {ex.Message}");
                    //throw new Exception($"Ошибка LEADTOOLS при сохранении {path}: {ex.Message}", ex);
                }
            }
        }


        public void LoadImage(string path)
        {
            try
            {
                _currentImage = _codecs.Load(path);
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Ошибка LEADTOOLS при загрузке {path}: {ex.Message}");
                //throw new Exception($"Ошибка LEADTOOLS при загрузке {path}: {ex.Message}", ex);
            }
            updateImagePreview();
        }

        //private BitmapSource ToBitmapSource(RasterImage image)
        //{
        //    using (var stream = new MemoryStream())
        //    {
        //        _codecs.Save(image, stream, RasterImageFormat.Png, 24);
        //        stream.Seek(0, SeekOrigin.Begin);

        //        var bitmap = new BitmapImage();
        //        bitmap.BeginInit();
        //        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        //        bitmap.StreamSource = stream;
        //        bitmap.EndInit();
        //        bitmap.Freeze();
        //        return bitmap;
        //    }
        //}
    }
}
