using ImgViewer.Interfaces;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Buffers;
using System.Windows;

namespace ImgViewer.Models
{
    public sealed class WpfPixelDecoder : IPixelDecoder
    {
        public bool TryDecodeToBgra32(ImageSource imageSource,
                                        out int width, out int height, out int strideBytes,
                                        out double dpiX, out double dpiY,
                                        out IMemoryOwner<byte>? pixelsOwner, out string? fail)
        {
            try
            {
                var src = imageSource as BitmapSource;
                if (src == null)
                {
                    width = height = strideBytes = 0;
                    dpiX = dpiY = 0;
                    pixelsOwner = null;
                    fail = "Invalid image source.";
                    return false;
                }

                width = src.PixelWidth;
                height = src.PixelHeight;
                dpiX = src.DpiX;
                dpiY = src.DpiY;

                if (src.Format != PixelFormats.Bgra32)
                {
                    src = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
                }
                strideBytes = (checked(width * 4 + 3) & ~3); 
                int totalBytes = checked(strideBytes * height);
                

                pixelsOwner = MemoryPool<byte>.Shared.Rent(totalBytes);
                var mem = pixelsOwner.Memory.Slice(0, totalBytes);

                unsafe
                {
                    fixed (byte* p = mem.Span)
                    {
                        var rect = new Int32Rect(0, 0, width, height);

                        src.CopyPixels(rect, (IntPtr)p, totalBytes, strideBytes);
                    }
                }

                fail = null;
                return true;
            }
            catch (Exception ex)
            {
                width = height = strideBytes = 0;
                dpiX = dpiY = 0;
                pixelsOwner = null;
                fail = ex.Message;
                return false;
            }
        }
    }
}
