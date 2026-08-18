using System.Windows;
using System.Windows.Controls;

namespace GrunflexPOS2.Views;

/// <summary>
/// Ajusta márgenes y columnas de CajaView según ancho de pantalla para mantener proporciones
/// similares entre monitores grandes y terminales POS más pequeños.
/// </summary>
internal static class CajaResponsiveHelper
{
    public static void Ajustar(
        Window window,
        Grid columnas,
        ContentControl mainContent,
        Border panelCobro,
        TextBlock totalText)
    {
        if (columnas.ColumnDefinitions.Count < 3)
            return;

        var w = window.ActualWidth;
        var sidebar = columnas.ColumnDefinitions[0];
        var center = columnas.ColumnDefinitions[1];
        var checkout = columnas.ColumnDefinitions[2];

        if (w >= 1500)
        {
            sidebar.MinWidth = 180;
            sidebar.MaxWidth = 300;
            center.MinWidth = 320;
            checkout.MinWidth = 260;
            checkout.MaxWidth = 420;
            mainContent.Margin = new Thickness(22, 18, 22, 18);
            panelCobro.Padding = new Thickness(18, 18, 18, 14);
            totalText.FontSize = 38;
        }
        else if (w >= 1280)
        {
            sidebar.MinWidth = 160;
            sidebar.MaxWidth = 260;
            center.MinWidth = 280;
            checkout.MinWidth = 230;
            checkout.MaxWidth = 360;
            mainContent.Margin = new Thickness(16, 16, 16, 16);
            panelCobro.Padding = new Thickness(14, 16, 14, 12);
            totalText.FontSize = 34;
        }
        else
        {
            sidebar.MinWidth = 140;
            sidebar.MaxWidth = 220;
            center.MinWidth = 220;
            checkout.MinWidth = 200;
            checkout.MaxWidth = 300;
            mainContent.Margin = new Thickness(10, 12, 10, 12);
            panelCobro.Padding = new Thickness(10, 12, 10, 10);
            totalText.FontSize = 30;
        }
    }
}
