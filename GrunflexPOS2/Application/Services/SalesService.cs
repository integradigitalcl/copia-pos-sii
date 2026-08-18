using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Repositories;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Multicaja;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Application.Services;

public sealed class SalesService
{
    private readonly object _sync = new();
    private bool _ticketSequenceReady;
    private readonly GrunflexDbContext _db;
    private readonly IVentaRepository _ventaRepository;
    private readonly CashRegisterService _cashRegisterService;
    private readonly TicketService _ticketService;
    private readonly InventoryService _inventoryService;

    private List<Venta> _historialVentas = new();

    public SalesService(
        GrunflexDbContext db,
        IVentaRepository ventaRepository,
        CashRegisterService cashRegisterService,
        TicketService ticketService,
        InventoryService inventoryService)
    {
        _db = db;
        _ventaRepository = ventaRepository;
        _cashRegisterService = cashRegisterService;
        _ticketService = ticketService;
        _inventoryService = inventoryService;
        _historialVentas = _ventaRepository.ObtenerVentas();
    }

    public int GenerarNumeroTicket()
    {
        lock (_sync)
        {
            return GenerarNumeroTicketDesdeDb();
        }
    }

    public bool GuardarVenta(Venta venta, out string? error)
    {
        lock (_sync)
        {
            error = null;
            _historialVentas = _ventaRepository.ObtenerVentas();
            var sesion = _cashRegisterService.ObtenerSesionAbierta();
            _cashRegisterService.CompletarContextoVenta(venta, sesion);

            if (venta.EsConsumoPersonal)
                venta.Total = 0;

            if (UsesCentralSalesApi())
                return GuardarVentaConApiYRespaldo(venta, sesion, out error);

            return GuardarVentaLocalCompleta(venta, sesion, out error);
        }
    }

    private bool GuardarVentaLocalCompleta(Venta venta, CajaSesion sesion, out string? error)
    {
        if (!TryPersistVentaLocalTransaccional(venta, sesion, out error))
            return false;

        _historialVentas.Add(venta);
        _ventaRepository.GuardarVentas(_historialVentas);
        _ticketService.NotificarVenta(venta);
        return true;
    }

    private bool GuardarVentaConApiYRespaldo(Venta venta, CajaSesion sesion, out string? error)
    {
        error = null;
        var cfg = AppConfig.Cargar();
        var body = CrearCommitRequest(venta, sesion);

        if (IntentarCommitApiEnLinea(body, venta, sesion, cfg, out error))
        {
            _historialVentas.Add(venta);
            _ventaRepository.GuardarVentas(_historialVentas);
            if (!MulticajaRuntime.UseApiOnlyClient && !venta.EsConsumoPersonal)
                SincronizarStockLocalTrasVentaCentral(venta.Items);
            _ticketService.NotificarVenta(venta);
            return true;
        }

        if (cfg.EsCajaPrincipal && !MulticajaRuntime.UseApiOnlyClient)
        {
            if (!TryPersistVentaLocalTransaccional(venta, sesion, out error))
                return false;

            if (TryEnqueueVentaCommit(body, cfg))
            {
                PosDiagnostics.Log(
                    $"multicaja.venta: guardada local + cola id={body.RequestId[..Math.Min(8, body.RequestId.Length)]}");
            }
            else
            {
                PosDiagnostics.Log("multicaja.venta: guardada local; cola offline no disponible (revisar disco/caps).");
            }

            _historialVentas.Add(venta);
            _ventaRepository.GuardarVentas(_historialVentas);
            _ticketService.NotificarVenta(venta);
            error = null;
            return true;
        }

        if (MulticajaRuntime.UseApiOnlyClient)
        {
            if (!TryEnqueueVentaCommit(body, cfg))
            {
                error ??= "Sin conexión al servidor. No se pudo guardar la venta en cola offline.";
                return false;
            }

            if (venta.NumeroTicket <= 0)
                venta.NumeroTicket = GenerarNumeroTicketProvisional();

            _historialVentas.Add(venta);
            _ventaRepository.GuardarVentas(_historialVentas);
            _ticketService.NotificarVenta(venta);
            PosDiagnostics.Log("multicaja.venta: terminal API-only — venta encolada para sync.");
            return true;
        }

        error ??= "No se pudo registrar la venta en el servidor. Verifique que la API esté activa.";
        return false;
    }

    private bool IntentarCommitApiEnLinea(
        MulticajaVentaCommitRequest body,
        Venta venta,
        CajaSesion sesion,
        AppConfig cfg,
        out string? error)
    {
        error = null;
        if (App.Connectivity?.State == ConnectivityState.Offline)
            return false;

        if (cfg.MulticajaBlockCriticalWhenOffline && App.Connectivity?.State == ConnectivityState.Degraded)
        {
            error = "Conectividad degradada; venta bloqueada por configuración.";
            return false;
        }

        var ok = Task.Run(() => CommitVentaEnServidorAsync(body, venta, sesion)).GetAwaiter().GetResult();
        if (!ok)
            error = "No se pudo registrar la venta en el servidor.";
        return ok;
    }

    private MulticajaVentaCommitRequest CrearCommitRequest(Venta venta, CajaSesion sesion)
    {
        var body = new MulticajaVentaCommitRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CajaId = venta.CajaId,
            CajaSesionId = sesion.Id,
            UsuarioId = venta.UsuarioId,
            Cliente = venta.Cliente,
            MetodoPago = venta.MetodoPago,
            EsConsumoPersonal = venta.EsConsumoPersonal,
            Items = venta.Items.Select(i => new MulticajaVentaLineaDto
            {
                CodigoBarras = i.CodigoBarras,
                Producto = i.Producto,
                Cantidad = i.Cantidad,
                Precio = i.Precio
            }).ToList()
        };
        MulticajaTerminalAuditHelper.Enrich(body);
        return body;
    }

    private bool TryPersistVentaLocalTransaccional(Venta venta, CajaSesion sesion, out string? error)
    {
        error = null;
        using var tx = _db.Database.BeginTransaction();
        try
        {
            if (venta.NumeroTicket <= 0)
                venta.NumeroTicket = GenerarNumeroTicketDesdeDb();

            var ventaDb = new VentaEntity
            {
                NumeroTicket = venta.NumeroTicket,
                Fecha = venta.Fecha,
                Total = venta.Total,
                NumeroCaja = venta.NumeroCaja,
                CajaId = venta.CajaId,
                Cajero = venta.Cajero,
                Cliente = venta.Cliente,
                MetodoPago = venta.MetodoPago,
                EstaAnulada = venta.EstaAnulada,
                FechaAnulacion = venta.FechaAnulacion,
                UsuarioId = venta.UsuarioId,
                CajaSesionId = venta.CajaSesionId,
                EsConsumoPersonal = venta.EsConsumoPersonal
            };

            _db.Ventas.Add(ventaDb);
            _db.SaveChanges();

            foreach (var linea in venta.Items)
            {
                _db.DetalleVentas.Add(new DetalleVenta
                {
                    VentaId = ventaDb.Id,
                    CodigoBarras = linea.CodigoBarras,
                    Producto = linea.Producto,
                    Cantidad = linea.Cantidad,
                    Precio = linea.Precio
                });
            }

            if (!venta.EsConsumoPersonal)
                _inventoryService.DescontarStockPorLineas(venta.Items);

            if (!venta.EsConsumoPersonal)
            {
                var sesionDb = _db.CajaSesiones.FirstOrDefault(x => x.Id == sesion.Id)
                    ?? throw new InvalidOperationException("Sesión de caja no encontrada.");
                if (!sesionDb.Abierta)
                    throw new InvalidOperationException("No se pueden registrar movimientos en una caja cerrada.");

                _db.MovimientosCaja.Add(new MovimientoCaja
                {
                    Id = Guid.NewGuid(),
                    CajaSesionId = sesion.Id,
                    Fecha = DateTime.UtcNow,
                    Tipo = "VENTA",
                    Monto = venta.Total,
                    Descripcion = $"Venta Ticket #{venta.NumeroTicket}"
                });
                sesionDb.TotalVentas += venta.Total;
            }

            _db.SaveChanges();
            tx.Commit();
            PosDiagnostics.Log($"venta: persistida local ticket={venta.NumeroTicket}");
            return true;
        }
        catch (Exception ex)
        {
            tx.Rollback();
            PosDiagnostics.Log("venta: fallo transaccional local (rollback completo).", ex);
            error = "No se pudo guardar la venta. No se modificó el inventario.";
            return false;
        }
    }

    private int GenerarNumeroTicketProvisional()
    {
        _historialVentas = _ventaRepository.ObtenerVentas();
        return (_historialVentas.Count == 0 ? 0 : _historialVentas.Max(v => v.NumeroTicket)) + 1;
    }

    private async Task<bool> CommitVentaEnServidorAsync(
        MulticajaVentaCommitRequest body,
        Venta venta,
        CajaSesion sesion)
    {
        try
        {
            var resp = await MulticajaOperacionesClient.CommitVentaAsync(body).ConfigureAwait(false);
            if (resp == null || !resp.Ok)
            {
                PosDiagnostics.Log($"multicaja.venta error code={resp?.ErrorCode} msg={resp?.Error}");
                return false;
            }

            venta.NumeroTicket = resp.NumeroTicket;
            if (App.MulticajaSesionEnServidor != null && App.MulticajaSesionEnServidor.Id == sesion.Id &&
                !venta.EsConsumoPersonal)
                App.MulticajaSesionEnServidor.TotalVentas += venta.Total;

            PosDiagnostics.Log($"multicaja.venta ok ticket={resp.NumeroTicket} ventaId={resp.VentaId}");
            return true;
        }
        catch (HttpRequestException ex)
        {
            PosDiagnostics.Log("multicaja.venta: error de red al enviar.", ex);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            PosDiagnostics.Log("multicaja.venta: timeout al enviar.", ex);
            return false;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.venta excepción", ex);
            return false;
        }
    }

    private async Task<bool> CommitVentaEnServidorAsync(Venta venta, CajaSesion sesion)
    {
        var body = CrearCommitRequest(venta, sesion);
        return await CommitVentaEnServidorAsync(body, venta, sesion).ConfigureAwait(false);
    }

    /// <summary>
    /// Encola commit de venta con <paramref name="body"/>.<c>RequestId</c> como id estable de cola e idempotencia API.
    /// </summary>
    private static bool TryEnqueueVentaCommit(MulticajaVentaCommitRequest body, AppConfig cfg)
    {
        if (!cfg.MulticajaEnqueueCriticalWhenOffline)
            return false;
        if (string.IsNullOrWhiteSpace(body.RequestId))
            body.RequestId = Guid.NewGuid().ToString("N");

        if (!MulticajaOfflineEnqueue.TryEnqueue("multicaja-venta-commit", body, body.RequestId))
            return false;
        var pend = App.OfflineQueue?.PendingCount() ?? 0;
        PosDiagnostics.Log($"multicaja.venta: encolada id={body.RequestId[..8]} pendientes={pend}");
        return true;
    }

    private int GenerarNumeroTicketDesdeDb()
    {
        if (UsesCentralSalesApi())
        {
            _historialVentas = _ventaRepository.ObtenerVentas();
            return (_historialVentas.Count == 0 ? 0 : _historialVentas.Max(v => v.NumeroTicket)) + 1;
        }

        try
        {
            var con = _db.Database.GetDbConnection();
            if (con.State != ConnectionState.Open)
                con.Open();

            if (!_ticketSequenceReady)
            {
                using var crearSeq = con.CreateCommand();
                crearSeq.CommandText = "CREATE SEQUENCE IF NOT EXISTS ventas_numero_ticket_seq START WITH 1 INCREMENT BY 1;";
                crearSeq.ExecuteNonQuery();

                using var maxCmd = con.CreateCommand();
                maxCmd.CommandText = "SELECT COALESCE(MAX(\"NumeroTicket\"), 0) FROM \"Ventas\";";
                var maxObj = maxCmd.ExecuteScalar();
                var maxActual = Convert.ToInt32(maxObj ?? 0);

                using var setvalCmd = con.CreateCommand();
                setvalCmd.CommandText = "SELECT setval('ventas_numero_ticket_seq', @maxValue, @isCalled);";
                var pMax = setvalCmd.CreateParameter();
                pMax.ParameterName = "@maxValue";
                pMax.Value = maxActual;
                setvalCmd.Parameters.Add(pMax);
                var pIsCalled = setvalCmd.CreateParameter();
                pIsCalled.ParameterName = "@isCalled";
                pIsCalled.Value = maxActual > 0;
                setvalCmd.Parameters.Add(pIsCalled);
                setvalCmd.ExecuteScalar();

                _ticketSequenceReady = true;
            }

            using var nextCmd = con.CreateCommand();
            nextCmd.CommandText = "SELECT nextval('ventas_numero_ticket_seq');";
            var nextObj = nextCmd.ExecuteScalar();
            var next = Convert.ToInt32(nextObj ?? 1);
            return next;
        }
        catch
        {
            // Fallback defensivo para no bloquear la operación de caja.
            var maxDb = _db.Ventas.Any() ? _db.Ventas.Max(v => v.NumeroTicket) : 0;
            return maxDb + 1;
        }
    }

    public void AnularVenta(int numeroTicket)
    {
        if (UsesCentralSalesApi())
        {
            var ok = Task.Run(() => AnularVentaEnServidorAsync(numeroTicket)).GetAwaiter().GetResult();
            if (!ok)
                return;

            lock (_sync)
            {
                _historialVentas = _ventaRepository.ObtenerVentas();
                var venta = _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
                if (venta == null || venta.EstaAnulada)
                    return;

                venta.EstaAnulada = true;
                venta.FechaAnulacion = DateTime.UtcNow;
                _ventaRepository.GuardarVentas(_historialVentas);
            }

            return;
        }

        lock (_sync)
        {
            _historialVentas = _ventaRepository.ObtenerVentas();
            var venta = _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
            if (venta == null || venta.EstaAnulada)
                return;

            venta.EstaAnulada = true;
            venta.FechaAnulacion = DateTime.UtcNow;
            _ventaRepository.GuardarVentas(_historialVentas);

            try
            {
                var ventaDb = _db.Ventas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
                if (ventaDb != null)
                {
                    ventaDb.EstaAnulada = true;
                    ventaDb.FechaAnulacion = venta.FechaAnulacion;
                    _db.SaveChanges();
                }
            }
            catch
            {
                // noop
            }
        }
    }

    private async Task<bool> AnularVentaEnServidorAsync(int numeroTicket)
    {
        var cfg = AppConfig.Cargar();
        var sesion = _cashRegisterService.ObtenerSesionAbierta();
        var uid = App.UsuarioActual?.Id ?? Guid.Empty;
        if (uid == Guid.Empty)
        {
            PosDiagnostics.Log("multicaja.anulacion: sin usuario actual.");
            return false;
        }

        var body = new MulticajaAnularVentaRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CajaId = sesion.CajaId,
            CajaSesionId = sesion.Id,
            UsuarioId = uid,
            NumeroTicket = numeroTicket
        };
        MulticajaTerminalAuditHelper.Enrich(body);

        try
        {
            if (App.Connectivity?.State == ConnectivityState.Offline)
            {
                if (MulticajaOfflineEnqueue.TryEnqueue("multicaja-anular-venta", body, body.RequestId))
                    PosDiagnostics.Log("multicaja.anulacion: encolada (API offline).");
                return false;
            }

            if (cfg.MulticajaBlockCriticalWhenOffline && App.Connectivity?.State == ConnectivityState.Degraded)
            {
                PosDiagnostics.Log("multicaja.anulacion: bloqueado en modo degradado.");
                return false;
            }

            var resp = await MulticajaOperacionesClient.AnularVentaAsync(body).ConfigureAwait(false);
            if (resp == null || !resp.Ok)
            {
                PosDiagnostics.Log($"multicaja.anulacion error code={resp?.ErrorCode} msg={resp?.Error}");
                return false;
            }

            if (App.MulticajaSesionEnServidor != null && App.MulticajaSesionEnServidor.Id == sesion.Id)
            {
                var hv = _ventaRepository.ObtenerVentas().FirstOrDefault(v => v.NumeroTicket == numeroTicket);
                if (hv != null && !hv.EsConsumoPersonal)
                    App.MulticajaSesionEnServidor.TotalVentas =
                        Math.Max(0, App.MulticajaSesionEnServidor.TotalVentas - hv.Total);
            }

            PosDiagnostics.Log($"multicaja.anulacion ok ticket={numeroTicket}");
            return true;
        }
        catch (HttpRequestException ex)
        {
            PosDiagnostics.Log("multicaja.anulacion: error de red.", ex);
            MulticajaOfflineEnqueue.TryEnqueue("multicaja-anular-venta", body, body.RequestId);
            return false;
        }
        catch (TaskCanceledException ex)
        {
            PosDiagnostics.Log("multicaja.anulacion: timeout.", ex);
            MulticajaOfflineEnqueue.TryEnqueue("multicaja-anular-venta", body, body.RequestId);
            return false;
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.anulacion excepción", ex);
            return false;
        }
    }

    public List<Venta> ObtenerVentas()
    {
        _historialVentas = _ventaRepository.ObtenerVentas();
        return _historialVentas;
    }

    public Venta? ObtenerVentaPorTicket(int numeroTicket)
    {
        _historialVentas = _ventaRepository.ObtenerVentas();
        return _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
    }

    public bool DevolverArticulo(int numeroTicket, string producto, decimal precio, int cantidad, out string mensaje)
    {
        mensaje = string.Empty;
        if (cantidad <= 0)
        {
            mensaje = "Cantidad inválida.";
            return false;
        }

        if (UsesCentralSalesApi())
        {
            var (ok, msg) = Task.Run(() => DevolverArticuloEnServidorAsync(numeroTicket, producto, precio, cantidad))
                .GetAwaiter().GetResult();
            mensaje = msg;
            if (!ok)
                return false;

            lock (_sync)
            {
                _historialVentas = _ventaRepository.ObtenerVentas();
                var venta = _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
                if (venta == null || venta.EstaAnulada)
                    return false;

                var item = venta.Items.FirstOrDefault(i =>
                    string.Equals(i.Producto ?? "", producto ?? "", StringComparison.OrdinalIgnoreCase) &&
                    i.Precio == precio);
                if (item == null || item.Cantidad < cantidad)
                    return false;

                item.Cantidad -= cantidad;
                if (item.Cantidad <= 0)
                    venta.Items.Remove(item);

                venta.Total = venta.Items.Sum(x => x.Importe);
                _ventaRepository.GuardarVentas(_historialVentas);
            }

            return true;
        }

        lock (_sync)
        {
            _historialVentas = _ventaRepository.ObtenerVentas();
            var venta = _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
            if (venta == null)
            {
                mensaje = "No se encontró la venta.";
                return false;
            }

            if (venta.EstaAnulada)
            {
                mensaje = "La venta ya está anulada.";
                return false;
            }

            if (!_inventoryService.AplicarDevolucion(venta, numeroTicket, producto, precio, cantidad, out var montoDevuelto, out mensaje))
                return false;

            _ventaRepository.GuardarVentas(_historialVentas);

            try
            {
                var sesion = _db.CajaSesiones.FirstOrDefault(c => c.Abierta);
                if (sesion != null)
                    _cashRegisterService.RegistrarMovimientoDevolucion(sesion.Id, numeroTicket, montoDevuelto, producto);
            }
            catch
            {
                // noop
            }

            return true;
        }
    }

    private async Task<(bool ok, string mensaje)> DevolverArticuloEnServidorAsync(int numeroTicket, string producto,
        decimal precio, int cantidad)
    {
        var cfg = AppConfig.Cargar();
        var sesion = _cashRegisterService.ObtenerSesionAbierta();
        var uid = App.UsuarioActual?.Id ?? Guid.Empty;
        if (uid == Guid.Empty)
            return (false, "No hay usuario logueado.");

        _historialVentas = _ventaRepository.ObtenerVentas();
        var venta = _historialVentas.FirstOrDefault(v => v.NumeroTicket == numeroTicket);
        if (venta == null)
            return (false, "No se encontró la venta.");
        if (venta.EstaAnulada)
            return (false, "La venta ya está anulada.");

        var line = venta.Items.FirstOrDefault(i =>
            string.Equals(i.Producto ?? "", producto ?? "", StringComparison.OrdinalIgnoreCase) &&
            i.Precio == precio);
        if (line == null)
            return (false, "No se encontró el artículo en el ticket.");
        if (line.Cantidad < cantidad)
            return (false, "La cantidad a devolver supera lo vendido.");

        var body = new MulticajaDevolucionLineaRequest
        {
            RequestId = Guid.NewGuid().ToString("N"),
            CajaId = sesion.CajaId,
            CajaSesionId = sesion.Id,
            UsuarioId = uid,
            NumeroTicket = numeroTicket,
            CodigoBarras = string.IsNullOrWhiteSpace(line.CodigoBarras) ? null : line.CodigoBarras.Trim(),
            Producto = line.Producto,
            Precio = line.Precio,
            Cantidad = cantidad
        };
        MulticajaTerminalAuditHelper.Enrich(body);

        try
        {
            if (App.Connectivity?.State == ConnectivityState.Offline)
            {
                if (MulticajaOfflineEnqueue.TryEnqueue("multicaja-devolucion-linea", body, body.RequestId))
                    PosDiagnostics.Log("multicaja.devolucion: encolada (API offline).");
                return (false, "Sin conexión al servidor. Intente cuando vuelva la red o revise la cola offline.");
            }

            if (cfg.MulticajaBlockCriticalWhenOffline && App.Connectivity?.State == ConnectivityState.Degraded)
                return (false, "Conectividad degradada; devolución bloqueada por configuración.");

            var resp = await MulticajaOperacionesClient.DevolverLineaAsync(body).ConfigureAwait(false);
            if (resp == null || !resp.Ok)
                return (false, resp?.Error ?? "Error en servidor.");

            if (App.MulticajaSesionEnServidor != null && App.MulticajaSesionEnServidor.Id == sesion.Id &&
                resp.MontoDevuelto > 0)
                App.MulticajaSesionEnServidor.TotalVentas =
                    Math.Max(0, App.MulticajaSesionEnServidor.TotalVentas - resp.MontoDevuelto);

            PosDiagnostics.Log($"multicaja.devolucion ok ticket={numeroTicket} monto={resp.MontoDevuelto}");
            return (true, $"Devolución aplicada. Monto: {resp.MontoDevuelto:C}.");
        }
        catch (HttpRequestException ex)
        {
            PosDiagnostics.Log("multicaja.devolucion: error de red.", ex);
            MulticajaOfflineEnqueue.TryEnqueue("multicaja-devolucion-linea", body, body.RequestId);
            return (false, "Error de red; devolución encolada para reintento.");
        }
        catch (TaskCanceledException ex)
        {
            PosDiagnostics.Log("multicaja.devolucion: timeout.", ex);
            MulticajaOfflineEnqueue.TryEnqueue("multicaja-devolucion-linea", body, body.RequestId);
            return (false, "Timeout; devolución encolada para reintento.");
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.devolucion excepción", ex);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Ventas/anulaciones/devoluciones que mutan inventario deben pasar por la API cuando hay
    /// inventario centralizado (caja principal o terminal API-only).
    /// </summary>
    private static bool UsesCentralSalesApi() =>
        MulticajaInventoryWriter.ShouldUseCentralApi(AppConfig.Cargar());

    /// <summary>
    /// Tras commit en API (misma grunflex.db), recarga stock y aplica fallback si EF/WAL quedó desfasado.
    /// </summary>
    private void SincronizarStockLocalTrasVentaCentral(IEnumerable<DetalleVenta> lineas)
    {
        try
        {
            foreach (var line in lineas)
            {
                var p = BuscarProductoLocal(line);
                if (p == null)
                    continue;

                var stockAntes = p.Stock;
                _db.Entry(p).Reload();

                if (p.Stock >= stockAntes && line.Cantidad > 0)
                {
                    p.Stock = Math.Max(0, p.Stock - line.Cantidad);
                    _db.SaveChanges();
                    PosDiagnostics.Log(
                        $"multicaja.venta: fallback stock local {line.Producto} {stockAntes} → {p.Stock}");
                }
            }
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("multicaja.venta: no se pudo sincronizar stock local tras commit API.", ex);
        }
    }

    private Producto? BuscarProductoLocal(DetalleVenta line)
    {
        Producto? p = null;
        var codigo = line.CodigoBarras?.Trim();
        if (!string.IsNullOrEmpty(codigo))
            p = _db.Productos.Local.FirstOrDefault(x => x.CodigoBarras == codigo)
                ?? _db.Productos.FirstOrDefault(x => x.CodigoBarras == codigo);

        if (p == null && !string.IsNullOrWhiteSpace(line.Producto))
            p = _db.Productos.Local.FirstOrDefault(x => x.Nombre == line.Producto)
                ?? _db.Productos.FirstOrDefault(x => x.Nombre == line.Producto);

        return p;
    }
}
