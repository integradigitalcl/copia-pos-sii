using System.Globalization;
using System.Windows.Data;

namespace Grunflex.LicenseIssuer.Converters;

public sealed class PercentToSparklineBarHeightConverter : IValueConverter
{
    public double MaxHeight { get; set; } = 64;

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var max = MaxHeight;
        if (parameter is string ps && double.TryParse(ps, NumberStyles.Float, CultureInfo.InvariantCulture, out var ph))
            max = ph;

        var pct = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };
        pct = Math.Clamp(pct, 0, 100);
        return Math.Max(2, max * pct / 100.0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
