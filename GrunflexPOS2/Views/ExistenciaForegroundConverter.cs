using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace GrunflexPOS2.Views
{
    /// <summary>Rojo = sin stock, naranja = bajo respecto a mínimo, verde = OK.</summary>
    public sealed class ExistenciaForegroundConverter : IMultiValueConverter
    {
        private static readonly SolidColorBrush Verde = new(Color.FromRgb(22, 163, 74));
        private static readonly SolidColorBrush Rojo = new(Color.FromRgb(220, 38, 38));
        private static readonly SolidColorBrush Naranja = new(Color.FromRgb(234, 88, 12));

        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values.Any(v => v == DependencyProperty.UnsetValue || v == null))
                return Verde;

            int stock = values[0] is int s ? s : System.Convert.ToInt32(values[0], culture);
            int invMin = values[1] is int m ? m : System.Convert.ToInt32(values[1], culture);

            if (stock == 0)
                return Rojo;
            if (invMin > 0 && stock > 0 && stock <= invMin)
                return Naranja;
            return Verde;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
