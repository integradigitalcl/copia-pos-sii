namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Estado en runtime del modo multicaja API-only (sin UNC).
/// </summary>
public static class MulticajaRuntime
{
    public static bool UseApiOnlyClient { get; set; }

    /// <summary>
    /// Sync de catálogo/cajeros hacia SQLite sombra (pull desde API). Permite SaveChanges de
    /// <see cref="Models.Entities.Producto"/> sin confundirlo con ajustes locales de stock.
    /// </summary>
    public static bool AllowShadowCatalogSync { get; set; }

    public static IDisposable EnterShadowCatalogSyncScope()
    {
        AllowShadowCatalogSync = true;
        return new ShadowSyncScope();
    }

    private sealed class ShadowSyncScope : IDisposable
    {
        public void Dispose() => AllowShadowCatalogSync = false;
    }

    /// <summary>
    /// Configuración inválida para API-only (URL, CajaId, SharedSecret requerido, etc.).
    /// Bloquea ingresos/retiros y otras operaciones monetarias centralizadas en UI.
    /// </summary>
    public static bool BlockMonetaryForInvalidConfig { get; set; }

    /// <summary>El primer /health/live al arranque falló (la API podría estar levantando).</summary>
    public static bool StartupApiUnreachable { get; set; }

    public static int LastOfflineQueuePending { get; set; }
    public static long LastOfflineQueueBytes { get; set; }
    public static DateTime? LastRiskScanUtc { get; set; }
}
