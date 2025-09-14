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

namespace LeadImgProcessor  
{
    public class LeadImageProcessor
    {

       // public RasterImage CurrentImage => _currentImage;

        private readonly RasterCodecs _codecs;
        private RasterImage? _currentImage;

        public BitmapSource ApplyDeskewCurrent()
        {
            if (_currentImage != null)
            {
                var command = new DeskewCommand();
                command.Run(_currentImage);
            }
            return ToBitmapSource(_currentImage);
        }

        public LeadImageProcessor(string licensePath, string key)
        {
            RasterSupport.SetLicense(licensePath, key);
            _codecs = new RasterCodecs();
        }

        public BitmapSource LoadImage(string path)
        {
            try
            {
                _currentImage = _codecs.Load(path);
                return ToBitmapSource(_currentImage);
            }
            catch (Exception ex)
            {
                throw new Exception($"Ошибка LEADTOOLS при загрузке {path}: {ex.Message}", ex);
            }
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

        public void ApplyDeskew(RasterImage image)
        {
            var command = new DeskewCommand();
            command.Run(image);
        }
    }
}
