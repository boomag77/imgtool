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
            public int BitsPerSample;
            public bool IsRawCompressed;

            // Для decoded raster (если есть) — как раньше:
            public byte[] Data;

            // Для raw-compressed multi-strip TIFFs:
            public byte[][] RawStrips;               // каждый элемент = содержимое одного raw-strip (точно столько байт, сколько ReadRawStrip вернул)
            public int RowsPerStripOriginal;         // исходное ROWSPERSTRIP, чтобы правильно восстановить структуру
        }

        private static int GetIntTag(Tiff tiff, TiffTag tag, int defaultValue = 0)
        {
            FieldValue[]? v = tiff.GetField(tag);
            return v != null ? v[0].ToInt() : defaultValue;
        }

        private static void DumpTiffInfo(string path)
        {
            using (Tiff tif = Tiff.Open(path, "r"))
            {
                if (tif == null) { Debug.WriteLine("Can't open TIFF"); return; }

                Debug.WriteLine($"IsTiled: {tif.IsTiled()}");

                // helper to read scalar tags safely
                int GetInt(TiffTag tag, int defaultValue = -1)
                {
                    FieldValue[]? v = tif.GetField(tag);
                    if (v == null || v.Length == 0) return defaultValue;
                    try { return v[0].ToInt(); } catch { return defaultValue; }
                }

                double GetDouble(TiffTag tag, double defaultValue = double.NaN)
                {
                    FieldValue[]? v = tif.GetField(tag);
                    if (v == null || v.Length == 0) return defaultValue;
                    try { return v[0].ToDouble(); } catch { return defaultValue; }
                }
#if DEBUG
                Debug.WriteLine($"ImageWidth: {GetInt(TiffTag.IMAGEWIDTH)}");
                Debug.WriteLine($"ImageLength: {GetInt(TiffTag.IMAGELENGTH)}");
                Debug.WriteLine($"RowsPerStrip: {GetInt(TiffTag.ROWSPERSTRIP)}");
                Debug.WriteLine($"Compression: {GetInt(TiffTag.COMPRESSION)}");
                Debug.WriteLine($"Photometric: {GetInt(TiffTag.PHOTOMETRIC)}");
                Debug.WriteLine($"PlanarConfiguration: {GetInt(TiffTag.PLANARCONFIG)}");
                Debug.WriteLine($"SamplesPerPixel: {GetInt(TiffTag.SAMPLESPERPIXEL)}");
                Debug.WriteLine($"BitsPerSample: {GetInt(TiffTag.BITSPERSAMPLE)}");
#endif
                // Try to print STRIPOFFSETS and STRIPBYTECOUNTS if present
                FieldValue[]? off = tif.GetField(TiffTag.STRIPOFFSETS);
                if (off != null && off.Length > 0)
                {
                    // FieldValue may contain an array; try to safely print its elements
                    var raw = off[0];
                    try
                    {
                        object? val = raw.Value; // may be array or scalar
                        if (val is Array arr)
                        {
                            string s = string.Join(", ", arr.Cast<object>().Select(x => x.ToString()));
                            Debug.WriteLine($"StripOffsets: [{s}]");
                        }
                        else
                        {
                            Debug.WriteLine($"StripOffsets: {val}");
                        }
                    }
                    catch { Debug.WriteLine("StripOffsets: (unknown format)"); }
                }

                FieldValue[]? counts = tif.GetField(TiffTag.STRIPBYTECOUNTS);
                if (counts != null && counts.Length > 0)
                {
                    try
                    {
                        object? val = counts[0].Value;
                        if (val is Array arr)
                        {
                            string s = string.Join(", ", arr.Cast<object>().Select(x => x.ToString()));
                            Debug.WriteLine($"StripByteCounts: [{s}]");
                        }
                        else
                        {
                            Debug.WriteLine($"StripByteCounts: {val}");
                        }
                    }
                    catch { Debug.WriteLine("StripByteCounts: (unknown format)"); }
                }

                int strips = tif.NumberOfStrips();
                //Debug.WriteLine($"NumberOfStrips(): {strips}");
                int declaredStripSize = tif.StripSize();
                //Debug.WriteLine($"StripSize() (declared max): {declaredStripSize}");

                // For each strip: try to read encoded strip and log actual read bytes.
                for (int i = 0; i < strips; i++)
                {
                    try
                    {
                        int bufSize = Math.Max(declaredStripSize, 4096);
                        byte[] buf = new byte[bufSize];
                        // ReadEncodedStrip returns number of bytes read (or -1/0 if none)
                        int read = tif.ReadEncodedStrip(i, buf, 0, buf.Length);
                        //Debug.WriteLine($"Strip {i}: read={read} bytes (buffer size={bufSize})");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Strip {i}: read error: {ex.Message}");
                    }
                }
            }
        }

        private static byte[][] ExtractCcittRawStrips(Tiff image)
        {
            int stripsCount = image.NumberOfStrips();
            if (stripsCount <= 0)
                return Array.Empty<byte[]>();

            var list = new System.Collections.Generic.List<byte[]>(stripsCount);

            for (int i = 0; i < stripsCount; i++)
            {
                int rawSize = (int)image.RawStripSize(i);
                if (rawSize <= 0)
                {
                    // возможно zero-length strip — добавим пустой элемент, чтобы индексы совпадали
                    list.Add(Array.Empty<byte>());
                    continue;
                }

                // выделяем буфер равный ожидаемому compressed size
                // ReadRawStrip может вернуть меньше; используем возвращаемое значение для корректной длины
                byte[] buf = new byte[rawSize];
                try
                {
                    int read = image.ReadRawStrip(i, buf, 0, rawSize); // read = number of compressed bytes actually read
                    if (read <= 0)
                    {
                        list.Add(Array.Empty<byte>());
                    }
                    else if (read == rawSize)
                    {
                        list.Add(buf);
                    }
                    else
                    {
                        // урезаем до фактического прочитанного размера
                        byte[] shrunk = new byte[read];
                        Array.Copy(buf, 0, shrunk, 0, read);
                        list.Add(shrunk);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading raw strip {i}: {ex.Message}");
                    // безопасно: добавляем пустой strip, обработка дальше может обнаружить и логнуть проблему
                    list.Add(Array.Empty<byte>());
                }
            }

            return list.ToArray();
        }


        private static async Task<TiffImageInfo?> ReadTiff(string filePath)
        {
            return await Task.Run<TiffImageInfo?>(() =>
            {
                try
                {
                    using (Tiff tiff = Tiff.Open(filePath, "r"))
                    {
                        if (tiff == null)
                        {
                            Debug.WriteLine("Could not open incoming image");
                            return null;
                        }
#if DEBUG
                        DumpTiffInfo(filePath);
#endif
                        int width = GetIntTag(tiff, TiffTag.IMAGEWIDTH);
                        int height = GetIntTag(tiff, TiffTag.IMAGELENGTH);
                        if (width <= 0 || height <= 0)
                        {
                            Debug.WriteLine($"Invalid image size: width={width}, height={height}");
                            return null;
                        }
                        int samplesPerPixel = GetIntTag(tiff, TiffTag.SAMPLESPERPIXEL, 1);
                        int bitsPerSample = GetIntTag(tiff, TiffTag.BITSPERSAMPLE, 8);
                        int photoMetric = GetIntTag(tiff, TiffTag.PHOTOMETRIC, (int)Photometric.MINISBLACK);
                        int fillOrder = GetIntTag(tiff, TiffTag.FILLORDER, (int)FillOrder.MSB2LSB);
                        int planarConfig = GetIntTag(tiff, TiffTag.PLANARCONFIG, (int)PlanarConfig.CONTIG);
                        if (planarConfig != (int)PlanarConfig.CONTIG)
                        {
                            Debug.WriteLine("Unsupported planar configuration (not CONTIG)");
                            return null;
                        }
                        //int? orientation = tiff.GetField(TiffTag.ORIENTATION)[0].ToInt();
                        int rowsPerStrip = GetIntTag(tiff, TiffTag.ROWSPERSTRIP, height);

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


                        int compression = GetIntTag(tiff, TiffTag.COMPRESSION, (int)TiffCompression.None);

                        byte[] data = Array.Empty<byte>();
                        bool isRaw = false;

                        byte[][] tiRawStrips = null;
                        int rowsPerStripOriginal = height;

                        switch (compression)
                        {
                            case (int)TiffCompression.None:
                                //data = ExtractDecodedPixels(tiff, width, height);
                                data = ExtractDecodedPixelsByStrip(tiff, width, height);
                                break;

                            case (int)TiffCompression.CCITTG3:
                            case (int)TiffCompression.CCITTG4:
                                //data = ExtractCcittRaw(tiff, width, height);
                                //isRaw = true;
                                {
                                    // читаем каждый raw-strip в отдельный элемент массива
                                    var strips = ExtractCcittRawStrips(tiff);
                                    data = Array.Empty<byte>(); // keep Data empty for raw case — raw strips are in RawStrips
                                    isRaw = true;

                                    // создадим локальный TiffImageInfo и заполним поля ниже (см. возвращаемую структуру)
                                    // но т.к. ReadTiff в одном месте формирует TiffImageInfo в конце, мы запишем полученные значения туда:
                                    tiRawStrips = strips;
                                    rowsPerStripOriginal = GetIntTag(tiff, TiffTag.ROWSPERSTRIP, height);
                                }
                                break;
                            case (int)TiffCompression.JPEG:
                                //data = ExtractJpegRaw(tiff);
                                //isRaw = true;

                                data = tiff.IsTiled()
                                    ? ExtractDecodedPixelsByTile(tiff, width, height)
                                    : ExtractDecodedPixelsByStrip(tiff, width, height);
                                isRaw = false;
                                compression = (int)TiffCompression.None; // important: data is now decompressed

                                break;
                            case (int)TiffCompression.LZW:
                                //data = ExtractDecodedPixels(tiff, width, height);
                                data = ExtractDecodedPixelsByStrip(tiff, width, height);
                                break;
                            case (int)TiffCompression.Deflate:
                                data = ExtractDecodedPixels(tiff, width, height);
                                break;
                            case (int)TiffCompression.PackBits:
                                data = ExtractDecodedPixels(tiff, width, height);
                                break;
                            default:
                                Debug.WriteLine($"Other compression: {compression}");
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
                            BitsPerSample = bitsPerSample,
                            IsRawCompressed = isRaw,
                            Data = data
                        };


                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading TIFF: {ex.Message}");
                    return null;
                }

            });
        }

        private static byte[] ExtractDecodedPixelsByTile(Tiff image, int width, int height)
        {
            int bitsPerSample = GetIntTag(image, TiffTag.BITSPERSAMPLE, 8);
            int samplesPerPixel = GetIntTag(image, TiffTag.SAMPLESPERPIXEL, 1);
            int planar = GetIntTag(image, TiffTag.PLANARCONFIG, (int)PlanarConfig.CONTIG);

            if (bitsPerSample % 8 != 0 || planar != (int)PlanarConfig.CONTIG)
            {
                Debug.WriteLine("Tile decode: unsupported bps or planar config, falling back to strips.");
                return ExtractDecodedPixelsByStrip(image, width, height);
            }

            int bytesPerPixel = (bitsPerSample / 8) * samplesPerPixel;

            int tileWidth = GetIntTag(image, TiffTag.TILEWIDTH, width);
            int tileHeight = GetIntTag(image, TiffTag.TILELENGTH, height);

            int tilesAcross = (width + tileWidth - 1) / tileWidth;
            int tilesDown = (height + tileHeight - 1) / tileHeight;

            int tileSize = image.TileSize();
            if (tileSize <= 0) return Array.Empty<byte>();

            byte[] output = new byte[width * height * bytesPerPixel];
            byte[] tileBuffer = new byte[tileSize];

            for (int ty = 0; ty < tilesDown; ty++)
            {
                for (int tx = 0; tx < tilesAcross; tx++)
                {
                    int x = tx * tileWidth;
                    int y = ty * tileHeight;

                    int tileIndex = image.ComputeTile(x, y, 0, 0);
                    int read = image.ReadEncodedTile(tileIndex, tileBuffer, 0, tileSize);
                    if (read <= 0) continue;

                    int copyWidth = Math.Min(tileWidth, width - x);
                    int copyHeight = Math.Min(tileHeight, height - y);

                    int tileRowBytes = tileWidth * bytesPerPixel;
                    int outRowBytes = width * bytesPerPixel;

                    for (int row = 0; row < copyHeight; row++)
                    {
                        int srcOffset = row * tileRowBytes;
                        int dstOffset = (y + row) * outRowBytes + x * bytesPerPixel;
                        Buffer.BlockCopy(tileBuffer, srcOffset, output, dstOffset, copyWidth * bytesPerPixel);

                    }
                }
            }

            return output;
        }

        private static byte[] ExtractDecodedPixels(Tiff image, int width, int height)
        {
            if (height > short.MaxValue)
                return ExtractDecodedPixelsByStrip(image, width, height);

            int scanlineSize = image.ScanlineSize();
            if (scanlineSize <= 0) { Debug.WriteLine("Bad scanline size"); return Array.Empty<byte>(); }
            long total = (long)scanlineSize * height;
            if (total <= 0 || total > int.MaxValue) { Debug.WriteLine("Too big"); return Array.Empty<byte>(); }

            byte[] buffer = new byte[(int)total];
            for (int i = 0; i < height; i++)
            {
                int offset = i * scanlineSize;
                if (offset < 0 || (long)offset + scanlineSize > buffer.Length)
                {
                    Debug.WriteLine($"Scanline would overflow buffer: row={i}, offset={offset}, scanlineSize={scanlineSize}, bufferLen={buffer.Length}");
                    return Array.Empty<byte>();
                }
                image.ReadScanline(buffer, i * scanlineSize, (short)i);
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
                try
                {
                    int read = image.ReadEncodedStrip(strip, stripBuffer, 0, stripSize);
                    if (read > 0)
                        ms.Write(stripBuffer, 0, read);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading strip (ExtractDecodedPixelsByStrip) {strip}: {ex.Message}");
                    return Array.Empty<byte>();

                }
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
                try
                {
                    int read = image.ReadRawStrip(i, stripData, 0, stripSize);

                    if (read > 0)
                        ms.Write(stripData, 0, read);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading strip (ExtractJpegRaw) {i}: {ex.Message}");
                    return Array.Empty<byte>();
                }

            }

            byte[] raw = ms.ToArray();

            var jpt = image.GetField(TiffTag.JPEGTABLES);
            if ((raw.Length < 2 || raw[0] != 0xFF || raw[1] != 0xD8) && jpt != null)
            {
                try
                {
                    byte[] tables = jpt[0].ToByteArray();
                    using var fixedMs = new MemoryStream();
                    fixedMs.Write(tables, 0, tables.Length);
                    fixedMs.Write(raw, 0, raw.Length);
                    raw = fixedMs.ToArray();
                }
                catch { /* log and fallthrough */ }
            }

            if (raw.Length > 2 && (raw[raw.Length - 2] != 0xFF || raw[raw.Length - 1] != 0xD9))
            {
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
            if (totalSize <= 0 || totalSize > int.MaxValue)
            {
                Debug.WriteLine($"CCITT total size invalid: {totalSize}");
                return Array.Empty<byte>();
            }
            int total = (int)totalSize;
            byte[] buffer = new byte[total];
            int offset = 0;
            for (int i = 0; i < stripsCount; i++)
            {
                int stripSize = (int)image.RawStripSize(i);
                if (stripSize <= 0)
                {
                    Debug.WriteLine($"Skipping zero-length raw strip {i}");
                    continue;
                }
                try
                {
                    image.ReadRawStrip(i, buffer, offset, stripSize);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error reading strip (ExtractCcittRaw) {i}: {ex.Message}");
                    return Array.Empty<byte>();
                }

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
                    if (output == null)
                    {
                        Debug.WriteLine("Could not open output TIFF");
                        return new MemoryStream();
                    }

                    output.SetField(TiffTag.IMAGEWIDTH, tiffImageInfo.Width);
                    output.SetField(TiffTag.IMAGELENGTH, tiffImageInfo.Height);
                    output.SetField(TiffTag.COMPRESSION, tiffImageInfo.Compression);
                    output.SetField(TiffTag.SAMPLESPERPIXEL, tiffImageInfo.SamplesPerPixel);
                    
                    output.SetField(TiffTag.BITSPERSAMPLE, tiffImageInfo.BitsPerSample);
                   
                    output.SetField(TiffTag.PHOTOMETRIC, tiffImageInfo.PhotoMetric);
                    output.SetField(TiffTag.PLANARCONFIG, (int)PlanarConfig.CONTIG);
                    output.SetField(TiffTag.FILLORDER, tiffImageInfo.FillOrder);
                    output.SetField(TiffTag.ROWSPERSTRIP, tiffImageInfo.Height);

                    if (tiffImageInfo.IsRawCompressed && tiffImageInfo.RawStrips != null && tiffImageInfo.RawStrips.Length > 0)
                    {
                        // Восстанавливаем исходную структуру strip'ов:
                        output.SetField(TiffTag.ROWSPERSTRIP, tiffImageInfo.RowsPerStripOriginal);

                        for (int i = 0; i < tiffImageInfo.RawStrips.Length; i++)
                        {
                            byte[] strip = tiffImageInfo.RawStrips[i] ?? Array.Empty<byte>();
                            if (strip.Length == 0)
                            {
                                // Записываем пустой strip (libtiff/WriteRawStrip может ожидать >=0)
                                output.WriteRawStrip(i, strip, 0, 0);
                            }
                            else
                            {
                                output.WriteRawStrip(i, strip, 0, strip.Length);
                            }
                        }
                    }
                    else if (tiffImageInfo.IsRawCompressed)
                    {
                        // fallback (старое поведение) — если RawStrips не заполнены, запишем Data как один raw strip
                        output.WriteRawStrip(0, tiffImageInfo.Data, 0, tiffImageInfo.Data.Length);
                    }
                    else
                    {
                        output.WriteEncodedStrip(0, tiffImageInfo.Data, 0, tiffImageInfo.Data.Length);
                    }

                    output.WriteDirectory();
                    output.Flush();
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
                return await Task.Run(() => LoadImageSourceFromJpeg(rawData));
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

                using var rgbTiffStream = TiffMemoryStream(rgbInfo);
                return CreateImageSource(rgbTiffStream);
            }

            if (tiffInfo.Value.PhotoMetric == (int)Photometric.PALETTE)
            {
                using (var tiff = Tiff.Open(filePath, "r"))
                {
                    if (tiff == null)
                        return null;

                    var rgbData = ConvertPaletteToRgb(tiff, tiffInfo.Value.Data, tiffInfo.Value.BitsPerSample);

                    // создаём RGB-вариант
                    var rgbInfo = tiffInfo.Value;
                    rgbInfo.Data = rgbData;
                    rgbInfo.PhotoMetric = (int)Photometric.RGB;
                    rgbInfo.SamplesPerPixel = 3;

                    using var rgbTiffStream = TiffMemoryStream(rgbInfo);
                    return CreateImageSource(rgbTiffStream);
                }
            }

            using var tiffStream = TiffMemoryStream(tiffInfo.Value);
            return CreateImageSource(tiffStream);
        }

        public static async Task<byte[]?> LoadPixelsFromTiff(string filePath)
        {
            var tiffInfo = await ReadTiff(filePath);
            if (tiffInfo == null)
                return null;
            var rawData = tiffInfo.Value.Data;
            if (rawData.Length == 0)
                return null;

            if (tiffInfo.Value.Compression == (int)TiffCompression.JPEG)
            {
                return rawData;
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

                using var rgbTiffStream = TiffMemoryStream(rgbInfo);
                return rgbTiffStream.ToArray();
            }

            if (tiffInfo.Value.PhotoMetric == (int)Photometric.PALETTE)
            {
                using (var tiff = Tiff.Open(filePath, "r"))
                {
                    if (tiff == null)
                        return null;

                    var rgbData = ConvertPaletteToRgb(tiff, tiffInfo.Value.Data, tiffInfo.Value.BitsPerSample);

                    // создаём RGB-вариант
                    var rgbInfo = tiffInfo.Value;
                    rgbInfo.Data = rgbData;
                    rgbInfo.PhotoMetric = (int)Photometric.RGB;
                    rgbInfo.SamplesPerPixel = 3;

                    using var rgbTiffStream = TiffMemoryStream(rgbInfo);
                    return rgbTiffStream.ToArray();
                }
            }

            using var tiffStream = TiffMemoryStream(tiffInfo.Value);
            return tiffStream.ToArray();
        }



        private static ImageSource LoadImageSourceFromJpeg(byte[] jpegData)
        {
            using var ms = new MemoryStream(jpegData);
            var decoder = new JpegBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var copy = new WriteableBitmap(frame);
            copy.Freeze();
            return copy;

        }


        private static ImageSource? CreateImageSource(MemoryStream tiffStream)
        {
            try
            {
                tiffStream.Position = 0;
                var decoder = new TiffBitmapDecoder(tiffStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames[0];
                var copy = new WriteableBitmap(frame);
                copy.Freeze();
                return copy;
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

                r = (r < 0f) ? 0f : (r > 1f ? 1f : r);
                g = (g < 0f) ? 0f : (g > 1f ? 1f : g);
                b = (b < 0f) ? 0f : (b > 1f ? 1f : b);

                rgbData[j] = (byte)(r * 255f + 0.5f);
                rgbData[j + 1] = (byte)(g * 255f + 0.5f);
                rgbData[j + 2] = (byte)(b * 255f + 0.5f);
            }

            return rgbData;
        }

        private static byte[] ConvertPaletteToRgb(Tiff image, byte[] indexData, int bitsPerSample)
        {
            var colorMap = image.GetField(TiffTag.COLORMAP);
            if (colorMap == null) return indexData;

            ushort[] red = colorMap[0].ToUShortArray();
            ushort[] green = colorMap[1].ToUShortArray();
            ushort[] blue = colorMap[2].ToUShortArray();

            byte[] indices;
            if (bitsPerSample >= 8)
            {
                indices = indexData;
            }
            else
            {
                int pixels = (indexData.Length * 8) / bitsPerSample;
                indices = new byte[pixels];
                int bitPos = 0;
                int mask = (1 << bitsPerSample) - 1;

                for (int p = 0; p < pixels; ++p)
                {
                    int byteIndex = bitPos / 8;
                    int bitOffset = bitPos % 8;

                    int value = (indexData[byteIndex] & 0xFF) << 8; 
                    if (byteIndex + 1 < indexData.Length)
                        value |= (indexData[byteIndex + 1] & 0xFF);

                    int shift = 16 - bitOffset - bitsPerSample;
                    int val = (value >> shift) & mask;
                    indices[p] = (byte)val;

                    bitPos += bitsPerSample;
                }

            }

            int pixelCount = indices.Length;
            byte[] rgbData = new byte[pixelCount * 3];

            for (int i = 0, j = 0; i < pixelCount; i++, j += 3)
            {
                int paletteSize = Math.Min(red.Length, Math.Min(green.Length, blue.Length));
                int idx = indices[i];
                if (idx < 0) idx = 0;
                if (idx >= paletteSize) idx = paletteSize - 1;
                rgbData[j] = (byte)(red[idx] >> 8);   // из 16 бит в 8 бит
                rgbData[j + 1] = (byte)(green[idx] >> 8);
                rgbData[j + 2] = (byte)(blue[idx] >> 8);
            }

            return rgbData;
        }


    }
}
