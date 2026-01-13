using ImgViewer.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenCvSharp;
using System;
using System.Diagnostics;
using System.Threading;

namespace ImgViewer.Tests
{
    [TestClass]
    public class LeakSmokeTests
    {
        [TestMethod]
        public void Mat_Dispose_SetsIsDisposed()
        {
            var mat = new Mat(10, 10, MatType.CV_8UC1, Scalar.All(0));
            Assert.IsFalse(mat.IsDisposed);
            mat.Dispose();
            Assert.IsTrue(mat.IsDisposed);
        }

        [TestMethod]
        [TestCategory("LeakSmoke")]
        public void LeakSmoke_Enhancer_Retinex_DoesNotGrowUnbounded()
        {
            using var src = new Mat(256, 256, MatType.CV_8UC3, Scalar.All(128));
            long before = GetPrivateBytes();

            for (int i = 0; i < 50; i++)
            {
                using var result = Enhancer.HomomorphicRetinex(CancellationToken.None, src);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long after = GetPrivateBytes();
            Assert.IsTrue(after - before < 200L * 1024 * 1024);
        }

        [TestMethod]
        [TestCategory("LeakSmoke")]
        public void LeakSmoke_PunchHoleRemover_DoesNotGrowUnbounded()
        {
            using var src = new Mat(200, 300, MatType.CV_8UC3, Scalar.All(255));
            var specs = new System.Collections.Generic.List<PunchSpec>
            {
                new PunchSpec
                {
                    Shape = PunchShape.Rect,
                    RectSize = new OpenCvSharp.Size(20, 10),
                    Density = 1.0,
                    SizeToleranceFraction = 0.2
                }
            };

            long before = GetPrivateBytes();

            for (int i = 0; i < 30; i++)
            {
                using var result = global::PunchHoleRemover.RemovePunchHoles(
                    CancellationToken.None,
                    src,
                    specs,
                    roundness: 30,
                    fr: 0.5,
                    offsetTop: 20,
                    offsetBottom: 0,
                    offsetLeft: 0,
                    offsetRight: 0);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long after = GetPrivateBytes();
            Assert.IsTrue(after - before < 200L * 1024 * 1024);
        }

        [TestMethod]
        [TestCategory("LeakSmoke")]
        public void LeakSmoke_Binarizer_DoesNotGrowUnbounded()
        {
            using var src = new Mat(256, 256, MatType.CV_8UC3, Scalar.All(128));
            var p = BinarizeParameters.Default;

            long before = GetPrivateBytes();

            for (int i = 0; i < 40; i++)
            {
                using var t = Binarizer.Binarize(src, BinarizeMethod.Threshold, p);
                p.Method = BinarizeMethod.Adaptive;
                using var a = Binarizer.Binarize(src, BinarizeMethod.Adaptive, p);
                p.Method = BinarizeMethod.Sauvola;
                using var s = Binarizer.Binarize(src, BinarizeMethod.Sauvola, p);
            }

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            long after = GetPrivateBytes();
            Assert.IsTrue(after - before < 200L * 1024 * 1024);
        }

        private static long GetPrivateBytes()
        {
            using var proc = Process.GetCurrentProcess();
            return proc.PrivateMemorySize64;
        }
    }
}
