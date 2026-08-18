using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Sincroniza catálogo y cajeros desde la API del servidor hacia la BD sombra local
/// (caja adicional API-only). Sin esto, el POS muestra datos viejos y el login remoto
/// no coincide con usuarios creados solo en sombra.
/// </summary>
public static class MulticajaShadowCatalogSync
{
    public static async Task<(bool Ok, string? Error)> PullUsuariosAsync(CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient || App.DbContext == null)
            return (false, "No aplica");

        var remoto = await MulticajaOperacionesClient.ListarUsuariosAsync(ct).ConfigureAwait(false);
        if (remoto == null)
            return (false, "No se pudo obtener la lista de usuarios del servidor.");

        await using var tx = await App.DbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var idsRemoto = remoto.Select(x => x.Id).ToHashSet();
            var locales = await App.DbContext.Usuarios.ToListAsync(ct).ConfigureAwait(false);
            foreach (var u in locales.Where(u => !idsRemoto.Contains(u.Id)))
                App.DbContext.Usuarios.Remove(u);

            foreach (var dto in remoto)
            {
                var e = await App.DbContext.Usuarios.FindAsync(new object[] { dto.Id }, ct).ConfigureAwait(false);
                if (e == null)
                {
                    App.DbContext.Usuarios.Add(new Usuario
                    {
                        Id = dto.Id,
                        Username = dto.Username,
                        Password = string.Empty,
                        Nombre = dto.Nombre,
                        Rol = dto.Rol
                    });
                }
                else
                {
                    e.Username = dto.Username;
                    e.Nombre = dto.Nombre;
                    e.Rol = dto.Rol;
                }
            }

            await App.DbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (System.Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Ok, string? Error)> PullProductosAsync(CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient || App.DbContext == null)
            return (false, "No aplica");

        var remoto = await MulticajaOperacionesClient.ListarProductosAsync(ct).ConfigureAwait(false);
        if (remoto == null)
            return (false, "No se pudo obtener el catálogo del servidor.");

        return await ApplyProductosAsync(remoto, ct).ConfigureAwait(false);
    }

    public static async Task<(bool Ok, string? Error)> PullProductsByIdsAsync(
        IReadOnlyList<int> productIds,
        CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient || App.DbContext == null)
            return (false, "No aplica");

        var remoto = await MulticajaOperacionesClient.ListarProductosByIdsAsync(productIds, ct)
            .ConfigureAwait(false);
        if (remoto == null)
            return (false, "No se pudo obtener productos por IDs.");

        return await ApplyProductosAsync(remoto, ct).ConfigureAwait(false);
    }

    public static async Task<(bool Ok, string? Error)> ApplyProductosAsync(
        List<MulticajaProductoSyncDto> remoto,
        CancellationToken ct = default)
    {
        if (App.DbContext == null)
            return (false, "DbContext no disponible");

        await using var tx = await App.DbContext.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            using var _sync = MulticajaRuntime.EnterShadowCatalogSyncScope();
            foreach (var dto in remoto)
            {
                var e = await App.DbContext.Productos.FindAsync(new object[] { dto.Id }, ct).ConfigureAwait(false);
                if (e == null)
                {
                    App.DbContext.Productos.Add(new Producto
                    {
                        Id = dto.Id,
                        Nombre = dto.Nombre ?? "",
                        Costo = dto.Costo,
                        Precio = dto.Precio,
                        Stock = dto.Stock,
                        CodigoBarras = dto.CodigoBarras ?? "",
                        PrecioMayoreo = dto.PrecioMayoreo,
                        InvMinimo = dto.InvMinimo,
                        InvMaximo = dto.InvMaximo,
                        TipoVenta = dto.TipoVenta ?? "",
                        Departamento = dto.Departamento ?? "",
                        CategoriaId = dto.CategoriaId
                    });
                }
                else
                {
                    e.Nombre = dto.Nombre ?? "";
                    e.Costo = dto.Costo;
                    e.Precio = dto.Precio;
                    e.Stock = dto.Stock;
                    e.CodigoBarras = dto.CodigoBarras ?? "";
                    e.PrecioMayoreo = dto.PrecioMayoreo;
                    e.InvMinimo = dto.InvMinimo;
                    e.InvMaximo = dto.InvMaximo;
                    e.TipoVenta = dto.TipoVenta ?? "";
                    e.Departamento = dto.Departamento ?? "";
                    e.CategoriaId = dto.CategoriaId;
                }
            }

            await App.DbContext.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return (true, null);
        }
        catch (System.Exception ex)
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Ok, string? Error)> PullTodoAsync(CancellationToken ct = default)
    {
        var u = await PullUsuariosAsync(ct).ConfigureAwait(false);
        if (!u.Ok)
            return u;
        return await PullProductosAsync(ct).ConfigureAwait(false);
    }
}
