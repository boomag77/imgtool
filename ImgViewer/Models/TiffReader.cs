using BitMiracle.LibTiff.Classic;
using System.Diagnostics;
using System.Windows.Controls.Primitives;

namespace ImgViewer.Models
{
    internal class TiffReader
    {
        public struct TiffImageInfo
        {
            public int Width;
            public int Height;
            public int SamplesPerPixel;
            public int BitsPerSample;
            public int Compression;
            public byte[] RasterData;
        }

        public async Task<byte[]?> ReadTiff(string filePath)
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
                int fillOrder = tiff.GetField(TiffTag.FILLORDER)[0].ToInt();
                int planarConfig = tiff.GetField(TiffTag.PLANARCONFIG)[0].ToInt();
                int orientation = tiff.GetField(TiffTag.ORIENTATION)[0].ToInt();
                int rowsPerStrip = tiff.GetField(TiffTag.ROWSPERSTRIP)[0].ToInt();
                int resolutionUnit = tiff.GetField(TiffTag.RESOLUTIONUNIT)[0].ToInt();
                float xResolution = tiff.GetField(TiffTag.XRESOLUTION)[0].ToFloat();
                float yResolution = tiff.GetField(TiffTag.YRESOLUTION)[0].ToFloat();
                int dpiX = (int)(xResolution * (resolutionUnit == 2 ? 1 : 39.3701f)); // 2 = inches, 3 = centimeters
                int dpiY = (int)(yResolution * (resolutionUnit == 2 ? 1 : 39.3701f)); // 2 = inches, 3 = centimeters


                var compression = tiff.GetField(TiffTag.COMPRESSION)[0].ToInt();

                byte[] raster = new byte[height * width * samplesPerPixel * (bitsPerSample / 8)];

                switch (compression)
                {
                    case (int)TiffCompression.None:
                        await Task.Run(() =>
                            raster = ExtractUncompressed(tiff, width, height));
                        return raster;

                    case (int)TiffCompression.CCITTG3:
                        Console.WriteLine("CCITT Group 3 compression");
                        break;
                    case (int)TiffCompression.CCITTG4:
                        {
                            return await Task.Run(() =>
                                raster = ExtractCcittRaw(tiff, width, height));
                        }
                    case (int)TiffCompression.JPEG:

                        Console.WriteLine("JPEG compression");
                        break;
                    case (int)TiffCompression.LZW:
                        Console.WriteLine("LZW compression");
                        break;
                    case (int)TiffCompression.Deflate:
                        Console.WriteLine("Deflate compression");
                        break;
                    case (int)TiffCompression.PackBits:
                        Console.WriteLine("PackBits compression");
                        break;
                    default:
                        Console.WriteLine($"Other compression: {compression}");
                        break;
                }

                

            }
            return null;
        }

        private byte[] ExtractUncompressed(Tiff image, int width, int height)
        {
            
            return [];
        }

        private byte[] ExtractLzwRaw(Tiff image, int width, int height)
        {
            return ExtractUncompressed(image, width, height);
        }

        private byte[] ExtractJpegRaw(Tiff image, int width, int height)
        {
            return ExtractUncompressed(image, width, height);
        }

        private byte[] ExtractPackBitsRaw(Tiff image, int width, int height)
        {
            return ExtractUncompressed(image, width, height);
        }

        private byte[] ExtractDeflateRaw(Tiff image, int width, int height)
        {
            return ExtractUncompressed(image, width, height);
        }

        private byte[] ExtractCcittRaw(Tiff image, int width, int height)
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
    }
}
