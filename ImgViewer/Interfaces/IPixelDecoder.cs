using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Windows.Media;

namespace ImgViewer.Interfaces
{
    public interface IPixelDecoder
    {
        bool TryDecodeToBgra32(
                                ImageSource imageSource,
                                out int width,
                                out int height,
                                out int strideBytes,
                                out IMemoryOwner<byte>? pixelsOwner,
                                out string? fail);
    }
}
