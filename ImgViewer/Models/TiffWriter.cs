using BitMiracle.LibTiff.Classic;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public enum TiffCompression
{
    None = 1,
    CCITTG3 = 3,
    CCITTG4 = 4,
    LZW = 5,
    Deflate = 8,
    JPEG = 7,
    PackBits = 32773
}

namespace ImgViewer.Models
{
    public class TiffWriter :IDisposable
    {


        public void Dispose()
        {
            // nothing to dispose
        }
        // public entry point
        public void SaveTiff(Stream stream, string path, TiffCompression compression, int dpi = 300, bool overwrite = true)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentNullException(nameof(path));

            // ensure output folder
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (File.Exists(path))
            {
                if (overwrite)
                    File.Delete(path);
                else
                    throw new IOException($"File already exists: {path}");
            }

            // make a seekable copy of stream (OpenCV streams may be non-seekable)
            MemoryStream ms = stream as MemoryStream;
            bool createdCopy = false;
            if (ms == null || !ms.CanSeek)
            {
                ms = new MemoryStream();
                stream.CopyTo(ms);
                createdCopy = true;
            }
            ms.Position = 0;

            try
            {
                // If CCITT requested -> use LibTiff.NET pipeline (guaranteed)
                if (compression == TiffCompression.CCITTG4 || compression == TiffCompression.CCITTG3)
                {
                    // decode stream to System.Drawing.Bitmap
                    using var bmp = (Bitmap)Image.FromStream(ms);

                    // convert to binary 0/255 bytes (grayscale + Otsu)
                    var binPixels = ConvertBitmapToBinary(bmp, out int width, out int height);

                    // detect if need to invert (we want background white, foreground black typical for fax)
                    bool invert = ShouldInvertBinary(binPixels, width, height);
                    if (invert)
                        InvertBinary(binPixels);

                    // write via LibTiff.NET
                    SaveBinaryBytesAsCcitt(binPixels, width, height, path, dpi, compression == TiffCompression.CCITTG3 ? Compression.CCITTFAX3 : Compression.CCITTFAX4, photometricMinIsWhite: false);
                }
                else
                {
                    // For non-CCITT compressions, use Magick.NET if available (or fallback to saving decoded TIFF from System.Drawing)
                    // Try to use Magick.NET settings if you prefer; here we'll do a safe fallback: decode and re-encode via System.Drawing (uncompressed TIFF) then let Magick handle compression if present.
                    // Simpler: write the decoded image to disk using Image.Save with requested encoder params if available.

                    // decode to Bitmap and use Magick if present; otherwise save PNG or TIFF
                    ms.Position = 0;
                    using var bmp = (Bitmap)Image.FromStream(ms);
                    // try Magick.NET write if present (optional) - but simplest: save as tiff using GDI+ (no compression)
                    // Note: GDI+ doesn't support many TIFF compressions; recommended to add Magick.NET here.
                    bmp.Save(path, ImageFormat.Tiff); // uncompressed likely
                }
            }
            finally
            {
                if (createdCopy) ms.Dispose();
            }
        }

        // ---------------- helpers ----------------

        // Convert System.Drawing.Bitmap to binary array (0 or 255 per pixel)
        // returns byte[] length = width*height row-major
        private static byte[] ConvertBitmapToBinary(Bitmap bmp, out int width, out int height)
        {
            width = bmp.Width;
            height = bmp.Height;
            // Convert to 24bpp RGB if not already
            using var converted = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics g = Graphics.FromImage(converted))
                g.DrawImage(bmp, 0, 0, width, height);

            // Lock bits
            var rect = new Rectangle(0, 0, width, height);
            var data = converted.LockBits(rect, ImageLockMode.ReadOnly, converted.PixelFormat);
            try
            {
                int srcStride = data.Stride;
                int bytesPerPixel = Image.GetPixelFormatSize(converted.PixelFormat) / 8;
                byte[] row = new byte[srcStride];
                byte[] gray = new byte[width * height];

                IntPtr scan0 = data.Scan0;
                for (int y = 0; y < height; y++)
                {
                    Marshal.Copy(scan0 + y * srcStride, row, 0, srcStride);
                    int dstOff = y * width;
                    for (int x = 0; x < width; x++)
                    {
                        int idx = x * bytesPerPixel;
                        byte b = row[idx + 0];
                        byte g = row[idx + 1];
                        byte r = row[idx + 2];
                        // luminance
                        int lum = (int)(0.2126 * r + 0.7152 * g + 0.0722 * b);
                        if (lum < 0) lum = 0;
                        if (lum > 255) lum = 255;
                        gray[dstOff + x] = (byte)lum;
                    }
                }

                //const byte threshold = 128; // можно выбрать другое значение или сделать параметром
                //byte[] bin = new byte[width * height];
                //for (int i = 0; i < bin.Length; i++)
                //    bin[i] = (gray[i] > threshold) ? (byte)255 : (byte)0;

                //return bin;

                // compute Otsu threshold
                byte thresh = ComputeOtsuThreshold(gray);
                // binarize
                byte[] bin = new byte[width * height];
                for (int i = 0; i < bin.Length; i++)
                    bin[i] = (gray[i] > thresh) ? (byte)255 : (byte)0;
                return bin;
            }
            finally
            {
                converted.UnlockBits(data);
            }
        }

        

        // Otsu implementation
        private static byte ComputeOtsuThreshold(byte[] gray)
        {
            long[] hist = new long[256];
            for (int i = 0; i < gray.Length; i++) hist[gray[i]]++;
            long total = gray.Length;
            double sum = 0;
            for (int t = 0; t < 256; t++) sum += t * hist[t];

            double sumB = 0;
            long wB = 0;
            long wF = 0;
            double varMax = 0;
            int threshold = 0;

            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                wF = total - wB;
                if (wF == 0) break;

                sumB += (double)(t * hist[t]);
                double mB = sumB / wB;
                double mF = (sum - sumB) / wF;
                double varBetween = (double)wB * (double)wF * (mB - mF) * (mB - mF);

                if (varBetween > varMax)
                {
                    varMax = varBetween;
                    threshold = t;
                }
            }
            return (byte)threshold;
        }

        // Heuristic: decide if inversion needed (we want background white)
        private static bool ShouldInvertBinary(byte[] bin, int width, int height)
        {
            long white = 0;
            long black = 0;
            for (int i = 0; i < bin.Length; i++)
            {
                if (bin[i] == 0) white++; // here 0 = black? depends on binarize - we used >thresh -> 255 so 255 is bright
                else black++;
            }
            // Actually compute properly: in our bin 255 = foreground bright, 0 = background dark
            // we expect background white (255). If most pixels are black(==0) then invert.
            long count255 = bin.LongCount(b => b == 255);
            long count0 = bin.LongCount(b => b == 0);
            return count255 < count0;
        }

        private static void InvertBinary(byte[] bin)
        {
            for (int i = 0; i < bin.Length; i++)
                bin[i] = (byte)(bin[i] == 0 ? 255 : 0);
        }

        // pack row 0/255 -> 1-bit MSB-first
        private static byte[] PackRowTo1Bit(byte[] srcRow, int width)
        {
            int dstStride = (width + 7) / 8;
            byte[] dst = new byte[dstStride];
            for (int x = 0; x < width; x++)
            {
                int byteIndex = x >> 3;
                int bitIndex = 7 - (x & 7);
                if (srcRow[x] != 0) // 255 => black(1)
                    dst[byteIndex] |= (byte)(1 << bitIndex);
            }
            return dst;
        }

        private static void PackRowTo1BitInto(byte[] srcRow, int width, byte[] dstPacked)
        {
            Array.Clear(dstPacked, 0, dstPacked.Length);
            for (int x = 0; x < width; x++)
            {
                int byteIndex = x >> 3;
                int bitIndex = 7 - (x & 7);
                if (srcRow[x] != 0)
                    dstPacked[byteIndex] |= (byte)(1 << bitIndex);
            }
        }

        // Save binary via LibTiff.NET with chosen compression (CCITT G3/G4)
        private static void SaveBinaryBytesAsCcitt(byte[] binPixels, int width, int height, string outPath, int dpi, Compression compressionMethod, bool photometricMinIsWhite = true)
        {
            if (binPixels.Length != width * height)
                throw new ArgumentException("binPixels length mismatch");

            // Open TIFF
            using var tif = Tiff.Open(outPath, "w");
            if (tif == null) throw new InvalidOperationException("Cannot open output tiff.");

            tif.SetField(TiffTag.IMAGEWIDTH, width);
            tif.SetField(TiffTag.IMAGELENGTH, height);
            tif.SetField(TiffTag.BITSPERSAMPLE, 1);
            tif.SetField(TiffTag.SAMPLESPERPIXEL, 1);
            tif.SetField(TiffTag.COMPRESSION, compressionMethod);
            tif.SetField(TiffTag.PHOTOMETRIC, photometricMinIsWhite ? Photometric.MINISWHITE : Photometric.MINISBLACK);
            tif.SetField(TiffTag.FILLORDER, FillOrder.MSB2LSB);
            tif.SetField(TiffTag.ROWSPERSTRIP, height);
            if (dpi > 0)
            {
                tif.SetField(TiffTag.XRESOLUTION, (double)dpi);
                tif.SetField(TiffTag.YRESOLUTION, (double)dpi);
                tif.SetField(TiffTag.RESOLUTIONUNIT, ResUnit.INCH);
            }

            int packedStride = (width + 7) / 8;
            var srcRow = new byte[width];
            var packedRow = new byte[packedStride];

            for (int y = 0; y < height; y++)
            {
                Buffer.BlockCopy(binPixels, y * width, srcRow, 0, width);
                PackRowTo1BitInto(srcRow, width, packedRow); // версия, пишущая в переданный packedRow
                bool res = tif.WriteScanline(packedRow, y);
                if (!res) throw new IOException($"WriteScanline failed at row {y}");
            }

            tif.WriteDirectory();
        }
    }
}
