using System.Net.Http;
using System.Net.Http.Json;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.API;

public static class SupportTicketApi
{
    public static async Task<(bool Ok, string Message)> SubmitAsync(
        string activationId,
        string subject,
        string body,
        CancellationToken cancellationToken = default)
    {
        var cfg = AppConfig.Cargar();
        var baseUrl = cfg.ApiBaseUrl.TrimEnd('/') + "/";

        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(25) };
            var payload = new { activationId, subject, body };
            using var response = await client.PostAsJsonAsync("api/support/tickets", payload, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                return (false, $"Servidor: {(int)response.StatusCode} {text}");

            return (true, "Ticket registrado.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }
}
