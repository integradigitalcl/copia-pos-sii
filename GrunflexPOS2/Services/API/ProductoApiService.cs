using System.Net;
using System.Net.Http;
using System.Text.Json;
using GrunflexPOS2.Services.DTOs;

namespace GrunflexPOS2.Services.API;

/// <summary>Consulta de productos vía HTTP (sin acceso directo a BD desde la UI).</summary>
public sealed class ProductoApiService : IProductoLookupService
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public ProductoApiService(HttpClient http)
    {
        _http = http;
    }

    public async Task<ProductoPosDto?> ObtenerPorCodigoBarrasAsync(string codigo, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return null;

        var encoded = Uri.EscapeDataString(codigo.Trim());
        var baseUri = _http.BaseAddress ?? throw new InvalidOperationException("HttpClient.BaseAddress no configurado.");
        var url = new Uri(baseUri, $"api/productos/codigo/{encoded}");

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        if (!response.IsSuccessStatusCode)
        {
            PosDiagnostics.Log($"Producto API HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<ProductoPosDto>(stream, JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }
}
