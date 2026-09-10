namespace GrunflexPOS.API.Services;

/// <summary>
/// Impacto en el cajón de efectivo para cuadratura de sesión multicaja.
/// TotalVentas de la sesión representa efectivo (no el total de todos los medios de pago).
/// </summary>
internal static class MulticajaCashImpact
{
    public static decimal ForSale(string? metodoPago, decimal total, decimal? montoEfectivo)
    {
        var method = (metodoPago ?? string.Empty).Trim();
        if (method.Equals("Efectivo", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0m, total);
        if (method.Equals("Mixto", StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(montoEfectivo ?? 0m, 0m, Math.Max(0m, total));
        return 0m;
    }

    public static decimal ForRefund(
        string? metodoPago, decimal saleTotalBefore, decimal refundAmount, decimal? montoEfectivo)
    {
        if (refundAmount <= 0 || saleTotalBefore <= 0)
            return 0m;
        var cashBase = ForSale(metodoPago, saleTotalBefore, montoEfectivo);
        if (cashBase <= 0)
            return 0m;
        return Math.Round(cashBase * refundAmount / saleTotalBefore, 2, MidpointRounding.AwayFromZero);
    }
}
