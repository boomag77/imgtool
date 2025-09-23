using ImgProcessor.Abstractions;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Windows.Media.Imaging;

namespace OpenCVProcessor
{
    public class OpenCVImageProcessor : IImageProcessor, IDisposable
    {
        private Mat _currentImage;

        public OpenCVImageProcessor()
        {
            //throw new NotImplementedException();
        }

        public void Dispose()
       {
           //throw new NotImplementedException();
       }

        public event Action<Stream>? ImageUpdated;
        public event Action<string>? ErrorOccured;

        public void Load(string path)
        {
            //throw new NotImplementedException();
            try
            {
                _currentImage = Cv2.ImRead(path, ImreadModes.Color);
                BitmapSource bmpSource = MatToBitmapSource(_currentImage);
                ImageUpdated?.Invoke(BitmapSourceToStream(bmpSource));
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
            }
        }

        private Stream BitmapSourceToStream(BitmapSource bmpSource)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmpSource));
            var ms = new MemoryStream();
            encoder.Save(ms);
            ms.Position = 0;
            return ms;
        }

        public void SaveCurrentImage(string path)
        {
            //throw new NotImplementedException();
        }

        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object> parameters)
        {
            if (_currentImage != null)
            {
                switch (command)
                {
                    case ProcessorCommands.Binarize:
                        Binarize();
                        break;
                    case ProcessorCommands.Deskew:
                        Deskew();
                        break;
                    case ProcessorCommands.BorderRemove:
                        //applyBorderRemoveCurrent();
                        break;
                    case ProcessorCommands.Despeckle:
                        //applyDespeckleCurrent();
                        break;
                    case ProcessorCommands.AutoCropRectangle:
                        //applyAutoCropRectangleCurrent();
                        break;
                    case ProcessorCommands.LineRemove:
                        //ApplyLinesRemoveCurrent();
                        break;
                    case ProcessorCommands.DotsRemove:
                        //RemoveSpecksWithHandler();
                        break;


                }
                updateImagePreview();
            }
        }

        private void updateImagePreview()
        {
            if (_currentImage != null)
            {
                var stream = MatToStream(_currentImage);
                ImageUpdated?.Invoke(stream);
            }
        }

        private Stream MatToStream(Mat mat)
        {
            BitmapSource bmpSource = MatToBitmapSource(mat);
            return BitmapSourceToStream(bmpSource);
        }

        public Stream? LoadAsPNGStream(string path, int targetBPP)
        {
            try
            {
                using var mat = Cv2.ImRead(path, ImreadModes.Color);
                BitmapSource bmpSource = MatToBitmapSource(mat);
                // Сохраняем в MemoryStream как PNG
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bmpSource));
                var ms = new MemoryStream();
                encoder.Save(ms);
                ms.Position = 0;
                return ms;
            }
            catch (Exception ex)
            {
                ErrorOccured?.Invoke($"Error loading image {path}: {ex.Message}");
                return null;
            }
        }

        private BitmapSource MatToBitmapSource(Mat mat)
        {
            // Быстрая конвертация через OpenCvSharp.Extensions:
            // var bmp = mat.ToBitmap(); // создаёт System.Drawing.Bitmap (GDI+) — не идеально для WPF
            // Но лучше: создать WriteableBitmap и скопировать байты
            var wb = new WriteableBitmap(mat.Width, mat.Height, 96, 96, System.Windows.Media.PixelFormats.Bgr24, null);
            int stride = mat.Cols * mat.ElemSize();
            wb.WritePixels(new System.Windows.Int32Rect(0, 0, mat.Width, mat.Height), mat.Data, mat.Rows * stride, stride);
            wb.Freeze();
            return wb;
        }

        public void Binarize(int threshold = 128)
        {
            //using var mat = BitmapSourceConverter.ToMat(src); // конвертация (может быть из OpenCvSharp.Extensions)
            using var gray = new Mat();
            Cv2.CvtColor(_currentImage, gray, ColorConversionCodes.BGR2GRAY);
            Cv2.Threshold(gray, gray, threshold, 255, ThresholdTypes.Binary);
            _currentImage = gray.Clone();
            updateImagePreview();
            //return MatToBitmapSource(gray);
        }

        public void Deskew()
        {
            if (_currentImage == null || _currentImage.Empty())
                return;

            // Клонируем исходник, чтобы безопасно работать с using
            using var src = _currentImage.Clone();

            // 1) серый + бинаризация
            using var gray = new Mat();
            Cv2.CvtColor(src, gray, ColorConversionCodes.BGR2GRAY);
            using var bw = new Mat();
            Cv2.Threshold(gray, bw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);

            // Иногда полезно инвертировать (чтобы объект был белым на чёрном) — зависит от входа
            Cv2.BitwiseNot(bw, bw);

            // 2) закрытие для объединения букв/строк (настроить kernel по результату)
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(15, 3)); // можно регулировать
            Cv2.MorphologyEx(bw, bw, MorphTypes.Close, kernel);

            // 3) найти контуры и выбрать самый большой по площади
            var contours = Cv2.FindContoursAsArray(bw, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (contours == null || contours.Length == 0)
                return; // ничего не найдено — пропускаем

            double imgArea = src.Width * src.Height;
            int bestIdx = -1;
            double bestArea = 0;
            for (int i = 0; i < contours.Length; i++)
            {
                double area = Cv2.ContourArea(contours[i]);
                if (area > bestArea)
                {
                    bestArea = area;
                    bestIdx = i;
                }
            }

            // если самый большой контур слишком мал — скорее всего шум
            if (bestIdx < 0 || bestArea < imgArea * 0.001) // порог 0.1% — можно менять
                return;

            // 4) minAreaRect для найденного большого контура
            var rect = Cv2.MinAreaRect(contours[bestIdx]);

            // Нормализация угла: если прямоугольник "вверх ногами", поднимаем на 90°
            double angle = rect.Angle;
            if (rect.Size.Width < rect.Size.Height)
                angle += 90.0;

            // Обычно нужно повернуть на -angle чтобы "выпрямить" объект (зависит от реализации OpenCV, этот вариант работает в большинстве случаев)
            double rotation = -angle;

            // 5) вычисляем новый размер холста, чтобы не обрезать при повороте
            double rad = rotation * Math.PI / 180.0;
            double absCos = Math.Abs(Math.Cos(rad));
            double absSin = Math.Abs(Math.Sin(rad));
            int newW = (int)Math.Round(src.Width * absCos + src.Height * absSin);
            int newH = (int)Math.Round(src.Width * absSin + src.Height * absCos);

            // 6) получаем матрицу поворота относительно центра исходного изображения
            var center = new Point2f(src.Width / 2f, src.Height / 2f);
            var M = Cv2.GetRotationMatrix2D(center, rotation, 1.0);

            // сдвигаем трансляцию, чтобы центр новой картинки совпал с центром нового холста
            // M[0,2] и M[1,2] — смещения
            M.Set(0, 2, M.Get<double>(0, 2) + (newW / 2.0 - center.X));
            M.Set(1, 2, M.Get<double>(1, 2) + (newH / 2.0 - center.Y));

            using var rotated = new Mat();
            Cv2.WarpAffine(src, rotated, M, new Size(newW, newH), InterpolationFlags.Linear, BorderTypes.Constant, Scalar.All(255));

            // 7) необязательно: обрезать по области содержимого (по большому контуру на повернутом изображении)
            // преобразуем в бинарное и ищем самый большой контур снова
            using var rgray = new Mat();
            Cv2.CvtColor(rotated, rgray, ColorConversionCodes.BGR2GRAY);
            using var rbw = new Mat();
            Cv2.Threshold(rgray, rbw, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.BitwiseNot(rbw, rbw);
            // можно применить небольшое закрытие, чтобы гарантировать связность
            using var smallKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 3));
            Cv2.MorphologyEx(rbw, rbw, MorphTypes.Close, smallKernel);

            var rContours = Cv2.FindContoursAsArray(rbw, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            if (rContours != null && rContours.Length > 0)
            {
                // находим boundingRect самого большого контура
                int ridx = 0;
                double rbest = 0;
                for (int i = 0; i < rContours.Length; i++)
                {
                    double a = Cv2.ContourArea(rContours[i]);
                    if (a > rbest) { rbest = a; ridx = i; }
                }

                var cropRect = Cv2.BoundingRect(rContours[ridx]);

                // добавим небольшой отступ (margin)
                int margin = 10;
                int x = Math.Max(0, cropRect.X - margin);
                int y = Math.Max(0, cropRect.Y - margin);
                int w = Math.Min(rotated.Width - x, cropRect.Width + margin * 2);
                int h = Math.Min(rotated.Height - y, cropRect.Height + margin * 2);

                if (w > 0 && h > 0)
                {
                    var cropped = new Mat(rotated, new Rect(x, y, w, h));
                    _currentImage = cropped.Clone(); // ставим результат (клонируем, чтобы можно было Dispose исходников)
                }
                else
                {
                    _currentImage = rotated.Clone();
                }
            }
            else
            {
                // если не нашли контуры после вращения — просто используем полностью повернутую картинку
                _currentImage = rotated.Clone();
            }

            // обновляем превью (твой метод)
            updateImagePreview();
        }


    }
}
