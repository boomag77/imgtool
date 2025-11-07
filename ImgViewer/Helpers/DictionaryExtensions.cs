using System;
using System.Collections.Generic;
using System.Globalization;

public static class DictionaryExtensions
{
    /// <summary>
    /// Try to read a boolean-like value from dictionary safely.
    /// Accepts: bool, "true"/"false", "1"/"0", numeric types (int/long/double/float/decimal),
    /// "yes"/"no", "on"/"off". Returns false from Try if key missing or conversion fails.
    /// </summary>
    public static bool TryGetBool(this IDictionary<string, object?> dict, string key, out bool value)
    {
        value = false;
        if (dict == null) return false;
        if (!dict.TryGetValue(key, out var raw) || raw == null) return false;

        // direct bool
        if (raw is bool b)
        {
            value = b;
            return true;
        }

        // strings
        if (raw is string s)
        {
            s = s.Trim();
            if (bool.TryParse(s, out var rb))
            {
                value = rb;
                return true;
            }

            if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var iv))
            {
                value = iv != 0;
                return true;
            }

            switch (s.ToLowerInvariant())
            {
                case "yes":
                case "y":
                case "on":
                    value = true;
                    return true;
                case "no":
                case "n":
                case "off":
                    value = false;
                    return true;
            }

            return false;
        }

        // numeric types
        switch (raw)
        {
            case int i:
                value = i != 0;
                return true;
            case long l:
                value = l != 0L;
                return true;
            case short sh:
                value = sh != 0;
                return true;
            case byte by:
                value = by != 0;
                return true;
            case double d:
                if (double.IsNaN(d)) return false;
                value = Math.Abs(d) > double.Epsilon && d != 0.0;
                return true;
            case float f:
                value = Math.Abs(f) > float.Epsilon && f != 0f;
                return true;
            case decimal dec:
                value = dec != 0m;
                return true;
        }

        // fallback for IConvertible
        if (raw is IConvertible ic)
        {
            try
            {
                value = Convert.ToBoolean(ic, CultureInfo.InvariantCulture);
                return true;
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Get boolean or default.
    /// </summary>
    public static bool GetBoolOrDefault(this IDictionary<string, object?> dict, string key, bool defaultValue = false)
        => dict.TryGetBool(key, out var v) ? v : defaultValue;
}
