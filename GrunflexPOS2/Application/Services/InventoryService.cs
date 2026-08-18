using System;
using System.Collections.Generic;
using System.Linq;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Application.Services;

public sealed class InventoryService
{
    private readonly GrunflexDbContext _db;

    public InventoryService(GrunflexDbContext db) => _db = db;

    public bool AplicarDevolucion(
        Venta venta,
        int numeroTicket,
        string producto,
        decimal precio,
        int cantidad,
        out decimal montoDevuelto,
        out string mensaje)
    {
        montoDevuelto = 0;
        mensaje = string.Empty;

        var item = venta.Items.FirstOrDefault(i =>
            string.Equals(i.Producto ?? "", producto ?? "", StringComparison.OrdinalIgnoreCase) &&
            i.Precio == precio);

        if (item == null)
        {
            mensaje = "No se encontró el artículo en el ticket.";
            return false;
        }

        if (item.Cantidad < cantidad)
        {
            mensaje = "La cantidad a devolver supera lo vendido.";
            return false;
        }

        montoDevuelto = item.Precio * cantidad;

        using var tx = _db.Database.BeginTransaction();
        try
        {
            item.Cantidad -= cantidad;
            if (item.Cantidad <= 0)
                venta.Items.Remove(item);

            venta.Total = venta.Items.Sum(x => x.Importe);

            var ventaDb = _db.Ventas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
            if (ventaDb != null)
            {
                var detDb = _db.DetalleVentas
                    .Where(d => d.VentaId == ventaDb.Id && d.Producto == producto && d.Precio == precio)
                    .OrderByDescending(d => d.Cantidad)
                    .FirstOrDefault();

                if (detDb != null)
                {
                    detDb.Cantidad -= cantidad;
                    if (detDb.Cantidad <= 0)
                        _db.DetalleVentas.Remove(detDb);
                }

                ventaDb.Total = Math.Max(0, ventaDb.Total - montoDevuelto);
            }

            RestaurarStockPorLineas(new[] { new DetalleVenta
            {
                CodigoBarras = item.CodigoBarras,
                Producto = item.Producto,
                Cantidad = cantidad,
                Precio = item.Precio
            }});

            _db.SaveChanges();
            tx.Commit();
        }
        catch (Exception ex)
        {
            tx.Rollback();
            PosDiagnostics.Log("inventario: error en devolución local.", ex);
            mensaje = "No se pudo aplicar la devolución. Intente de nuevo.";
            return false;
        }

        mensaje = $"Devolución aplicada. Monto: {montoDevuelto:C}.";
        return true;
    }

    /// <summary>Descuenta existencias (sin SaveChanges; usar dentro de transacción del caller).</summary>
    public void DescontarStockPorLineas(IEnumerable<DetalleVenta> lineas) =>
        AplicarDeltaStock(lineas, -1);

    public void RestaurarStockPorLineas(IEnumerable<DetalleVenta> lineas) =>
        AplicarDeltaStock(lineas, 1);

    private void AplicarDeltaStock(IEnumerable<DetalleVenta> lineas, int sign)
    {
        foreach (var line in lineas)
        {
            var p = BuscarProducto(line);
            if (p == null)
                throw new InvalidOperationException($"Producto no encontrado: {line.Producto ?? line.CodigoBarras}");

            if (sign < 0)
                p.Stock = Math.Max(0, p.Stock - line.Cantidad);
            else
                p.Stock += line.Cantidad;
        }
    }

    private Producto? BuscarProducto(DetalleVenta line)
    {
        Producto? p = null;
        var codigo = line.CodigoBarras?.Trim();
        if (!string.IsNullOrEmpty(codigo))
            p = _db.Productos.FirstOrDefault(x => x.CodigoBarras == codigo);

        if (p == null && !string.IsNullOrWhiteSpace(line.Producto))
            p = _db.Productos.FirstOrDefault(x => x.Nombre == line.Producto);

        return p;
    }
}
