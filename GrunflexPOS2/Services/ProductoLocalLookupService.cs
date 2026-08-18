using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services;

/// <summary>Búsqueda de productos por código de barras en la base local (modo sin API).</summary>
public sealed class ProductoLocalLookupService : IProductoLookupService
{
    private readonly GrunflexDbContext _db;

    public ProductoLocalLookupService(GrunflexDbContext db)
    {
        _db = db;
    }

    public async Task<ProductoPosDto?> ObtenerPorCodigoBarrasAsync(string codigo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return null;

        var c = codigo.Trim();
        var p = await _db.Productos
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CodigoBarras == c, cancellationToken)
            .ConfigureAwait(false);

        if (p == null)
            return null;

        return new ProductoPosDto
        {
            Id = p.Id,
            CodigoBarras = p.CodigoBarras,
            Nombre = p.Nombre,
            Precio = p.Precio,
            Stock = p.Stock
        };
    }
}
