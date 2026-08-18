using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace GrunflexPOS2.Views;

/// <summary>
/// Limita la altura de tablas según el viewport real (ventana o área visible),
/// evitando grids que se estiran desproporcionadamente en pantallas grandes.
/// </summary>
internal static class PosGridHeightHelper
{
    public static void AjustarAlturaGrid(
        FrameworkElement host,
        DataGrid grid,
        double reservarSuperior = 280,
        double minAltura = 160,
        double maxFraccionViewport = 0.42)
    {
        var viewport = ObtenerAlturaViewport(host);
        if (viewport <= 0)
            return;

        var tope = viewport * maxFraccionViewport;
        var restante = viewport - reservarSuperior;
        var altura = Math.Min(tope, restante);

        if (host.ActualHeight > 0)
        {
            var enHost = host.ActualHeight - reservarSuperior;
            if (enHost > 0)
                altura = Math.Min(altura, enHost);
        }

        grid.MaxHeight = Math.Max(minAltura, altura);
    }

    public static double ObtenerAlturaViewport(FrameworkElement element)
    {
        for (var current = element as DependencyObject;
             current != null;
             current = VisualTreeHelper.GetParent(current))
        {
            switch (current)
            {
                case ScrollViewer sv when sv.ActualHeight > 0:
                    return sv.ViewportHeight > 0 ? sv.ViewportHeight : sv.ActualHeight;
                case Window w when w.ActualHeight > 0:
                    return w.ActualHeight;
            }
        }

        if (element.ActualHeight > 0)
            return element.ActualHeight;

        return SystemParameters.WorkArea.Height * 0.72;
    }
}
