using ImgViewer.Models;
using Leadtools;
using Leadtools.Codecs;
using Leadtools.ImageProcessing;
using Leadtools.ImageProcessing.Core;
using System.IO;
using System.Text.Json;


namespace LeadImgProcessor
{





    public class LeadImageProcessor : IImageProcessor
    {

        private readonly string _licensePath;
        private readonly string _licenseKey;

        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;

        private readonly RasterCodecs _codecs;
        private RasterImage? _currentImage;

        private class LicenseCredentials
        {
            public string? LicenseFilePath { get; set; }
            public string? LicenseKey { get; set; }
        }


        public LeadImageProcessor()
        {

            var creds = ReadLicenseCreds();
            string licPath = creds.LicenseFilePath;
            string key = creds.LicenseKey;


            RasterSupport.SetLicense(licPath, key);
            _codecs = new RasterCodecs();
        }

        private LicenseCredentials ReadLicenseCreds()
        {
            try
            {
                var secretPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "secret.json");
                var secret = File.ReadAllText(secretPath);
                var creds = JsonSerializer.Deserialize<LicenseCredentials>(secret);
                if (creds == null || string.IsNullOrWhiteSpace(creds.LicenseFilePath) || string.IsNullOrWhiteSpace(creds.LicenseKey))
                    throw new Exception("Invalid license credentials in secret.json");
                creds.LicenseFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, creds.LicenseFilePath);
                return creds;
            }
            catch (Exception ex)
            {
                throw new Exception("Error reading license credentials: " + ex.Message, ex);
            }
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

        private void RemoveSpecksWithHandler()
        {
            int seen = 0, removed = 0;
            // ВАЖНО: 1-bit! Если у вас 24bpp — сначала AutoBinarize + принудительно ColorResolution до 1 bpp.
            var cmd = new DotRemoveCommand
            {
                Flags = DotRemoveCommandFlags.UseSize
                      | DotRemoveCommandFlags.UseDiagonals
                      | DotRemoveCommandFlags.SingleRegion
                      | DotRemoveCommandFlags.UseDpi,
                MinimumDotWidth = 100,
                MinimumDotHeight = 100,
                MaximumDotWidth = 600,
                MaximumDotHeight = 600
            };

            cmd.DotRemove += (sender, e) =>
            {
                // Пример простой эвристики:
                seen++;
                var r = e.BoundingRectangle;
                int area = Math.Max(1, r.Width * r.Height);
                double fill = (double)e.BlackCount / area; // доля "чёрного" в bbox

                // если blob "плотный" (прямоугольное окно/засветка), удаляем;
                // если есть белые "дыры" или blob разреженный — оставляем
                if (fill >= 0.60) { e.Status = RemoveStatus.Remove; removed++; }
                else e.Status = RemoveStatus.NoRemove;
            };

            try
            {
                cmd.Run(_currentImage);
            }
            finally
            {
                //cmd.DotRemove -= handler;
                _currentImage.MakeRegionEmpty(); // важно снять регион
            }

            //Debug.WriteLine($"DotRemove: candidates={seen}, removed={removed}, bpp={_currentImage.BitsPerPixel}");
        }

        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            if (_currentImage != null)
            {
                switch (command)
                {
                    case ProcessorCommands.Binarize:
                        ApplyAutoBinarizeCurrent();
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
                    case ProcessorCommands.LineRemove:
                        ApplyLinesRemoveCurrent();
                        break;
                    case ProcessorCommands.DotsRemove:
                        RemoveSpecksWithHandler();
                        break;


                }
                updateImagePreview();
            }
        }


        public void ApplyCommandsToImageStream(Stream input, Stream output, ProcessorCommands[] commandsQueue, Dictionary<string, object> parameters)
        {
            RasterImage tmp = _currentImage;
            _currentImage = _codecs.Load(input);


            foreach (var command in commandsQueue)
            {
                ApplyCommandToCurrent(command, parameters);
            }

            try
            {
                _currentImage.XResolution = 300;
                _currentImage.YResolution = 300;
                _codecs.Save(_currentImage, output, RasterImageFormat.CcittGroup4, 1);
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Ошибка LEADTOOLS при сохранении в поток: {ex.Message}");
                //throw new Exception($"Ошибка LEADTOOLS при сохранении в поток: {ex.Message}", ex);
            }
            _currentImage = tmp;
        }


        private void ApplyAutoBinarizeCurrent()
        {
            var command = new AutoBinarizeCommand();
            command.Flags = AutoBinarizeCommandFlags.UseUserThreshold | AutoBinarizeCommandFlags.DontUsePreProcessing;
            command.Factor = 230;
            command.Run(_currentImage);

            if (_currentImage.BitsPerPixel != 1)
            {
                new ColorResolutionCommand
                {
                    BitsPerPixel = 1,
                    DitheringMethod = RasterDitheringMethod.None,
                    PaletteFlags = ColorResolutionCommandPaletteFlags.Fixed
                }.Run(_currentImage);
            }
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
            var lower = new RasterColor(0, 0, 0);
            var upper = new RasterColor(100, 100, 100);

            foreach (var s in seeds)
            {
                // Если вдруг в этом углу не чёрный (бывает), пропускаем
                var p = _currentImage.GetPixelColor(s.X, s.Y);
                if (!IsDark(p)) continue;

                _currentImage.MakeRegionEmpty();

                // НИКАКИХ клип-прямоугольников — нам нужен весь связный чёрный фон
                _currentImage.AddMagicWandToRegion(s.X, s.Y, lower, upper, RasterRegionCombineMode.Set);

                // Заливаем найденный регион цветом бумаги
                new FillCommand { Color = paper }.Run(_currentImage);
            }

            _currentImage.MakeRegionEmpty();
            //RemoveSpecksWithHandler();
        }

        private static bool IsDark(RasterColor c)
        {
            // простая яркость по sRGB
            int y = (int)(0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B);
            return y < 96; // порог под себя
        }


        private void ApplyLinesRemoveCurrent()
        {
            if (_currentImage == null)
                return;
            if (_currentImage.BitsPerPixel != 1)
            {
                var cr = new ColorResolutionCommand
                {
                    BitsPerPixel = 1
                };
                cr.Run(_currentImage);
            }
            try
            {
                var command = new LineRemoveCommand();
                command.Run(_currentImage);
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Ошибка LEADTOOLS while lines removal: {ex.Message}");
                //throw new Exception($"Ошибка LEADTOOLS при удалении линий: {ex.Message}", ex);
            }

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
                    _currentImage.XResolution = 300;
                    _currentImage.YResolution = 300;
                    _codecs.Save(_currentImage, path, RasterImageFormat.CcittGroup4, 1);
                }
                catch (Exception ex)
                {
                    ErrorOccured?.Invoke($"Ошибка LEADTOOLS при сохранении {path}: {ex.Message}");
                    //throw new Exception($"Ошибка LEADTOOLS при сохранении {path}: {ex.Message}", ex);
                }
            }
        }

        public Stream? LoadAsPNGStream(string path, int targetBPP = 24)
        {
            try
            {
                using var codecs = new RasterCodecs();
                var img = codecs.Load(path);

                var ms = new MemoryStream();
                codecs.Save(img, ms, RasterImageFormat.Png, targetBPP);
                ms.Position = 0;
                return ms;
            }
            catch (Leadtools.RasterException rex)
            {
                ErrorOccured?.Invoke(
                    $"LEADTOOLS ошибка при загрузке {path}:\n" +
                    $"ErrorId: {rex.Code}\n" +
                    $"Сообщение: {rex.Message}");
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke(
                    $"Общая ошибка при загрузке {path}:\n" +
                    $"Тип: {ex.GetType().Name}\n" +
                    $"Сообщение: {ex.Message}\n" +
                    $"StackTrace: {ex.StackTrace}");


            }
            return null;
        }


        public void Load(string path)
        {
            try
            {

                _currentImage = _codecs.Load(path);
                //        if (_currentImage != null && _currentImage.BitsPerPixel == 1)
                //{
                //            var cr = new ColorResolutionCommand
                //            {
                //                BitsPerPixel = 24,
                //                PaletteFlags = ColorResolutionCommandPaletteFlags.None,
                //                DitheringMethod = RasterDitheringMethod.None
                //            };
                //            cr.Run(_currentImage);
                //        }
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
