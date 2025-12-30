using OpenCvSharp;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ImgViewer.Models
{
    internal static class MatPool
    {
        private static readonly ConcurrentDictionary<(int rows, int cols, MatType type), ConcurrentBag<Mat>> _pool = new();

        public static Mat Rent(int rows, int cols, MatType type)
        {
            var key = (rows, cols, type);
            if (_pool.TryGetValue(key, out var bag) && bag.TryTake(out var mat))
            {
                if (!mat.Empty() && mat.Rows == rows && mat.Cols == cols && mat.Type() == type)
                    return mat;

                mat.Dispose();
            }

            return new Mat(rows, cols, type);
        }

        public static void Return(Mat mat)
        {
            if (mat == null || mat.IsDisposed) return;
            var key = (mat.Rows, mat.Cols, mat.Type());
            var bag = _pool.GetOrAdd(key, _ => new ConcurrentBag<Mat>());
            bag.Add(mat);
        }
    }
}