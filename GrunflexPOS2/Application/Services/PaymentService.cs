using System;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Services.API;

namespace GrunflexPOS2.Application.Services;

public sealed class PaymentService
{
    private readonly PagoApiService _pagoApiService;

    public PaymentService(PagoApiService pagoApiService)
    {
        _pagoApiService = pagoApiService;
    }

    public async Task<PagoTransaccion?> ProcesarTarjetaConPollingAsync(decimal monto, int numeroTicket, CancellationToken cancellationToken = default)
    {
        var id = await _pagoApiService.CrearPagoAsync(Math.Round(monto, 0), numeroTicket.ToString());
        if (id == null)
            return null;

        PagoTransaccion? estado = null;
        for (var i = 0; i < 20; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(1000, cancellationToken);

            estado = await _pagoApiService.ObtenerEstadoAsync(id.Value);
            if (estado != null && estado.Estado != "PENDIENTE")
                break;
        }

        return estado;
    }
}
