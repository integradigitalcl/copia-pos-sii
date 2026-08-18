using GrunflexPOS2.Services.DTOs;

namespace GrunflexPOS2.Services;

/// <summary>Resolución de productos para venta (catálogo local; API solo si se añade otra implementación).</summary>
public interface IProductoLookupService
{
    Task<ProductoPosDto?> ObtenerPorCodigoBarrasAsync(string codigo, CancellationToken cancellationToken = default);
}
