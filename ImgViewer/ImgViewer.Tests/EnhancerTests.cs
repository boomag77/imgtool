using ImgViewer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using System.Threading;

namespace ImgViewer.Tests
{
    [TestClass]
    public class EnhancerTests
    {
        [TestMethod]
        public void ApplyClahe_Throws_OnNull()
        {
            Assert.ThrowsException<ArgumentNullException>(() =>
                Enhancer.ApplyClahe(CancellationToken.None, null!));
        }

        [TestMethod]
        public void ApplyClahe_GrayNoMidtones_ReturnsSame()
        {
            using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0));
            using var result = Enhancer.ApplyClahe(CancellationToken.None, gray);

            using var diff = new Mat();
            Cv2.Absdiff(gray, result, diff);
            Assert.AreEqual(0, Cv2.CountNonZero(diff));
        }

        [TestMethod]
        public void ApplyClahe_GrayWithMidtones_ReturnsGray()
        {
            using var gray = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(128));
            using var result = Enhancer.ApplyClahe(CancellationToken.None, gray);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(gray.Rows, result.Rows);
            Assert.AreEqual(gray.Cols, result.Cols);
        }

        [TestMethod]
        public void ApplyClahe_BgrInput_ReturnsBgr()
        {
            using var bgr = new Mat(8, 12, MatType.CV_8UC3, Scalar.All(200));
            using var result = Enhancer.ApplyClahe(CancellationToken.None, bgr);

            Assert.AreEqual(MatType.CV_8UC3, result.Type());
            Assert.AreEqual(bgr.Rows, result.Rows);
            Assert.AreEqual(bgr.Cols, result.Cols);
        }

        [TestMethod]
        public void ApplyClahe_BgraInput_ReturnsBgr()
        {
            using var bgra = new Mat(8, 12, MatType.CV_8UC4, Scalar.All(200));
            using var result = Enhancer.ApplyClahe(CancellationToken.None, bgra);

            Assert.AreEqual(MatType.CV_8UC3, result.Type());
            Assert.AreEqual(bgra.Rows, result.Rows);
            Assert.AreEqual(bgra.Cols, result.Cols);
        }

        [TestMethod]
        public void ApplyClahe_InvalidType_Throws()
        {
            using var f32 = new Mat(5, 5, MatType.CV_32FC1, Scalar.All(0.5));
            Assert.ThrowsException<ArgumentException>(() =>
                Enhancer.ApplyClahe(CancellationToken.None, f32));
        }

        [TestMethod]
        public void ApplyClahe_Cancelled_Throws()
        {
            using var bgr = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(128));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                Enhancer.ApplyClahe(cts.Token, bgr));
        }

        [TestMethod]
        public void HomomorphicRetinex_Empty_ReturnsEmpty()
        {
            using var empty = new Mat();
            using var result = Enhancer.HomomorphicRetinex(CancellationToken.None, empty);
            Assert.IsTrue(result.Empty());
        }

        [TestMethod]
        public void HomomorphicRetinex_Color_Returns8U1()
        {
            using var bgr = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(180));
            using var result = Enhancer.HomomorphicRetinex(CancellationToken.None, bgr);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(bgr.Rows, result.Rows);
            Assert.AreEqual(bgr.Cols, result.Cols);
        }

        [TestMethod]
        public void HomomorphicRetinex_ReconstructExp_Returns8U1()
        {
            using var bgr = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(180));
            using var result = Enhancer.HomomorphicRetinex(
                CancellationToken.None,
                bgr,
                outputMode: Enhancer.RetinexOutputMode.ReconstructExp);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(bgr.Rows, result.Rows);
            Assert.AreEqual(bgr.Cols, result.Cols);
        }

        [TestMethod]
        public void HomomorphicRetinex_UseLabLFalse_Returns8U1()
        {
            using var bgr = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(180));
            using var result = Enhancer.HomomorphicRetinex(
                CancellationToken.None,
                bgr,
                useLabL: false);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(bgr.Rows, result.Rows);
            Assert.AreEqual(bgr.Cols, result.Cols);
        }

        [TestMethod]
        public void HomomorphicRetinex_RobustNormalizeFalse_Returns8U1()
        {
            using var bgr = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(180));
            using var result = Enhancer.HomomorphicRetinex(
                CancellationToken.None,
                bgr,
                robustNormalize: false);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(bgr.Rows, result.Rows);
            Assert.AreEqual(bgr.Cols, result.Cols);
        }

        [TestMethod]
        public void HomomorphicRetinex_FlatImage_ReturnsUniform()
        {
            using var gray = new Mat(16, 16, MatType.CV_8UC1, Scalar.All(120));
            using var result = Enhancer.HomomorphicRetinex(CancellationToken.None, gray);

            Cv2.MinMaxLoc(result, out double min, out double max);
            Assert.AreEqual(min, max, 1e-6);
        }

        [TestMethod]
        public void HomomorphicRetinex_Cancelled_Throws()
        {
            using var bgr = new Mat(16, 16, MatType.CV_8UC3, Scalar.All(180));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                Enhancer.HomomorphicRetinex(cts.Token, bgr));
        }

        [TestMethod]
        public void LevelsAndGamma8U_InvalidType_Throws()
        {
            using var bgr = new Mat(8, 8, MatType.CV_8UC3, Scalar.All(100));
            Assert.ThrowsException<ArgumentException>(() =>
                Enhancer.LevelsAndGamma8U(bgr, CancellationToken.None));
        }

        [TestMethod]
        public void LevelsAndGamma8U_Valid_Returns8U1()
        {
            using var gray = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(120));
            using var result = Enhancer.LevelsAndGamma8U(gray, CancellationToken.None);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(gray.Rows, result.Rows);
            Assert.AreEqual(gray.Cols, result.Cols);
        }

        [TestMethod]
        public void LevelsAndGamma8U_BlackWhiteInverted_StillWorks()
        {
            using var gray = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(120));
            using var result = Enhancer.LevelsAndGamma8U(
                gray,
                CancellationToken.None,
                blackPct: 95.0,
                whitePct: 1.0);

            Assert.AreEqual(MatType.CV_8UC1, result.Type());
            Assert.AreEqual(gray.Rows, result.Rows);
            Assert.AreEqual(gray.Cols, result.Cols);
        }

        [TestMethod]
        public void LevelsAndGamma8U_Cancelled_Throws()
        {
            using var gray = new Mat(8, 8, MatType.CV_8UC1, Scalar.All(120));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                Enhancer.LevelsAndGamma8U(gray, cts.Token));
        }
    }
}
