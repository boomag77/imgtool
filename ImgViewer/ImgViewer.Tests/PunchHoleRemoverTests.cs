using ImgViewer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using System.Collections.Generic;
using System.Threading;

namespace ImgViewer.Tests
{
    [TestClass]
    public class PunchHoleRemoverTests
    {
        [TestMethod]
        public void RemovePunchHoles_Null_ReturnsNull()
        {
            var result = global::PunchHoleRemover.RemovePunchHoles(
                CancellationToken.None,
                null!,
                new List<PunchSpec>(),
                roundness: 30,
                fr: 0.8,
                offsetTop: 10,
                offsetBottom: 10,
                offsetLeft: 10,
                offsetRight: 10);

            Assert.IsNull(result);
        }

        [TestMethod]
        public void RemovePunchHoles_Empty_ReturnsSame()
        {
            using var empty = new Mat();
            var result = global::PunchHoleRemover.RemovePunchHoles(
                CancellationToken.None,
                empty,
                new List<PunchSpec>(),
                roundness: 30,
                fr: 0.8,
                offsetTop: 10,
                offsetBottom: 10,
                offsetLeft: 10,
                offsetRight: 10);

            Assert.AreSame(empty, result);
        }

        [TestMethod]
        public void RemovePunchHoles_NoSpecs_ReturnsClone()
        {
            using var gray = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(200));
            var result = global::PunchHoleRemover.RemovePunchHoles(
                CancellationToken.None,
                gray,
                new List<PunchSpec>(),
                roundness: 30,
                fr: 0.8,
                offsetTop: 10,
                offsetBottom: 10,
                offsetLeft: 10,
                offsetRight: 10);

            Assert.IsNotNull(result);
            Assert.AreNotSame(gray, result);
            Assert.AreEqual(gray.Type(), result.Type());
            Assert.AreEqual(gray.Rows, result.Rows);
            Assert.AreEqual(gray.Cols, result.Cols);
            result.Dispose();
        }

        [TestMethod]
        public void RemovePunchHoles_Circle_NoNonZeroInSearchMask_ReturnsClone()
        {
            using var gray = new Mat(40, 40, MatType.CV_8UC1, Scalar.All(0));
            var specs = new List<PunchSpec>
            {
                new PunchSpec
                {
                    Shape = PunchShape.Circle,
                    Diameter = 10,
                    Density = 1.0,
                    SizeToleranceFraction = 0.4
                }
            };

            var result = global::PunchHoleRemover.RemovePunchHoles(
                CancellationToken.None,
                gray,
                specs,
                roundness: 30,
                fr: 0.8,
                offsetTop: 10,
                offsetBottom: 0,
                offsetLeft: 10,
                offsetRight: 0);

            Assert.IsNotNull(result);
            Assert.AreNotSame(gray, result);
            Assert.AreEqual(gray.Type(), result.Type());
            result.Dispose();
        }

        [TestMethod]
        public void RemovePunchHoles_RectDetection_InpaintsAndReturnsBgr()
        {
            using var src = new Mat(60, 80, MatType.CV_8UC3, Scalar.All(255));
            var rect = new Rect(10, 5, 20, 10);
            Cv2.Rectangle(src, rect, Scalar.Black, -1);

            var specs = new List<PunchSpec>
            {
                new PunchSpec
                {
                    Shape = PunchShape.Rect,
                    RectSize = new OpenCvSharp.Size(20, 10),
                    Density = 1.0,
                    SizeToleranceFraction = 0.2
                }
            };

            var result = global::PunchHoleRemover.RemovePunchHoles(
                CancellationToken.None,
                src,
                specs,
                roundness: 30,
                fr: 0.5,
                offsetTop: 20,
                offsetBottom: 0,
                offsetLeft: 0,
                offsetRight: 0);

            Assert.IsNotNull(result);
            Assert.AreEqual(MatType.CV_8UC3, result.Type());
            Assert.AreEqual(src.Rows, result.Rows);
            Assert.AreEqual(src.Cols, result.Cols);
            result.Dispose();
        }

        [TestMethod]
        public void RemovePunchHoles_Cancelled_Throws()
        {
            using var src = new Mat(20, 20, MatType.CV_8UC1, Scalar.All(200));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            Assert.ThrowsException<OperationCanceledException>(() =>
                global::PunchHoleRemover.RemovePunchHoles(
                    cts.Token,
                    src,
                    new List<PunchSpec>(),
                    roundness: 30,
                    fr: 0.8,
                    offsetTop: 10,
                    offsetBottom: 10,
                    offsetLeft: 10,
                    offsetRight: 10));
        }
    }
}
