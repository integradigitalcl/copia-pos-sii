using System;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Multicaja;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Data;

/// <summary>
/// En terminal API-only bloquea persistencia local de entidades que deben vivir solo en el servidor.
/// </summary>
internal static class MulticajaLocalWriteGuard
{
    public static void ValidateAndThrow(GrunflexDbContext db)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        foreach (var e in db.ChangeTracker.Entries())
        {
            if (e.State is EntityState.Unchanged or EntityState.Detached)
                continue;

            if (e.Entity is VentaEntity or DetalleVenta or MovimientoCaja or CajaSesion)
            {
                PosDiagnostics.Log(
                    $"multicaja.guard blocked_local_ef entity={e.Entity.GetType().Name} state={e.State}");
                throw new InvalidOperationException(
                    "Terminal en modo multicaja API-only: ventas, detalles, movimientos de caja y sesiones " +
                    "no se persisten en SQLite local. Operá solo vía API central.");
            }

            // Producto: la sombra local DEBE recibir catálogo/stock desde el servidor (sync pull).
            // Los ajustes manuales de stock en adicional van por API (InventarioView), no por SaveChanges local.
        }
    }
}
