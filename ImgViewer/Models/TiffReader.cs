using BitMiracle.LibTiff.Classic;
using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ImgViewer.Models
{
    internal class TiffReader
    {
        private struct TiffImageInfo
        {
            public int Width;
            public int Height;
            public int Compression;
            public int PhotoMetric;
            public int FillOrder;
            public int SamplesPerPixel;
            public byte[] Data;
        }

        private static async Task<TiffImageInfo?> ReadTiff(string filePath)
        {
            using (Tiff tiff = Tiff.Open(filePath, "r"))
            {
                if (tiff == null)
                {
                    Debug.WriteLine("Could not open incoming image");
                    return null;
                }
                int width = tiff.GetField(TiffTag.IMAGEWIDTH)[0].ToInt();
                int height = tiff.GetField(TiffTag.IMAGELENGTH)[0].ToInt();
                int samplesPerPixel = tiff.GetField(TiffTag.SAMPLESPERPIXEL)[0].ToInt();
                int bitsPerSample = tiff.GetField(TiffTag.BITSPERSAMPLE)[0].ToInt();
                int photoMetric = tiff.GetField(TiffTag.PHOTOMETRIC)[0].ToInt();
                FieldValue[]? fillOrderField = tiff.GetField(TiffTag.FILLORDER);
                int fillOrder = fillOrderField != null ? fillOrderField[0].ToInt() : (int)FillOrder.MSB2LSB; // MSB2LSB by default
                int planarConfig = tiff.GetField(TiffTag.PLANARCONFIG)[0].ToInt();
                //int? orientation = tiff.GetField(TiffTag.ORIENTATION)[0].ToInt();
                int rowsPerStrip = tiff.GetField(TiffTag.ROWSPERSTRIP)[0].ToInt();

                var resUnitField = tiff.GetField(TiffTag.RESOLUTIONUNIT);
                int resolutionUnit = resUnitField != null ? resUnitField[0].ToInt() : 2; // inches by default

                var xResField = tiff.GetField(TiffTag.XRESOLUTION);
                var yResField = tiff.GetField(TiffTag.YRESOLUTION);
                float xResolution = xResField != null ? xResField[0].ToFloat() : 96;
                float yResolution = yResField != null ? yResField[0].ToFloat() : 96;

                //int dpiX = (int)(xResolution * (resolutionUnit == 2 ? 1 : 39.3701f)); // 2 = inches, 3 = centimeters
                //int dpiY = (int)(yResolution * (resolutionUnit == 2 ? 1 : 39.3701f)); // 2 = inches, 3 = centimeters

                int dpiX = resolutionUnit == 3  // 3 = centimeters
                    ? (int)(xResolution * 2.54f)
                    : (int)xResolution; // default inches
                int dpiY = resolutionUnit == 3
                    ? (int)(yResolution * 2.54f)
                    : (int)yResolution;


                var compression = tiff.GetField(TiffTag.COMPRESSION)[0].ToInt();

                byte[] data = Array.Empty<byte>();

                switch (compression)
                {
                    case (int)TiffCompression.None:
                        data = await Task.Run(() =>
                            ExtractDecodedPixels(tiff, width, height));
                        break;

                    case (int)TiffCompression.CCITTG3:
                        data = await Task.Run(() =>
                               ExtractCcittRaw(tiff, width, height));
                        break;
                    case (int)TiffCompression.CCITTG4:
                        data = await Task.Run(() =>
                               ExtractCcittRaw(tiff, width, height));

                        break;
                    case (int)TiffCompression.JPEG:
                        data = await Task.Run(() =>
                               ExtractJpegRaw(tiff));
                        break;
                    case (int)TiffCompression.LZW:
                        data = await Task.Run(() =>
                            ExtractDecodedPixels(tiff, width, height));
                        break;
                    case (int)TiffCompression.Deflate:
                        data = await Task.Run(() =>
                             ExtractDecodedPixels(tiff, width, height));
                        break;
                    case (int)TiffCompression.PackBits:
                        data = await Task.Run(() =>
                            ExtractDecodedPixels(tiff, width, height));
                        break;
                    default:
                        Console.WriteLine($"Other compression: {compression}");
                        break;
                }
                return new TiffImageInfo
                {
                    Width = width,
                    Height = height,
                    Compression = compression,
                    FillOrder = fillOrder,
                    PhotoMetric = photoMetric,
                    SamplesPerPixel = samplesPerPixel,
                    Data = data
                };


            }
            return null;
        }

        private static byte[] ExtractDecodedPixels(Tiff image, int width, int height)
        {
            if (height > short.MaxValue)
                return ExtractDecodedPixelsByStrip(image, width, height);
            int scanlineSize = image.ScanlineSize();
            byte[] buffer = new byte[scanlineSize * height];
            for (short i = 0; i < height; i++)
            {
                image.ReadScanline(buffer, i * scanlineSize, i);
            }
            return buffer;
        }

        private static byte[] ExtractDecodedPixelsByStrip(Tiff image, int width, int height)
        {
            int stripsCount = image.NumberOfStrips();
            using var ms = new MemoryStream();

            for (int strip = 0; strip < stripsCount; strip++)
            {
                int stripSize = (int)image.StripSize();
                byte[] stripBuffer = new byte[stripSize];
                int read = image.ReadEncodedStrip(strip, stripBuffer, 0, stripSize);
                if (read > 0)
                    ms.Write(stripBuffer, 0, read);
            }

            return ms.ToArray();
        }


        private static byte[] ExtractJpegRaw(Tiff image)
        {
            int stripsCount = image.NumberOfStrips();
            if (stripsCount <= 0)
                return Array.Empty<byte>();

            using var ms = new MemoryStream();

            for (int i = 0; i < stripsCount; i++)
            {
                int stripSize = (int)image.RawStripSize(i);
                if (stripSize <= 0)
                    continue;

                byte[] stripData = new byte[stripSize];
                int read = image.ReadRawStrip(i, stripData, 0, stripSize);

                if (read > 0)
                    ms.Write(stripData, 0, read);
            }

            byte[] raw = ms.ToArray();

            
            if (raw.Length < 4 || raw[0] != 0xFF || raw[1] != 0xD8)
            {
                Debug.WriteLine("Warning: JPEG signature not found (FFD8 missing)");
            }
            if (raw.Length > 2 && (raw[raw.Length - 2] != 0xFF || raw[raw.Length - 1] != 0xD9))
            {
                Debug.WriteLine("Appending missing JPEG EOI marker (FFD9)");
                using var fix = new MemoryStream(raw.Length + 2);
                fix.Write(raw, 0, raw.Length);
                fix.WriteByte(0xFF);
                fix.WriteByte(0xD9);
                raw = fix.ToArray();
            }


            return raw;
        }

        private static byte[] ExtractCcittRaw(Tiff image, int width, int height)
        {

            var stripsCount = image.NumberOfStrips();
            if (stripsCount <= 0)
            {
                return Array.Empty<byte>();
            }
            long totalSize = 0;
            for (int i = 0; i < stripsCount; i++)
            {

                long size = image.RawStripSize(i);
                totalSize = checked(totalSize + size);
            }

            byte[] buffer = new byte[totalSize];
            int offset = 0;
            for (int i = 0; i < stripsCount; i++)
            {
                int stripSize = (int)image.RawStripSize(i);
                image.ReadRawStrip(i, buffer, offset, stripSize);
                offset += stripSize;
            }

            return buffer;

        }


        private static MemoryStream TiffMemoryStream(TiffImageInfo tiffImageInfo)
        {
            MemoryStream resultStream;
            using (var temp = new MemoryStream())
            {
                using (var output = Tiff.ClientOpen("in-memory", "w", temp, new TiffStream()))
                {
                    output.SetField(TiffTag.IMAGEWIDTH, tiffImageInfo.Width);
                    output.SetField(TiffTag.IMAGELENGTH, tiffImageInfo.Height);
                    output.SetField(TiffTag.COMPRESSION, tiffImageInfo.Compression);
                    output.SetField(TiffTag.SAMPLESPERPIXEL, tiffImageInfo.SamplesPerPixel);
                    int bitsPerSample = tiffImageInfo.PhotoMetric == (int)Photometric.MINISWHITE ||
                                        tiffImageInfo.PhotoMetric == (int)Photometric.MINISBLACK ? 1 : 8;
                    output.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
                   
                    output.SetField(TiffTag.PHOTOMETRIC, tiffImageInfo.PhotoMetric);
                    output.SetField(TiffTag.FILLORDER, tiffImageInfo.FillOrder);
                    output.SetField(TiffTag.ROWSPERSTRIP, tiffImageInfo.Height);
                    output.WriteRawStrip(0, tiffImageInfo.Data, tiffImageInfo.Data.Length);
                    output.WriteDirectory();
                }
                resultStream = new MemoryStream(temp.ToArray());
            }
            resultStream.Position = 0;
            return resultStream;
        }

        public static async Task<ImageSource?> LoadImageSourceFromTiff(string filePath)
        {
            var tiffInfo = await ReadTiff(filePath);
            if (tiffInfo == null)
                return null;
            var rawData = tiffInfo.Value.Data;
            if (rawData.Length == 0)
                return null;

            if (tiffInfo.Value.Compression == (int)TiffCompression.JPEG)
            {
                return LoadImageSourceFromJpeg(rawData);
            }

            if (tiffInfo.Value.PhotoMetric == (int)Photometric.SEPARATED &&
                                                    tiffInfo.Value.SamplesPerPixel == 4)
            {
                Debug.WriteLine("Detected CMYK TIFF — converting to RGB");
                var rgbData = ConvertCmykToRgb(tiffInfo.Value.Data);

                // Обновим поля для RGB-варианта
                var rgbInfo = tiffInfo.Value;
                rgbInfo.Data = rgbData;
                rgbInfo.PhotoMetric = (int)Photometric.RGB;
                rgbInfo.SamplesPerPixel = 3;

                var rgbTiffStream = TiffMemoryStream(rgbInfo);
                return CreateImageSource(rgbTiffStream);
            }

            if (tiffInfo.Value.PhotoMetric == (int)Photometric.PALETTE)
            {
                using (var tiff = Tiff.Open(filePath, "r"))
                {
                    if (tiff == null)
                        return null;

                    var rgbData = ConvertPaletteToRgb(tiff, tiffInfo.Value.Data);

                    // создаём RGB-вариант
                    var rgbInfo = tiffInfo.Value;
                    rgbInfo.Data = rgbData;
                    rgbInfo.PhotoMetric = (int)Photometric.RGB;
                    rgbInfo.SamplesPerPixel = 3;

                    var rgbTiffStream = TiffMemoryStream(rgbInfo);
                    return CreateImageSource(rgbTiffStream);
                }
            }

            var tiffStream = TiffMemoryStream(tiffInfo.Value);
            var imageSource = CreateImageSource(tiffStream);
            return imageSource;
        }

        private static ImageSource LoadImageSourceFromJpeg(byte[] jpegData)
        {
            using var ms = new MemoryStream(jpegData);
            using var img = System.Drawing.Image.FromStream(ms);

            using var bmpStream = new MemoryStream();
            img.Save(bmpStream, System.Drawing.Imaging.ImageFormat.Bmp);
            bmpStream.Position = 0;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = bmpStream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }


        private static ImageSource? CreateImageSource(MemoryStream tiffStream)
        {
            try
            {
                tiffStream.Position = 0;
                var decoder = new TiffBitmapDecoder(tiffStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                frame.Freeze();
                return frame;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to decode TIFF: {ex.Message}");
                return null;
            }
        }

        private static byte[] ConvertCmykToRgb(byte[] cmykData)
        {
            if (cmykData == null || cmykData.Length % 4 != 0)
                return Array.Empty<byte>();

            int pixelCount = cmykData.Length / 4;
            byte[] rgbData = new byte[pixelCount * 3];

            for (int i = 0, j = 0; i < cmykData.Length; i += 4, j += 3)
            {
                float c = cmykData[i] / 255f;
                float m = cmykData[i + 1] / 255f;
                float y = cmykData[i + 2] / 255f;   
                float k = cmykData[i + 3] / 255f;

                float r = 1 - Math.Min(1, c * (1 - k) + k);
                float g = 1 - Math.Min(1, m * (1 - k) + k);
                float b = 1 - Math.Min(1, y * (1 - k) + k);

                rgbData[j] = (byte)(r * 255);
                rgbData[j + 1] = (byte)(g * 255);
                rgbData[j + 2] = (byte)(b * 255);
            }

            return rgbData;
        }

        private static byte[] ConvertPaletteToRgb(Tiff image, byte[] indexData)
        {
            var colorMap = image.GetField(TiffTag.COLORMAP);
            if (colorMap == null) return indexData;

            ushort[] red = colorMap[0].ToUShortArray();
            ushort[] green = colorMap[1].ToUShortArray();
            ushort[] blue = colorMap[2].ToUShortArray();

            int pixelCount = indexData.Length;
            byte[] rgbData = new byte[pixelCount * 3];

            for (int i = 0, j = 0; i < pixelCount; i++, j += 3)
            {
                int idx = indexData[i];
                rgbData[j] = (byte)(red[idx] >> 8);   // из 16 бит в 8 бит
                rgbData[j + 1] = (byte)(green[idx] >> 8);
                rgbData[j + 2] = (byte)(blue[idx] >> 8);
            }

            return rgbData;
        }


    }
}
