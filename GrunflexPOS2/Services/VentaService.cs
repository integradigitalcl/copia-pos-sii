using System.Collections.Generic;
using System.IO;
using GrunflexPOS2.Application.Services;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Infrastructure.Repositories;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Services
{
    /// <summary>
    /// Fachada de compatibilidad para UI legacy.
    /// La orquestación real vive en Application.Services.SalesService.
    /// </summary>
    public class VentaService
    {
        private readonly SalesService _salesService;

        public VentaService(GrunflexDbContext db)
            : this(db, App.SessionContext)
        {
        }

        public VentaService(GrunflexDbContext db, IUserSessionContext sessionContext)
        {
            LocalDatabasePaths.EnsureDataDirectoryExists();
            var historialLocal = Path.Combine(LocalDatabasePaths.DataDirectory, "ventas_historial.json");
            var ventaRepository = new VentaFileRepository(historialLocal);
            var cashRegisterService = new CashRegisterService(db, sessionContext);
            var ticketService = new TicketService(
                ventaRepository,
                new BoletaPdfService(),
                new EmailService(),
                new ConfiguracionService());
            var inventoryService = new InventoryService(db);

            _salesService = new SalesService(
                db,
                ventaRepository,
                cashRegisterService,
                ticketService,
                inventoryService);
        }

        public int GenerarNumeroTicket() => _salesService.GenerarNumeroTicket();
        public bool GuardarVenta(Venta venta, out string? error) =>
            _salesService.GuardarVenta(venta, out error);
        public void AnularVenta(int numeroTicket) => _salesService.AnularVenta(numeroTicket);
        public List<Venta> ObtenerVentas() => _salesService.ObtenerVentas();
        public Venta? ObtenerVentaPorTicket(int numeroTicket) => _salesService.ObtenerVentaPorTicket(numeroTicket);
        public bool DevolverArticulo(int numeroTicket, string producto, decimal precio, int cantidad, out string mensaje) =>
            _salesService.DevolverArticulo(numeroTicket, producto, precio, cantidad, out mensaje);
    }
}