using System;
using System.Net.Http;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Multicaja;

namespace GrunflexPOS2.Services.DataSources;

/// <summary>
/// Factory central para construir <see cref="IProductoLookupService"/> según contexto
/// (Fase 2.4).
///
/// Decide entre:
///   - <see cref="ProductoApiService"/> (HTTP a la caja principal) — cuando esto es un cliente
///     multicaja y la conectividad lo permite. Reduce locking SMB y centraliza validaciones.
///   - <see cref="ProductoLocalLookupService"/> (acceso directo a SQLite/UNC) — default seguro.
///
/// Estrategia: <see cref="CreateProductoLookup"/> se llama por operación (NO cachear) para
/// reaccionar a cambios de conectividad inmediatamente. La creación es barata (HttpClient
/// es compartido y el DbContext ya existe).
/// </summary>
public static class DataSourceFactory
{
    private static readonly Lazy<HttpClient> SharedHttp = new(() =>
        new HttpClient { Timeout = TimeSpan.FromSeconds(6) });

    /// <summary>
    /// Devuelve el servicio de lookup adecuado. Si está disponible la API en multicaja Online
    /// → ProductoApiService (HTTP). Sino → ProductoLocalLookupService (SQLite/UNC directo).
    /// </summary>
    public static IProductoLookupService CreateProductoLookup(
        GrunflexDbContext db,
        ConnectivityMonitor? monitor,
        AppConfig? cfg = null)
    {
        cfg ??= AppConfig.Cargar();
        if (ShouldUseApi(cfg, monitor))
        {
            ConfigureHttpClient(SharedHttp.Value, cfg);
            return new ProductoApiService(SharedHttp.Value);
        }
        return new ProductoLocalLookupService(db);
    }

    /// <summary>
    /// Indica si el contexto actual sugiere usar la API en lugar de SQLite directo.
    /// Hoy: solo cuando <see cref="AppConfig.EsCajaAdicional"/> (cliente explícito o UNC sin rol)
/// Y la API está respondiendo Online/Degraded.
    /// </summary>
    public static bool ShouldUseApi(AppConfig cfg, ConnectivityMonitor? monitor)
    {
        // TerminalRole=client o heurística legacy (UNC sin rol): no usar OR con TieneConexionUnc
        // solo, porque un servidor podría tener UNC y quedaría mal clasificado.
        if (MulticajaRuntime.UseApiOnlyClient && cfg.EsCajaAdicional)
            return true;

        var esMulticajaCliente = cfg.EsCajaAdicional;
        if (!esMulticajaCliente) return false;
        if (monitor == null) return false;
        return monitor.State == ConnectivityState.Online || monitor.State == ConnectivityState.Degraded;
    }

    private static void ConfigureHttpClient(HttpClient http, AppConfig cfg)
    {
        try
        {
            var baseUrl = (cfg.ApiBaseUrl ?? "").Trim();
            if (string.IsNullOrEmpty(baseUrl)) return;
            if (!baseUrl.EndsWith("/")) baseUrl += "/";
            if (http.BaseAddress == null || http.BaseAddress.ToString() != baseUrl)
                http.BaseAddress = new Uri(baseUrl);
        }
        catch { /* dejar como esté */ }

        try
        {
            http.DefaultRequestHeaders.Remove("X-Grunflex-Multicaja-Key");
            var secret = (cfg.MulticajaSharedSecret ?? string.Empty).Trim();
            if (!string.IsNullOrEmpty(secret))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Multicaja-Key", secret);

            http.DefaultRequestHeaders.Remove("X-Grunflex-Caja-Id");
            if (Guid.TryParse(cfg.CajaId, out var cajaGuid))
                http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Caja-Id", cajaGuid.ToString());
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Terminal", Environment.MachineName);
        }
        catch { /* */ }
    }
}
