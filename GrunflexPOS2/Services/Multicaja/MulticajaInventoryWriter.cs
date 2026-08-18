using GrunflexPOS2.Data;
using GrunflexPOS2.Services.Multicaja;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>Inventario y ventas centralizados vía API (principal y adicional multicaja).</summary>
public static class MulticajaInventoryWriter
{
    /// <summary>
    /// Inventario autoritativo en <c>InventoryStocks</c> (API). Aplica a ajustes manuales y commits de venta.
    /// </summary>
    public static bool ShouldUseCentralApi(AppConfig cfg)
    {
        App.LicenseState.RefreshFromStores();
        if (!App.LicenseState.Multicaja)
            return false;

        return MulticajaRuntime.UseApiOnlyClient || cfg.EsCajaPrincipal;
    }
}
