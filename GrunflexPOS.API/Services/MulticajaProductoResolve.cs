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
        var existente = await BuscarAsync(db, line, ct);
        if (existente is not null)
            return existente;

        var creado = CrearDesdeLinea(line);
        if (creado is null)
            return null;

        db.Productos.Add(creado);
        await db.SaveChangesAsync(ct);
        return creado;
    }

    public static async Task<CommerceProducto?> PorComponenteAsync(
        PosCommerceDbContext db, MulticajaVentaComponenteDto comp, CancellationToken ct)
    {
        if (comp.ProductoId is > 0)
        {
            var porId = await db.Productos.FirstOrDefaultAsync(x => x.Id == comp.ProductoId, ct);
            if (porId is not null)
                return porId;
        }

        var cod = comp.CodigoBarras?.Trim();
        if (!string.IsNullOrEmpty(cod))
        {
            var porCodigo = await db.Productos.FirstOrDefaultAsync(x => x.CodigoBarras == cod, ct);
            if (porCodigo is not null)
                return porCodigo;
        }

        if (!string.IsNullOrWhiteSpace(comp.Producto))
        {
            var name = comp.Producto.Trim();
            return await db.Productos.FirstOrDefaultAsync(x => x.Nombre == name, ct);
        }

        return null;
    }

    /// <summary>Publica el catálogo de una caja en la base central (crea o actualiza por código de barras).</summary>
    public static async Task<int> UpsertLoteAsync(PosCommerceDbContext db,
        IReadOnlyCollection<MulticajaProductoDto> items, CancellationToken ct)
    {
        var upserted = 0;
        foreach (var item in items)
        {
            var code = (item.CodigoBarras ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(item.Nombre))
                continue;

            var actual = await db.Productos.FirstOrDefaultAsync(x => x.CodigoBarras == code, ct);
            if (actual is null)
            {
                db.Productos.Add(new CommerceProducto
                {
                    Nombre = item.Nombre.Trim(),
                    CodigoBarras = code,
                    Precio = item.Precio,
                    Costo = item.Costo,
                    PrecioMayoreo = item.PrecioMayoreo,
                    Stock = Math.Max(0, item.Stock),
                    InvMinimo = item.InvMinimo,
                    InvMaximo = item.InvMaximo,
                    TipoVenta = string.IsNullOrWhiteSpace(item.TipoVenta) ? "Unidad" : item.TipoVenta.Trim(),
                    Departamento = string.IsNullOrWhiteSpace(item.Departamento) ? "General" : item.Departamento.Trim(),
                    CategoriaId = item.CategoriaId
                });
                upserted++;
                continue;
            }

            actual.Nombre = item.Nombre.Trim();
            actual.Precio = item.Precio;
            actual.Costo = item.Costo;
            actual.PrecioMayoreo = item.PrecioMayoreo;
            actual.InvMinimo = item.InvMinimo;
            actual.InvMaximo = item.InvMaximo;
            if (!string.IsNullOrWhiteSpace(item.TipoVenta))
                actual.TipoVenta = item.TipoVenta.Trim();
            if (!string.IsNullOrWhiteSpace(item.Departamento))
                actual.Departamento = item.Departamento.Trim();
            upserted++;
        }

        if (upserted > 0)
            await db.SaveChangesAsync(ct);

        return upserted;
    }

    private static async Task<CommerceProducto?> BuscarAsync(PosCommerceDbContext db, MulticajaVentaLineaDto line,
        CancellationToken ct)
    {
        if (line.ProductoId is > 0)
        {
            var porId = await db.Productos.FirstOrDefaultAsync(x => x.Id == line.ProductoId, ct);
            if (porId is not null)
                return porId;
        }

        var cod = line.CodigoBarras?.Trim();
        if (!string.IsNullOrEmpty(cod))
        {
            var porCodigo = await db.Productos.FirstOrDefaultAsync(x => x.CodigoBarras == cod, ct);
            if (porCodigo is not null)
                return porCodigo;
        }

        if (!string.IsNullOrWhiteSpace(line.Producto))
        {
            var name = line.Producto.Trim();
            return await db.Productos.FirstOrDefaultAsync(x => x.Nombre == name, ct);
        }

        return null;
    }

    private static CommerceProducto? CrearDesdeLinea(MulticajaVentaLineaDto line)
    {
        var codigo = (line.CodigoBarras ?? string.Empty).Trim();
        var nombre = (line.Producto ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(codigo) && string.IsNullOrWhiteSpace(nombre))
            return null;

        if (string.IsNullOrWhiteSpace(codigo))
            codigo = $"AUTO-{Guid.NewGuid():N}"[..20];
        if (string.IsNullOrWhiteSpace(nombre))
            nombre = codigo;

        var cantidad = Math.Max(0, line.Cantidad);
        return new CommerceProducto
        {
            Nombre = nombre,
            CodigoBarras = codigo,
            Precio = line.Precio,
            Costo = line.Costo ?? line.Precio,
            PrecioMayoreo = line.Precio,
            Stock = Math.Max(line.Stock ?? 0, cantidad),
            TipoVenta = string.IsNullOrWhiteSpace(line.TipoVenta) ? "Unidad" : line.TipoVenta.Trim(),
            Departamento = string.IsNullOrWhiteSpace(line.Departamento) ? "General" : line.Departamento.Trim()
        };
    }
}
