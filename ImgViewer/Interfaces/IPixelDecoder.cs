using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;

namespace ImgViewer.Interfaces
{
    public interface IPixelDecoder
    {
        bool TryDecodeToBgra32(
                                string path,
                                out int width,
                                out int height,
                                out int strideBytes,
                                out IMemoryOwner<byte>? pixelsOwner,
                                out string? fail);
    }
}
