using System.Buffers;
using System.Runtime.CompilerServices;

namespace ImgViewer.Models
{
    internal sealed class ImageResizer
    {

        private struct ResizeResult
        {
            public IMemoryOwner<byte>? OutputPixelsOwner;
            public int OutputWidth;
            public int OutputHeight;
            public int OutputStrideBytes;
        }

        private struct ResizeRequest
        {
            public ReadOnlyMemory<byte> InputPixels;
            public int InputWidth;
            public int InputHeight;
            public int InputStrideBytes;
            public int OutputWidth;
            public int OutputHeight;
            public bool KeepAspectRatio;
            public int BytesPerPixel;
            public ResizeMethod Method;
        }

        public ImageResizer()
        {
        }

        public bool TryResizeImage(ReadOnlyMemory<byte> inputPixels,
                                   int inputWidth, int inputHeight, int inputStrideBytes,
                                   int requestedOutputWidth, int requestedOutputHeight, bool keepAspectRatio,
                                   int bytesPerPixel,
                                   ResizeMethod method,
                                   out IMemoryOwner<byte>? outputPixelsOwner, out int outputWidth, out int outputHeight, out int outputStrideBytes)
        {

            var request = new ResizeRequest
            {
                InputPixels = inputPixels,
                InputWidth = inputWidth,
                InputHeight = inputHeight,
                InputStrideBytes = inputStrideBytes,
                OutputWidth = requestedOutputWidth,
                OutputHeight = requestedOutputHeight,
                KeepAspectRatio = keepAspectRatio,
                BytesPerPixel = bytesPerPixel,
                Method = method
            };
            var result = new ResizeResult();
            bool success = false;
            switch (method)
            {
                case ResizeMethod.NearestNeighbor:
                    // Call the nearest neighbor resizing method
                    if (!TryResizeNearestNeighbor(request, ref result))
                    {
                        outputPixelsOwner = null;
                        outputWidth = 0;
                        outputHeight = 0;
                        outputStrideBytes = 0;
                        return false;
                    }
                    success = true;
                    break;
                case ResizeMethod.Bilinear:
                    // Call the bilinear resizing method
                    break;
                case ResizeMethod.Bicubic:
                    // Call the bicubic resizing method
                    break;
                case ResizeMethod.Lanczos4:
                    // Call the Lanczos4 resizing method
                    break;
                default:
                    success = false; break;
            }
            if (!success)
            {
                outputPixelsOwner = null;
                outputWidth = 0;
                outputHeight = 0;
                outputStrideBytes = 0;
                return false;
            }
            outputPixelsOwner = result.OutputPixelsOwner;
            outputWidth = result.OutputWidth;
            outputHeight = result.OutputHeight;
            outputStrideBytes = result.OutputStrideBytes;
            return true;
        }



        private static bool TryResizeNearestNeighbor(ResizeRequest request,
                                              ref ResizeResult result)
        {
            unsafe
            {
                int srcWidth = request.InputWidth;
                int srcHeight = request.InputHeight;
                int dstWidth = request.OutputWidth;
                int dstHeight = request.OutputHeight;
                bool keepAspect = request.KeepAspectRatio;

                int bytesPerPixel = request.BytesPerPixel;
                int srcStrideBytes = request.InputStrideBytes;


                ReadOnlySpan<byte> srcPixels = request.InputPixels.Span;

                // --- hard preconditions (no div-by-zero, no nonsense) ---
                if (srcWidth <= 0 || srcHeight <= 0) return false;
                if (dstWidth <= 0 || dstHeight <= 0) return false;
                if (bytesPerPixel <= 0) return false;
                if (srcStrideBytes <= 0) return false;

                int outW = dstWidth;
                int outH = dstHeight;

                if (keepAspect)
                {
                    if ((long)dstWidth * srcHeight > (long)dstHeight * srcWidth)
                    {
                        outH = dstHeight;
                        outW = (int)(((long)dstHeight * srcWidth) / srcHeight);
                        if (outW <= 0) outW = 1;
                    }
                    else
                    {
                        outW = dstWidth;
                        outH = (int)(((long)dstWidth * srcHeight) / srcWidth);
                        if (outH <= 0) outH = 1;
                    }
                }

                // --- src safety (avoid int overflow in checks) ---
                long minSrcStride = (long)srcWidth * bytesPerPixel;          // bytes needed for one row
                if (srcStrideBytes < minSrcStride) return false;

                long requiredSrcBytes = (long)srcStrideBytes * srcHeight;    // bytes needed for all rows
                if (requiredSrcBytes > srcPixels.Length) return false;       // Span length is int, safe compare

                // --- dst safety (avoid int overflow and wrong Rent size) ---
                long dstStrideBytesL = (long)outW * bytesPerPixel;
                if (dstStrideBytesL <= 0 || dstStrideBytesL > int.MaxValue) return false;
                int dstStrideBytes = (int)dstStrideBytesL;

                long totalDstBytesL = dstStrideBytesL * outH;
                if (totalDstBytesL <= 0 || totalDstBytesL > int.MaxValue) return false;
                int totalDstBytes = (int)totalDstBytesL;


                IMemoryOwner<byte> dstPixelsOwner = MemoryPool<byte>.Shared.Rent(totalDstBytes);
                Span<byte> dst = dstPixelsOwner.Memory.Span[..totalDstBytes];

                var xByteOffsets = ArrayPool<int>.Shared.Rent(outW);
                try
                {

                    for (int xOut = 0; xOut < outW; xOut++)
                    {
                        int xIn = (int)(((2L * xOut + 1) * srcWidth) / (2L * outW));
                        xByteOffsets[xOut] = xIn * bytesPerPixel;
                    }


                    fixed (byte* srcPtr = srcPixels)
                    fixed (byte* dstPtr = dst)
                    {
                        for (int yOut = 0; yOut < outH; yOut++)
                        {
                            int yIn = (int)(((2L * yOut + 1) * srcHeight) / (2L * outH));

                            int srcRow = (int)((long)yIn * srcStrideBytes);
                            int dstRow = (int)((long)yOut * dstStrideBytes);
                            int d = dstRow;

                            for (int xOut = 0; xOut < outW; xOut++)
                            {
                                int s = srcRow + xByteOffsets[xOut];
                                byte* pSrc = srcPtr + s;
                                byte* pDst = dstPtr + d;
                                Unsafe.CopyBlockUnaligned(pDst, pSrc, (uint)bytesPerPixel);
                                d += bytesPerPixel;
                            }
                        }
                    }
                }
                finally
                {
                    ArrayPool<int>.Shared.Return(xByteOffsets, clearArray: false);
                }
                result.OutputPixelsOwner = dstPixelsOwner;
                result.OutputWidth = outW;
                result.OutputHeight = outH;
                result.OutputStrideBytes = dstStrideBytes;
                return true;
            }

        }

        private void ResizeBilinear(ReadOnlySpan<byte> inputPixels, int bytesPerPixel,
                                    int inputWidth, int inputHeight, int inputStrideBytes,
                                    int outputWidth, int outputHeight, int outputStrideBytes,
                                    Span<byte> outputPixels)
        {
            // Placeholder implementation
        }

        private void ResizeBicubic(ReadOnlySpan<byte> inputPixels, int bytesPerPixel,
                                   int inputWidth, int inputHeight, int inputStrideBytes,
                                   int outputWidth, int outputHeight, int outputStrideBytes,
                                   Span<byte> outputPixels)
        {
            // Placeholder implementation
        }

        private void ResizeLanczos4(ReadOnlySpan<byte> inputPixels, int bytesPerPixel,
                                    int inputWidth, int inputHeight, int inputStrideBytes,
                                    int outputWidth, int outputHeight, int outputStrideBytes,
                                    Span<byte> outputPixels)
        {
            // Placeholder implementation
        }

    }
}
