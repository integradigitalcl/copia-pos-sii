namespace GrunflexPOS2.Startup;

/// <summary>Accesos directos del instalador: --module ventas|inventario|reportes|…</summary>
public static class StartupNavigation
{
    public const string ArgName = "--module";

    public const string Ventas = "ventas";
    public const string Inventario = "inventario";
    public const string Reportes = "reportes";
    public const string ReporteVentasDelDia = "reporte-ventas-del-dia";
    public const string ReporteVentas7Dias = "reporte-ventas-7-dias";
    public const string ReporteVentasPorHora = "reporte-ventas-por-hora";
    public const string ReporteMetodosPago = "reporte-metodos-pago";
    public const string ReporteTopProductos = "reporte-top-productos";
    public const string ReporteEstadoNegocio = "reporte-estado-negocio";

    public static string? ParseModule(IReadOnlyList<string>? args)
    {
        if (args == null || args.Count == 0)
            return null;

        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], ArgName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]))
                return Normalize(args[i + 1]);

            return null;
        }

        return null;
    }

    public static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var value = raw.Trim().ToLowerInvariant();
        return value switch
        {
            Ventas or Inventario or Reportes or ReporteVentasDelDia or ReporteVentas7Dias
                or ReporteVentasPorHora or ReporteMetodosPago or ReporteTopProductos or ReporteEstadoNegocio => value,
            "reporte-panel" or "panel-de-control" => Reportes,
            _ => null
        };
    }

    public static bool OpensDashboard(string? module) =>
        module is Reportes or ReporteVentas7Dias or ReporteVentasPorHora
            or ReporteMetodosPago or ReporteTopProductos or ReporteEstadoNegocio;
}
