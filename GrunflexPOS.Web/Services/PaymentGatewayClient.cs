using System.Net.Http.Json;

namespace GrunflexPOS.Web.Services;

public sealed class PaymentGatewayClient(HttpClient httpClient, ILogger<PaymentGatewayClient> logger)
{
    private readonly string _terminalId =
        Environment.GetEnvironmentVariable("GRUNFLEX_PAGO_TERMINAL_ID") ?? "POS-LOCAL";

    public async Task<PaymentResult> AuthorizeAsync(decimal amount, long ticketNumber,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await httpClient.PostAsJsonAsync("", new
            {
                monto = Math.Round(amount, 0),
                numeroTicket = ticketNumber.ToString(),
                terminalId = _terminalId
            }, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return PaymentResult.Failed("Error al iniciar pago con tarjeta.");

            var transaction = await response.Content.ReadFromJsonAsync<PaymentTransaction>(
                cancellationToken: cancellationToken);
            if (transaction is null)
                return PaymentResult.Failed("Respuesta inválida del sistema de pago.");

            for (var attempt = 0; attempt < 20 && transaction.Estado == "PENDIENTE"; attempt++)
            {
                await Task.Delay(1000, cancellationToken);
                transaction = await httpClient.GetFromJsonAsync<PaymentTransaction>(
                    transaction.Id.ToString(), cancellationToken);
                if (transaction is null)
                    return PaymentResult.Failed("No se pudo consultar el estado del pago.");
            }

            return transaction.Estado == "APROBADO"
                ? PaymentResult.Approved(transaction.CodigoAutorizacion)
                : PaymentResult.Failed($"Pago rechazado. Estado: {transaction.Estado}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Pasarela de pago no disponible");
            return PaymentResult.Failed("No se pudo conectar con la pasarela de pago.");
        }
    }

    private sealed record PaymentTransaction(Guid Id, string Estado, string CodigoAutorizacion);
}

public sealed record PaymentResult(bool Success, string Message)
{
    public static PaymentResult Approved(string authorization) =>
        new(true, string.IsNullOrWhiteSpace(authorization)
            ? "Pago aprobado."
            : $"Pago aprobado · Código: {authorization}");

    public static PaymentResult Failed(string message) => new(false, message);
}
