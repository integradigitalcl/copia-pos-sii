using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.DTOs;
using GrunflexPOS2.Services.Multicaja;

namespace GrunflexPOS2.Services.DataSources;

/// <summary>
/// Decorador resiliente para <see cref="IProductoLookupService"/>:
///   - Elige dinámicamente entre API y SQLite vía <see cref="DataSourceFactory"/>.
///   - Si la opción primaria (API) falla, hace fallback transparente a SQLite local
///     para que el cajero NO se quede sin poder vender productos por una caída de red.
///
/// Es seguro como reemplazo drop-in del lookup actual: la firma es idéntica.
/// </summary>
public sealed class ResilientProductoLookupService : IProductoLookupService
{
    private readonly GrunflexDbContext _db;
    private readonly ConnectivityMonitor? _monitor;
    private readonly ProductoLocalLookupService _localFallback;

    public ResilientProductoLookupService(GrunflexDbContext db, ConnectivityMonitor? monitor)
    {
        _db = db;
        _monitor = monitor;
        _localFallback = new ProductoLocalLookupService(db);
    }

    public async Task<ProductoPosDto?> ObtenerPorCodigoBarrasAsync(string codigo, CancellationToken cancellationToken = default)
    {
        var primary = DataSourceFactory.CreateProductoLookup(_db, _monitor);
        try
        {
            var result = await primary.ObtenerPorCodigoBarrasAsync(codigo, cancellationToken).ConfigureAwait(false);
            if (result != null) return result;

            if (MulticajaRuntime.UseApiOnlyClient && AppConfig.Cargar().EsCajaAdicional)
                return null;

            if (primary is GrunflexPOS2.Services.API.ProductoApiService)
            {
                var fallback = await _localFallback.ObtenerPorCodigoBarrasAsync(codigo, cancellationToken)
                    .ConfigureAwait(false);
                return fallback;
            }

            return null;
        }
        catch
        {
            if (MulticajaRuntime.UseApiOnlyClient && AppConfig.Cargar().EsCajaAdicional)
                return null;

            try
            {
                return await _localFallback.ObtenerPorCodigoBarrasAsync(codigo, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }
}
