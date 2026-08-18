using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

internal static class MulticajaProductoResolve
{
    public static async Task<CommerceProducto?> PorLineaAsync(PosCommerceDbContext db, MulticajaVentaLineaDto line,
        CancellationToken ct)
    {
        var cod = line.CodigoBarras?.Trim();
        if (!string.IsNullOrEmpty(cod))
            return await db.Productos.FirstOrDefaultAsync(x => x.CodigoBarras == cod, ct);
        if (!string.IsNullOrWhiteSpace(line.Producto))
            return await db.Productos.FirstOrDefaultAsync(x => x.Nombre == line.Producto, ct);
        return null;
    }
}
