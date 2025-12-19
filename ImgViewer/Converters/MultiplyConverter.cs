using System;
using System.Globalization;
using System.Windows.Data;

namespace ImgViewer.Converters
{
    internal sealed class MultiplyConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null)
                return System.Windows.Data.Binding.DoNothing;

            if (!double.TryParse(value.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double original))
                return System.Windows.Data.Binding.DoNothing;

            double factor = 1.0;
            if (parameter != null)
            {
                double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out factor);
            }

            return original * factor;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
