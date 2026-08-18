using System;
using System.Linq;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Application.Services;

public sealed class CashRegisterService
{
    private readonly GrunflexDbContext _db;
    private readonly IUserSessionContext _session;
    private readonly CajaService _cajaService;

    public CashRegisterService(GrunflexDbContext db, IUserSessionContext session)
    {
        _db = db;
        _session = session;
        _cajaService = new CajaService(db, session);
    }

    public CajaSesion ObtenerSesionAbierta()
    {
        if (App.MulticajaSesionEnServidor != null)
            return App.MulticajaSesionEnServidor;

        var sesion = _db.CajaSesiones.FirstOrDefault(c => c.Abierta);
        if (sesion == null)
            throw new Exception("No hay caja abierta. Debes abrir caja antes de vender.");
        return sesion;
    }

    public void CompletarContextoVenta(Venta venta, CajaSesion sesion)
    {
        venta.Fecha = DateTime.Now;
        venta.NumeroCaja = sesion.NumeroCaja;
        venta.Cajero = _session.UsuarioActual?.Username ?? "Sistema";
        venta.CajaId = sesion.CajaId;
        venta.UsuarioId = _session.UsuarioActual?.Id ?? Guid.Empty;
        venta.CajaSesionId = sesion.Id;
    }

    public void RegistrarMovimientoVenta(Guid cajaSesionId, int numeroTicket, decimal total)
    {
        _cajaService.RegistrarMovimiento(
            cajaSesionId,
            "VENTA",
            total,
            $"Venta Ticket #{numeroTicket}");
    }

    public void RegistrarMovimientoDevolucion(Guid cajaSesionId, int numeroTicket, decimal monto, string producto)
    {
        _cajaService.RegistrarMovimiento(
            cajaSesionId,
            "DEVOLUCION",
            -monto,
            $"Devolución Ticket #{numeroTicket} ({producto})");
    }
}
