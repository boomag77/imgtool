using OpenCvSharp;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using Tesseract;

namespace ImgViewer.Models
{
    class TextAwareOrienter
    {
        private CancellationToken _cancellationToken;
        private TesseractEngine _engine;

        public TextAwareOrienter(CancellationToken token, TesseractEngine engine)
        {
            _cancellationToken = token;
            _engine = engine;
        }

        public Mat Orient(Mat src, double minConfidence = 4.0)
        {
            int orientation = GetOrientation(src, minConfidence);
            return Rotate(src, orientation);
        }

        private Mat Rotate(Mat src, int orientation)
        {
            if (orientation == 0) return src;
            Mat rotated = new Mat();
            switch (orientation)
            {
                case 90:
                    Cv2.Rotate(src, rotated, RotateFlags.Rotate90Counterclockwise);
                    break;
                case 180:
                    Cv2.Rotate(src, rotated, RotateFlags.Rotate180);
                    break;
                case 270:
                    Cv2.Rotate(src, rotated, RotateFlags.Rotate90Clockwise);
                    break;
                default:
                    return src; // invalid orientation
            }
            return rotated;
        }


        //returns orientation in degrees (0, 90, 180, 270) or 0 if confidence is low
        public int GetOrientation(Mat src, double minConfidence = 4.0)
        {
            //using var engine = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
            if (!TryConvertMatToPix(src, out Pix? pixSrc) || pixSrc is null)
            {
                return 0;
            }

            using var pix = pixSrc;
            pix.Save("debug_orient_input.png"); // for debugging
            Debug.WriteLine($"Tesseract Engine version: {_engine.Version}");
            using var page = _engine.Process(pix, PageSegMode.AutoOsd);
            var orientation = 0;
            var confidence = 0f;
            try
            {
                page.DetectBestOrientation(out orientation, out confidence);
                Debug.WriteLine($"Detected orientation: {orientation} degrees with confidence {confidence}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during orientation detection: {ex.Message}");
                return 0;
            }
            var orientationConfidence = confidence;
            if (orientationConfidence < minConfidence)
            {
                return 0;
            }
            return orientation;
        }

        public double GetDeskewAngle(Mat src)
        {
            //using var engine = new TesseractEngine(@"./tessdata", "eng", EngineMode.Default);
            if (!TryConvertMatToPix(src, out Pix? pixSrc) || pixSrc is null)
            {
                return 0;
            }
            using var pix = pixSrc;
            using var page = _engine.Process(pix, PageSegMode.AutoOsd);
            using var layout = page.AnalyseLayout();
            layout.Begin();
            var props = layout.GetProperties();
            double deskewAngleDegrees = props.DeskewAngle * (180.0 / Math.PI);
            return deskewAngleDegrees;
        }


        private static bool TryEnsureToGray8U(Mat input, out Mat? gray)
        {
            if (input == null || input.Empty())
            {
                gray = null;
                return false;
            }

            int ch = input.Channels();
            Mat? temp = null;
            try
            {
                if (ch == 1)
                {
                    temp = new Mat();
                    input.CopyTo(temp);
                }
                else if (ch == 3)
                {
                    temp = new Mat();
                    Cv2.CvtColor(input, temp, ColorConversionCodes.BGR2GRAY);
                }
                else if (ch == 4)
                {
                    temp = new Mat();
                    Cv2.CvtColor(input, temp, ColorConversionCodes.BGRA2GRAY);
                }
                else
                {
                    gray = null;
                    return false;
                }

                if (temp.Depth() == MatType.CV_8U)
                {
                    gray = temp;       
                    temp = null;
                    return true;
                }

                gray = new Mat();
                if (temp.Depth() == MatType.CV_16U)
                {
                    temp.ConvertTo(gray, MatType.CV_8UC1, 1.0 / 256.0);
                }
                else
                {
                    temp.ConvertTo(gray, MatType.CV_8UC1);
                }

                return true;
            }
            finally
            {
                temp?.Dispose();
            }
        }


        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static unsafe void CopyGray8MatToPix8(Mat gray, PixData pixData)
        {
            // gray: CV_8UC1
            byte* srcBase = (byte*)gray.DataPointer;
            byte* dstBase = (byte*)pixData.Data;

            int w = gray.Width;
            int h = gray.Height;

            int srcStep = (int)gray.Step();
            int dstStep = pixData.WordsPerLine * 4;

            int words = w >> 2;       // w / 4
            int rem = w & 3;        // w % 4

            // На Windows почти всегда little-endian; на big-endian можно было бы делать прямую копию.
            // Но если хочешь 100% корректность — можно оставить branch:
            // if (!BitConverter.IsLittleEndian) { ... MemoryCopy width ... }

            for (int y = 0; y < h; y++)
            {
                byte* srcRowB = srcBase + y * srcStep;
                byte* dstRowB = dstBase + y * dstStep;

                uint* src32 = (uint*)srcRowB;
                uint* dst32 = (uint*)dstRowB;

                // 1) Быстрый путь: блоки по 4 байта
                for (int i = 0; i < words; i++)
                {
                    // src bytes: 0 1 2 3 -> dst bytes: 3 2 1 0 (canonical Leptonica order)
                    dst32[i] = BinaryPrimitives.ReverseEndianness(src32[i]);
                }

                // 2) Хвост (если ширина не кратна 4)
                if (rem != 0)
                {
                    uint* tailWord = dst32 + words;
                    *tailWord = 0u; // обнулим padding-байты

                    byte* dstTail = (byte*)tailWord;
                    byte* srcTail = srcRowB + (words << 2);

                    // пишем только rem байт, но в positions 3,2,1 (через x^3)
                    for (int r = 0; r < rem; r++)
                        dstTail[r ^ 3] = srcTail[r];
                }

                // (опционально) если хочешь 100% чистый padding до конца строки:
                // int usedWords = words + (rem != 0 ? 1 : 0);
                // for (int i = usedWords; i < pixData.WordsPerLine; i++) dst32[i] = 0;
            }
        }



        private static bool TryConvertMatToPix(Mat src, out Pix? pix)
        {
            pix = null;
            if (!TryEnsureToGray8U(src, out Mat? gray) || gray is null)
            {
                return false;
            }
            using (gray)
            {
                unsafe
                {
                    Pix? localPix = null;
                    try
                    {
                        localPix = Pix.Create(gray.Width, gray.Height, 8);

                        byte* srcBase = (byte*)gray.DataPointer;

                        PixData pixData = localPix.GetData();
                        CopyGray8MatToPix8(gray, pixData);
                        pix = localPix;
                        localPix = null; // ownership transferred
                        return true;
                    }
                    finally
                    {
                        localPix?.Dispose(); 
                    }
                }
            }
        }
    }
}
