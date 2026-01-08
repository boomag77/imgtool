using ImgViewer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;

namespace ImgViewer.Tests
{
    [TestClass]
    public class BinarizerTests
    {
        [TestMethod]
        public void Binarize_Throws_OnNull()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                Binarizer.Binarize(null, BinarizeMethod.Threshold, BinarizeParameters.Default));
        }

        [TestMethod]
        public void Binarize_Throws_OnEmpty()
        {
            using var empty = new Mat();
            Assert.ThrowsException<InvalidOperationException>(() =>
                Binarizer.Binarize(empty, BinarizeMethod.Threshold, BinarizeParameters.Default));
        }

        [TestMethod]
        public void Binarize_Threshold_GrayInput_ReturnsBinary()
        {
            using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(128));
            using var result = Binarizer.Binarize(gray, BinarizeMethod.Threshold, BinarizeParameters.Default);
            Assert.IsNotNull(result);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
        }

        [TestMethod]
        public void Binarize_Adaptive_ColorInput_ConvertsToGray()
        {
            using var color = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(200));
            using var result = Binarizer.Binarize(color, BinarizeMethod.Adaptive, BinarizeParameters.Default);
            Assert.IsNotNull(result);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
        }

        [TestMethod]
        public void Binarize_Sauvola_ColorInput_ConvertsToGray()
        {
            var p = BinarizeParameters.Default;
            p.Method = BinarizeMethod.Sauvola;
            using var color = new Mat(10, 10, MatType.CV_8UC3, Scalar.All(200));
            using var result = Binarizer.Binarize(color, BinarizeMethod.Sauvola, p);
            Assert.IsNotNull(result);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
        }

        [TestMethod]
        public void Binarize_Threshold_TinyImage_1x1_Works()
        {
            using var gray = new Mat(1, 1, MatType.CV_8UC1, Scalar.All(128));
            using var result = Binarizer.Binarize(gray, BinarizeMethod.Threshold, BinarizeParameters.Default);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(1, result.Rows);
            Assert.AreEqual(1, result.Cols);
        }

        [TestMethod]
        public void Binarize_Adaptive_TinyImage_2x2_ClampsBlockSize()
        {
            var p = BinarizeParameters.Default;
            p.Method = BinarizeMethod.Adaptive;
            p.BlockSize = 2; // even and too small
            using var gray = new Mat(2, 2, MatType.CV_8UC1, Scalar.All(120));
            using var result = Binarizer.Binarize(gray, BinarizeMethod.Adaptive, p);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(2, result.Rows);
            Assert.AreEqual(2, result.Cols);
        }

        [TestMethod]
        public void Binarize_Sauvola_TinyImage_1x1_Works()
        {
            var p = BinarizeParameters.Default;
            p.Method = BinarizeMethod.Sauvola;
            p.SauvolaWindowSize = 2; // even, should adjust
            using var gray = new Mat(1, 1, MatType.CV_8UC1, Scalar.All(200));
            using var result = Binarizer.Binarize(gray, BinarizeMethod.Sauvola, p);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(1, result.Rows);
            Assert.AreEqual(1, result.Cols);
        }

        [TestMethod]
        public void Binarize_Adaptive_BgraInput_ConvertsToGray()
        {
            using var bgra = new Mat(10, 10, MatType.CV_8UC4, Scalar.All(220));
            using var result = Binarizer.Binarize(bgra, BinarizeMethod.Adaptive, BinarizeParameters.Default);
            Assert.AreEqual(MatType.CV_8UC1, result.Type());
        }

        [TestMethod]
        public void Binarize_Threshold_ExtremeValues_Work()
        {
            var p = BinarizeParameters.Default;
            using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0));
            using var result0 = Binarizer.Binarize(gray, BinarizeMethod.Threshold, p);
            Assert.AreEqual(MatType.CV_8UC1, result0.Type());

            using var gray2 = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(255));
            using var result255 = Binarizer.Binarize(gray2, BinarizeMethod.Threshold, p);
            Assert.AreEqual(MatType.CV_8UC1, result255.Type());
        }
    }
}
