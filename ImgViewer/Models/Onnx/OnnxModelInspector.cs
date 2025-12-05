using System.Diagnostics;
using Microsoft.ML.OnnxRuntime;

namespace ImgViewer.Models.Onnx
{
    internal static class OnnxModelInspector
    {
        public static void PrintModelInfo(string modelPath)
        {
            using var session = new InferenceSession(modelPath);

            Debug.WriteLine("=== ONNX MODEL INFO ===");
            Debug.WriteLine($"Model path: {modelPath}");

            Debug.WriteLine("Inputs:");
            foreach (var kv in session.InputMetadata)
            {
                var name = kv.Key;
                var md = kv.Value;
                Debug.WriteLine($"  Name: {name}");
                Debug.WriteLine($"    ElementType: {md.ElementType}");
                Debug.WriteLine($"    Dimensions: [{string.Join(", ", md.Dimensions)}]");
            }

            Debug.WriteLine("Outputs:");
            foreach (var kv in session.OutputMetadata)
            {
                var name = kv.Key;
                var md = kv.Value;
                Debug.WriteLine($"  Name: {name}");
                Debug.WriteLine($"    ElementType: {md.ElementType}");
                Debug.WriteLine($"    Dimensions: [{string.Join(", ", md.Dimensions)}]");
            }
        }
    }
}
