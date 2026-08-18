using System.IO;
using System.Linq;
using System.Text;
using GrunflexPOS2.Domain.Repositories;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Application.Services;

public sealed class TicketService
{
    private readonly IVentaRepository _ventaRepository;
    private readonly BoletaPdfService _pdfService;
    private readonly EmailService _emailService;
    private readonly ConfiguracionService _configuracion;

    public TicketService(
        IVentaRepository ventaRepository,
        BoletaPdfService pdfService,
        EmailService emailService,
        ConfiguracionService configuracion)
    {
        _ventaRepository = ventaRepository;
        _pdfService = pdfService;
        _emailService = emailService;
        _configuracion = configuracion;
    }

    public int GenerarNumeroTicket()
    {
        var ventas = _ventaRepository.ObtenerVentas();
        var ultimoNumero = ventas.Any() ? ventas.Max(v => v.NumeroTicket) : 0;
        return ultimoNumero + 1;
    }

    public void NotificarVenta(Venta venta)
    {
        try
        {
            if (venta.EsConsumoPersonal)
                return;

            if (!_configuracion.GetCorreoActivo())
                return;

            var email = _configuracion.GetCorreoEmail();
            var clave = _configuracion.GetCorreoClave();
            var host = _configuracion.Get("correo_host");
            var puertoStr = _configuracion.Get("correo_puerto");
            var sslStr = _configuracion.Get("correo_ssl");

            int.TryParse(puertoStr, out var puerto);
            if (puerto <= 0)
                puerto = 587;

            var ssl = sslStr == "true";
            var cuerpo = GenerarBoletaTexto(venta);
            string? rutaPdf = null;

            try
            {
                rutaPdf = _pdfService.GenerarBoletaPdf(venta);
                if (!File.Exists(rutaPdf))
                    rutaPdf = null;
            }
            catch
            {
                rutaPdf = null;
            }

            _emailService.EnviarCorreo(
                email,
                $"Boleta Ticket #{venta.NumeroTicket}",
                cuerpo,
                host,
                puerto,
                ssl,
                email,
                clave,
                rutaPdf,
                out _);
        }
        catch
        {
            // No romper cierre de venta por notificación
        }
    }

    private static string GenerarBoletaTexto(Venta venta)
    {
        var sb = new StringBuilder();
        sb.AppendLine("====== GRUNFLEX POS ======");
        sb.AppendLine($"Ticket: {venta.NumeroTicket}");
        sb.AppendLine($"Fecha: {venta.Fecha}");
        sb.AppendLine($"Caja: {venta.NumeroCaja}");
        sb.AppendLine($"Cajero: {venta.Cajero}");
        sb.AppendLine("--------------------------");

        foreach (var item in venta.Items)
            sb.AppendLine($"{item.Producto} x{item.Cantidad} - ${item.Importe}");

        sb.AppendLine("--------------------------");
        sb.AppendLine($"TOTAL: ${venta.Total}");
        sb.AppendLine("==========================");
        return sb.ToString();
    }
}
