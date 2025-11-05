//using ImgViewer.Interfaces;
//using System;
//using System.Collections.Generic;
//using System.ComponentModel;
//using System.Globalization;
//using System.Reflection;

//namespace ImgViewer.Models
//{
//    internal class Helper
//    {
//        private static T MapDictionaryToStruct<T>(Dictionary<string, object>? dict) where T : struct, new()
//        {
//            T result = new T();
//            if (dict == null || dict.Count == 0) return result;

//            Type type = typeof(T);
//            // Box the struct so PropertyInfo.SetValue mutates the boxed copy.
//            object boxed = (object)result;

//            foreach (var kv in dict)
//            {
//                if (kv.Key == null) continue;
//                // normalize key: remove spaces/hyphens/underscores and compare case-insensitively
//                string normKey = NormalizeKey(kv.Key);

//                // find matching property (ignore case)
//                PropertyInfo? prop = Array.Find(type.GetProperties(BindingFlags.Public | BindingFlags.Instance),
//                    p => p.CanWrite && string.Equals(NormalizeKey(p.Name), normKey, StringComparison.OrdinalIgnoreCase));

//                if (prop == null) continue;

//                object? raw = kv.Value;
//                if (raw == null) continue;

//                Type targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

//                try
//                {
//                    object? converted;

//                    // If already assignable, use directly
//                    if (targetType.IsInstanceOfType(raw))
//                    {
//                        converted = raw;
//                    }
//                    else if (targetType.IsEnum)
//                    {
//                        // Try parse enum from string or numeric
//                        if (raw is string sEnum)
//                        {
//                            converted = Enum.Parse(targetType, sEnum, ignoreCase: true);
//                        }
//                        else
//                        {
//                            converted = Enum.ToObject(targetType, raw);
//                        }
//                    }
//                    else
//                    {
//                        // Try TypeConverter first (handles string -> bool/int/etc.)
//                        var converter = TypeDescriptor.GetConverter(targetType);
//                        if (converter != null && converter.CanConvertFrom(raw.GetType()))
//                        {
//                            converted = converter.ConvertFrom(null, CultureInfo.InvariantCulture, raw);
//                        }
//                        else
//                        {
//                            // fallback to ChangeType (may throw)
//                            converted = Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
//                        }
//                    }

//                    prop.SetValue(boxed, converted);
//                }
//                catch
//                {
//                    // Conversion failed - ignore or add logging if you want:
//                    // Debug.WriteLine($"Failed to convert key={kv.Key} value={kv.Value} to {prop.PropertyType}");
//                }
//            }

//            // unbox updated struct
//            result = (T)boxed;
//            return result;

//            static string NormalizeKey(string s) =>
//                s.Replace("_", "", StringComparison.Ordinal)
//                 .Replace("-", "", StringComparison.Ordinal)
//                 .Replace(" ", "", StringComparison.Ordinal)
//                 .ToLowerInvariant();
//        }

//        public void ApplyCommandToCurrent(ProcessorCommands command, Dictionary<string, object>? parameters = null)
//        {
//            if (_currentImage == null) return;

//            switch (command)
//            {
//                case ProcessorCommands.Binarize:
//                    BinarizeAdaptive();
//                    break;

//                case ProcessorCommands.Deskew:
//                    // map provided dictionary into Deskewer.Parameters
//                    Deskewer.Parameters p = MapDictionaryToStruct<Deskewer.Parameters>(parameters);

//                    // Now you have p populated from the dictionary; use p as needed:
//                    // Example: call a Deskew method that accepts Parameters (adapt to your API)
//                    // var result = Deskewer.Deskew(_currentImage, p); // if you add such overload
//                    // Or use fields individually:
//                    double angle;
//                    if (p.byBorders)
//                    {
//                        angle = GetSkewAngleByBorders(_currentImage, p.cannyTresh1, p.cannyTresh2, p.morphKernel, p.minAreaFraction);
//                    }
//                    else
//                    {
//                        angle = GetSkewAngleByHough(_currentImage, p.cannyTresh1, p.cannyTresh2, p.houghTreshold, p.minLineLength, p.maxLineGap);
//                    }

//                    // then apply rotation / store results / etc.
//                    break;

//                    // other cases...
//            }
//        }

//        var dict = new Dictionary<string, object>
//        {
//            ["byBorders"] = true,
//            ["cannyTresh1"] = 40,
//            ["cannyTresh2"] = 120,
//            ["morphKernel"] = 5,
//            ["minAreaFraction"] = 0.2,
//            ["houghTreshold"] = 80,
//            ["minLineLength"] = 100
//        };
//        ApplyCommandToCurrent(ProcessorCommands.Deskew, dict);
//    }
//}
