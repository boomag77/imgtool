using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using OpenCvSharp;
using System.Diagnostics;
using Size = OpenCvSharp.Size;

namespace ImgViewer.Models.Onnx
{
    public class DocBoundaryModel : IDisposable
    {
        private readonly InferenceSession _session;
        private readonly string _inputName;
        private readonly string _outputName;

        private readonly int _channels;
        private readonly int _height;
        private readonly int _width;
        private readonly CancellationToken _token;

        public DocBoundaryModel(CancellationToken token, string modelPath)
        {
            _token = token;
            _session = new InferenceSession(modelPath);

            var inMeta = _session.InputMetadata.First();
            _inputName = inMeta.Key; // "image"
            var inDims = inMeta.Value.Dimensions; // [1, 3, 640, 1280]

            _channels = inDims[1];
            _height = inDims[2];
            _width = inDims[3];

            var outMeta = _session.OutputMetadata.First();
            _outputName = outMeta.Key; // "mask"
        }

        public void Dispose() => _session.Dispose();

        /// <summary>
        /// BGR Mat -> маска документа (0/255, CV_8UC1), растянутая до размера src.
        /// </summary>
        public Mat PredictMask(Mat srcBgr, int cropLevel = 62)
        {
            if (srcBgr.Empty())
                throw new ArgumentException("srcBgr is empty", nameof(srcBgr));

            // 1) BGR -> RGB
            Mat rgb = new Mat();
            Cv2.CvtColor(srcBgr, rgb, ColorConversionCodes.BGR2RGB);

            // 2) Resize к размеру модели
            Mat resized = new Mat();
            Cv2.Resize(rgb, resized, new Size(_width, _height));

            // 3) float32 [0..1]
            resized.ConvertTo(resized, MatType.CV_32FC3, 1.0 / 255.0f);

            // 4) HWC -> CHW, без unsafe и указателей
            var tensor = new DenseTensor<float>(new[] { 1, _channels, _height, _width });

            for (int y = 0; y < _height; y++)
            {
                for (int x = 0; x < _width; x++)
                {
                    // OpenCV хранит как HWC, Vec3f = (R,G,B)
                    Vec3f v = resized.At<Vec3f>(y, x);
                    tensor[0, 0, y, x] = v.Item0; // R
                    tensor[0, 1, y, x] = v.Item1; // G
                    tensor[0, 2, y, x] = v.Item2; // B
                }
            }

            var inputs = new List<NamedOnnxValue>
            {
                NamedOnnxValue.CreateFromTensor(_inputName, tensor)
            };

            using var results = _session.Run(inputs);
            var output = results.First(r => r.Name == _outputName).AsTensor<float>();

            // Выход: [1, 2, H, W] = [1, 2, 640, 1280]
            var dims = output.Dimensions;
            int outC = dims[1];
            int outH = dims[2];
            int outW = dims[3];

            float[] outData = output.ToArray();

            Mat maskSmall = new Mat(outH, outW, MatType.CV_8UC1);

            // Насколько агрессивно отрезаем бордюры:
            // 0.5f – мягко, 0.7f – обычно хорошо, 0.8–0.9f – агрессивно.
            // map detection level 0..100% to threshold 0.3..0.95
            if (cropLevel < 0) cropLevel = 0;
            if (cropLevel > 100) cropLevel = 100;
            float t = cropLevel / 100f;
            float level = 0.3f + t * (0.95f - 0.3f);

            float docProbThreshold = level;

            for (int y = 0; y < outH; y++)
            {
                for (int x = 0; x < outW; x++)
                {
                    int pixelIndex = y * outW + x;

                    // logits двух классов
                    float logBg = outData[0 * outH * outW + pixelIndex];
                    float logDoc = outData[1 * outH * outW + pixelIndex];

                    // численно устойчивый softmax по 2 классам
                    float maxLog = logBg > logDoc ? logBg : logDoc;

                    float eBg = (float)Math.Exp(logBg - maxLog);
                    float eDoc = (float)Math.Exp(logDoc - maxLog);
                    float sum = eBg + eDoc;
                    float pDoc = sum > 0 ? (eDoc / sum) : 0.0f;

                    byte value = (byte)(pDoc > docProbThreshold ? 255 : 0);
                    maskSmall.Set(y, x, value);
                }
            }

            // Растянуть маску до размера исходного изображения
            Mat mask = new Mat();
            Cv2.Resize(maskSmall, mask, srcBgr.Size(), interpolation: InterpolationFlags.Nearest);

            return mask;
        }
    }
}
