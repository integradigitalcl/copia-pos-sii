using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services.Idempotency;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Cliente HTTP hacia <c>/api/multicaja/*</c> en la caja principal (LAN).
/// </summary>
public static class MulticajaOperacionesClient
{
    private static readonly JsonSerializerOptions JsonWrite = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly JsonSerializerOptions JsonRead = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static HttpClient CreateHttp()
    {
        if (!LicenseAccessGate.TryEnsureMulticajaModule(out var licErr))
            throw new InvalidOperationException(licErr);

        var cfg = AppConfig.Cargar();
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Clamp(cfg.MulticajaHealthTimeoutSeconds * 3, 10, 120)) };
        var baseUrl = (cfg.ApiBaseUrl ?? "").Trim();
        if (!string.IsNullOrEmpty(baseUrl))
        {
            if (!baseUrl.EndsWith('/')) baseUrl += "/";
            http.BaseAddress = new Uri(baseUrl);
        }

        var secret = (cfg.MulticajaSharedSecret ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(secret))
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Multicaja-Key", secret);

        http.DefaultRequestHeaders.Remove("X-Grunflex-Caja-Id");
        if (Guid.TryParse(cfg.CajaId, out var cajaGuid))
            http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Caja-Id", cajaGuid.ToString());
        http.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Terminal", Environment.MachineName);

        return http;
    }

    public static async Task<MulticajaLoginResponse?> LoginAsync(string username, string password,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var body = JsonSerializer.Serialize(new { username, password }, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/login")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaLoginResponse>(stream, JsonRead, ct).ConfigureAwait(false);
    }

    public static async Task<MulticajaCajaAutoRegistroResponse?> AutoRegistroCajaAsync(string machineName,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var body = JsonSerializer.Serialize(new { machineName }, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/cajas/auto-registro")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaCajaAutoRegistroResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaCajaSesionDto?> ObtenerSesionAbiertaAsync(Guid cajaId,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http
            .GetAsync($"api/multicaja/caja-sesiones/abierta?cajaId={Uri.EscapeDataString(cajaId.ToString())}", ct)
            .ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaCajaSesionDto>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaCajaSesionDto?> AbrirSesionAsync(Guid cajaId, Guid usuarioId, string username,
        decimal montoInicial, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var payload = new
        {
            cajaId,
            usuarioId,
            username,
            montoInicial
        };
        var body = JsonSerializer.Serialize(payload, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/caja-sesiones/abrir")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaCajaSesionDto>(stream, JsonRead, ct).ConfigureAwait(false);
    }

    public static async Task<MulticajaCajaAutoRegistroResponse?> VincularEquipoCajaAsync(
        Guid cajaId,
        string machineName,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(new { machineName }, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"api/multicaja/cajas/{Uri.EscapeDataString(cajaId.ToString())}/vincular-equipo")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaCajaAutoRegistroResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<bool> CajaExisteAsync(Guid cajaId, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http.GetAsync($"api/multicaja/cajas/{cajaId}/exists", ct).ConfigureAwait(false);
        return resp.StatusCode == System.Net.HttpStatusCode.NoContent;
    }

    /// <summary>Diagnóstico: código HTTP crudo del endpoint de existencia de caja (401/404/204…).</summary>
    public static async Task<HttpStatusCode> GetCajaExistsHttpStatusAsync(Guid cajaId, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http.GetAsync($"api/multicaja/cajas/{cajaId}/exists", ct).ConfigureAwait(false);
        return resp.StatusCode;
    }

    public static async Task<MulticajaVentaCommitResponse?> CommitVentaAsync(MulticajaVentaCommitRequest body,
        CancellationToken ct = default)
    {
        MulticajaTerminalAuditHelper.Enrich(body);
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/ventas/commit")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var r = await JsonSerializer.DeserializeAsync<MulticajaVentaCommitResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
        return r;
    }

    public static async Task<MulticajaAnularVentaResponse?> AnularVentaAsync(MulticajaAnularVentaRequest body,
        CancellationToken ct = default)
    {
        MulticajaTerminalAuditHelper.Enrich(body);
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/ventas/anular")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaAnularVentaResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaDevolucionLineaResponse?> DevolverLineaAsync(MulticajaDevolucionLineaRequest body,
        CancellationToken ct = default)
    {
        MulticajaTerminalAuditHelper.Enrich(body);
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/ventas/devolucion-linea")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaDevolucionLineaResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaInventarioAjusteResponse?> AjustarInventarioAsync(
        MulticajaInventarioAjusteRequest body,
        CancellationToken ct = default)
    {
        MulticajaTerminalAuditHelper.Enrich(body);
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/inventario/ajustar")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaInventarioAjusteResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaCierreCajaResponse?> CerrarSesionAsync(MulticajaCierreCajaRequest body,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/caja-sesiones/cerrar")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaCierreCajaResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaMovimientoCajaResponse?> RegistrarMovimientoCajaAsync(
        MulticajaMovimientoCajaRequest body, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/caja-sesiones/movimiento")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        req.ApplyIdempotencyHeaders(body, body.RequestId, body.CajaId);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaMovimientoCajaResponse>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<int?> ObtenerOrdinalCajaAsync(Guid cajaId, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http
            .GetAsync($"api/multicaja/cajas/{Uri.EscapeDataString(cajaId.ToString())}/ordinal", ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<int>(stream, JsonRead, ct).ConfigureAwait(false);
    }

    public static async Task<List<MulticajaUsuarioSyncDto>?> ListarUsuariosAsync(CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http.GetAsync("api/multicaja/usuarios", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<MulticajaUsuarioSyncDto>>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<MulticajaUsuarioSyncDto?> CrearUsuarioAsync(MulticajaUsuarioUpsertRequest body,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/multicaja/usuarios")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaUsuarioSyncDto>(stream, JsonRead, ct).ConfigureAwait(false);
    }

    public static async Task<MulticajaUsuarioSyncDto?> ActualizarUsuarioAsync(Guid id, MulticajaUsuarioUpsertRequest body,
        CancellationToken ct = default)
    {
        using var http = CreateHttp();
        var json = JsonSerializer.Serialize(body, JsonWrite);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"api/multicaja/usuarios/{Uri.EscapeDataString(id.ToString())}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<MulticajaUsuarioSyncDto>(stream, JsonRead, ct).ConfigureAwait(false);
    }

    /// <summary>null = éxito; string = mensaje de error del servidor.</summary>
    public static async Task<string?> EliminarUsuarioAsync(Guid id, CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http
            .DeleteAsync($"api/multicaja/usuarios/{Uri.EscapeDataString(id.ToString())}", ct)
            .ConfigureAwait(false);
        if (resp.IsSuccessStatusCode)
            return null;
        var txt = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(txt) ? $"HTTP {(int)resp.StatusCode}" : txt;
    }

    public static async Task<List<MulticajaProductoSyncDto>?> ListarProductosAsync(CancellationToken ct = default)
    {
        using var http = CreateHttp();
        using var resp = await http.GetAsync("api/multicaja/productos", ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<MulticajaProductoSyncDto>>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }

    public static async Task<List<MulticajaProductoSyncDto>?> ListarProductosByIdsAsync(
        IReadOnlyList<int> ids,
        CancellationToken ct = default)
    {
        if (ids.Count == 0)
            return new List<MulticajaProductoSyncDto>();

        var distinct = ids.Where(i => i > 0).Distinct().ToList();
        var qs = string.Join(",", distinct);
        using var http = CreateHttp();
        using var resp = await http
            .GetAsync($"api/multicaja/sync/products/by-ids?ids={Uri.EscapeDataString(qs)}", ct)
            .ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<List<MulticajaProductoSyncDto>>(stream, JsonRead, ct)
            .ConfigureAwait(false);
    }
}

public sealed class MulticajaLoginResponse
{
    public bool Ok { get; set; }
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class MulticajaCajaAutoRegistroResponse
{
    public bool Ok { get; set; }
    public Guid CajaId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Error { get; set; }
}

public sealed class MulticajaCajaSesionDto
{
    public Guid Id { get; set; }
    public Guid CajaId { get; set; }
    public int NumeroCaja { get; set; }
    public string Cajero { get; set; } = "";
    public Guid UsuarioAperturaId { get; set; }
    public DateTime FechaApertura { get; set; }
    public decimal MontoApertura { get; set; }
    public bool Abierta { get; set; }
    public decimal TotalVentas { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalRetiros { get; set; }
}

public sealed class MulticajaVentaCommitRequest : IMulticajaInventoryAuditRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public string Cliente { get; set; } = "Público en general";
    public string MetodoPago { get; set; } = "Efectivo";
    public bool EsConsumoPersonal { get; set; }
    public List<MulticajaVentaLineaDto> Items { get; set; } = new();
}

public sealed class MulticajaVentaLineaDto
{
    public string? CodigoBarras { get; set; }
    public string Producto { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal Precio { get; set; }
}

public sealed class MulticajaVentaCommitResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public Guid VentaId { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaAnularVentaRequest : IMulticajaInventoryAuditRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int NumeroTicket { get; set; }
}

public sealed class MulticajaAnularVentaResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaDevolucionLineaRequest : IMulticajaInventoryAuditRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int NumeroTicket { get; set; }
    public string? CodigoBarras { get; set; }
    public string Producto { get; set; } = "";
    public decimal Precio { get; set; }
    public int Cantidad { get; set; }
}

public sealed class MulticajaDevolucionLineaResponse
{
    public bool Ok { get; set; }
    public int NumeroTicket { get; set; }
    public decimal MontoDevuelto { get; set; }
    public decimal NuevoTotalVenta { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaInventarioAjusteRequest : IMulticajaInventoryAuditRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public Guid? TerminalId { get; set; }
    public string? TerminalCode { get; set; }
    public Guid? UserSessionId { get; set; }
    public Guid? BranchId { get; set; }
    public int ProductoId { get; set; }
    public int CantidadDelta { get; set; }
    public string Motivo { get; set; } = "";
}

public sealed class MulticajaInventarioAjusteResponse
{
    public bool Ok { get; set; }
    public int ProductoId { get; set; }
    public int StockAnterior { get; set; }
    public int StockNuevo { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaCierreCajaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioCierreId { get; set; }
    public decimal MontoContado { get; set; }
}

public sealed class MulticajaCierreCajaResponse
{
    public bool Ok { get; set; }
    public decimal Esperado { get; set; }
    public decimal MontoContado { get; set; }
    public decimal Diferencia { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaMovimientoCajaRequest
{
    public string RequestId { get; set; } = "";
    public Guid CajaId { get; set; }
    public Guid CajaSesionId { get; set; }
    public Guid UsuarioId { get; set; }
    public string Tipo { get; set; } = "";
    public decimal Monto { get; set; }
    public string Descripcion { get; set; } = "";
}

public sealed class MulticajaMovimientoCajaResponse
{
    public bool Ok { get; set; }
    public string? Tipo { get; set; }
    public decimal Monto { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalRetiros { get; set; }
    public decimal TotalVentas { get; set; }
    public string? Error { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class MulticajaUsuarioSyncDto
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
}

public sealed class MulticajaUsuarioUpsertRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Rol { get; set; } = "";
}

public sealed class MulticajaProductoSyncDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Costo { get; set; }
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string CodigoBarras { get; set; } = "";
    public decimal PrecioMayoreo { get; set; }
    public int InvMinimo { get; set; }
    public int InvMaximo { get; set; }
    public string TipoVenta { get; set; } = "";
    public string Departamento { get; set; } = "";
    public int? CategoriaId { get; set; }
}
