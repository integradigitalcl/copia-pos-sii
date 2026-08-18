using System.Windows;
using System.Windows.Media;
using GrunflexPOS2.Licensing;

namespace GrunflexPOS2.UI;

/// <summary>
/// Tema estructural del POS según licencia: azul (multicaja) o verde (mono caja).
/// </summary>
public static class BrandThemeService
{
    private static readonly BrandPalette Blue = new(
        Primary: ColorFromRgb(0x1D, 0x4E, 0xD8),
        Hover: ColorFromRgb(0x25, 0x63, 0xEB),
        Pressed: ColorFromRgb(0x1E, 0x40, 0xAF),
        Selection: ColorFromRgb(0xDB, 0xEA, 0xFE),
        GradientTop: ColorFromRgb(0x25, 0x63, 0xEB),
        GradientBottom: ColorFromRgb(0x1D, 0x4E, 0xD8),
        GradientHoverTop: ColorFromRgb(0x3B, 0x82, 0xF6),
        GradientHoverBottom: ColorFromRgb(0x25, 0x63, 0xEB),
        Border: ColorFromRgb(0x60, 0xA5, 0xFA),
        SecurityBg: ColorFromRgb(0xEF, 0xF6, 0xFF),
        SecurityBorder: ColorFromRgb(0xBF, 0xDB, 0xFE));

    private static readonly BrandPalette Green = new(
        Primary: ColorFromRgb(0x05, 0x96, 0x69),
        Hover: ColorFromRgb(0x10, 0xB9, 0x81),
        Pressed: ColorFromRgb(0x04, 0x7E, 0x57),
        Selection: ColorFromRgb(0xD1, 0xFA, 0xE5),
        GradientTop: ColorFromRgb(0x34, 0xD3, 0x99),
        GradientBottom: ColorFromRgb(0x10, 0xB9, 0x81),
        GradientHoverTop: ColorFromRgb(0x6E, 0xE7, 0xB7),
        GradientHoverBottom: ColorFromRgb(0x34, 0xD3, 0x99),
        Border: ColorFromRgb(0x6E, 0xE7, 0xB7),
        SecurityBg: ColorFromRgb(0xEC, 0xFD, 0xF5),
        SecurityBorder: ColorFromRgb(0xA7, 0xF3, 0xD0));

    private static bool _lastMulticaja = true;

    public static bool IsMulticajaTheme => _lastMulticaja;

    /// <summary>Azul si no hay licencia válida aún; luego según módulo Multicaja.</summary>
    public static void ApplyFromLicenseState()
    {
        var lic = App.LicenseState;
        var multicaja = !lic.IsValid || lic.Multicaja;
        Apply(multicaja);
    }

    public static void Apply(bool multicaja)
    {
        _lastMulticaja = multicaja;
        var palette = multicaja ? Blue : Green;

        if (System.Windows.Application.Current == null)
            return;

        var res = System.Windows.Application.Current.Resources;
        res["PrimaryColor"] = palette.Primary;
        res["PrimaryHoverColor"] = palette.Hover;
        res["PrimaryPressedColor"] = palette.Pressed;
        res["GridSelectionColor"] = palette.Selection;

        res["PrimaryBrush"] = Freeze(new SolidColorBrush(palette.Primary));
        res["PrimaryHoverBrush"] = Freeze(new SolidColorBrush(palette.Hover));
        res["PrimaryPressedBrush"] = Freeze(new SolidColorBrush(palette.Pressed));
        res["GridSelectionBrush"] = Freeze(new SolidColorBrush(palette.Selection));
        res["ConfigAccentBrush"] = Freeze(new SolidColorBrush(palette.Hover));
        res["CobroPrimaryBrush"] = Freeze(new SolidColorBrush(palette.Primary));
        res["LoginAccentBrush"] = Freeze(new SolidColorBrush(palette.Hover));
        res["BrandBorderBrush"] = Freeze(new SolidColorBrush(palette.Border));
        res["CobroPrimarySoftBrush"] = Freeze(new SolidColorBrush(palette.Selection));
        res["CobroSecurityBgBrush"] = Freeze(new SolidColorBrush(palette.SecurityBg));
        res["CobroSecurityBorderBrush"] = Freeze(new SolidColorBrush(palette.SecurityBorder));

        res["PrimaryGradientBrush"] = Freeze(CreateVerticalGradient(palette.GradientTop, palette.GradientBottom));
        res["PrimaryGradientHoverBrush"] = Freeze(CreateVerticalGradient(palette.GradientHoverTop, palette.GradientHoverBottom));
        res["LoginButtonGradientBrush"] = Freeze(CreateVerticalGradient(
            Lighten(palette.GradientHoverTop, 0.08),
            palette.GradientTop,
            palette.GradientBottom));
        res["BrandHeaderGradientBrush"] = Freeze(CreateDiagonalGradient(
            palette.GradientBottom,
            ColorFromRgb(0x7C, 0x3A, 0xED)));
        res["PremiumCardGradientBrush"] = Freeze(CreateDiagonalGradient(
            ColorFromRgb(0x7C, 0x3A, 0xED),
            ColorFromRgb(0x5B, 0x21, 0xB6),
            palette.Primary));
    }

    private static Color ColorFromRgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush Freeze(LinearGradientBrush brush)
    {
        if (brush.CanFreeze)
            brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush CreateVerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush(top, bottom, new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));
        return brush;
    }

    private static LinearGradientBrush CreateVerticalGradient(Color top, Color mid, Color bottom)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(0, 1)
        };
        brush.GradientStops.Add(new GradientStop(top, 0));
        brush.GradientStops.Add(new GradientStop(mid, 0.55));
        brush.GradientStops.Add(new GradientStop(bottom, 1));
        return brush;
    }

    private static LinearGradientBrush CreateDiagonalGradient(Color start, Color end)
    {
        var brush = new LinearGradientBrush(start, end, new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));
        return brush;
    }

    private static LinearGradientBrush CreateDiagonalGradient(Color start, Color mid, Color end)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1)
        };
        brush.GradientStops.Add(new GradientStop(start, 0));
        brush.GradientStops.Add(new GradientStop(mid, 0.45));
        brush.GradientStops.Add(new GradientStop(end, 1));
        return brush;
    }

    private static Color Lighten(Color c, double amount)
    {
        static byte L(byte v, double a) => (byte)Math.Clamp(v + (255 - v) * a, 0, 255);
        return Color.FromRgb(L(c.R, amount), L(c.G, amount), L(c.B, amount));
    }

    private readonly record struct BrandPalette(
        Color Primary,
        Color Hover,
        Color Pressed,
        Color Selection,
        Color GradientTop,
        Color GradientBottom,
        Color GradientHoverTop,
        Color GradientHoverBottom,
        Color Border,
        Color SecurityBg,
        Color SecurityBorder);
}
