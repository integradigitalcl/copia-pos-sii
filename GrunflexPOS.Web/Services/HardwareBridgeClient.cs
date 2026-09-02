using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace GrunflexPOS.Web.Services;

public sealed class HardwareBridgeClient(
    HttpClient httpClient,
    IConfiguration configuration,
    ILogger<HardwareBridgeClient> logger,
    LocalPosStore settings,
    BridgeTokenResolver tokenResolver,
    TicketTemplateService ticketTemplate,
    PosLogoService posLogo)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private string Token => tokenResolver.Resolve();

    public async Task<string> GetConfiguredPrinterNameAsync(CancellationToken cancellationToken = default) =>
        (await settings.GetSettingAsync(
            "impresora_nombre", configuration["HardwareBridge:PrinterName"] ?? string.Empty, cancellationToken)).Trim();

    public async Task<HardwareResult> PrintTicketAsync(
        long saleId, IReadOnlyCollection<CartItem> items, decimal total,
        string customer = "Público en general", CancellationToken cancellationToken = default)
    {
        var printer = await GetConfiguredPrinterNameAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(printer))
            return HardwareResult.Unavailable("Impresora no configurada");

        try
        {
            var template = await ticketTemplate.LoadAsync(cancellationToken);
            var ticket = ticketTemplate.BuildSaleTicket(template, saleId, items, total, customer);
            var logoBytes = await posLogo.GetLogoBytesAsync(cancellationToken);
            var logoMonochrome = template.PrintLogoMonochrome;
            var mode = await settings.GetSettingAsync("impresora_modo", "windows", cancellationToken);
            if (string.Equals(mode, "termica", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
            {
                return await SendRawTicketAsync(printer, ticket, logoBytes, cancellationToken, logoMonochrome: logoMonochrome);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/print/text")
            {
                Content = JsonContent.Create(new
                {
                    printer,
                    text = ticket,
                    logoBase64 = logoBytes is { Length: > 0 } ? Convert.ToBase64String(logoBytes) : null,
                    logoMonochrome
                })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
                return HardwareResult.Ok("Ticket enviado a la impresora");
            return HardwareResult.Unavailable(await ReadPrinterErrorAsync(response, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al imprimir");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> OpenDrawerAsync(CancellationToken cancellationToken = default)
    {
        var (enabled, mode, device) = await ResolveDrawerConfigAsync(cancellationToken);
        if (!enabled)
            return HardwareResult.Unavailable("Cajón desactivado");
        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(device))
            return HardwareResult.Unavailable("Cajón no configurado");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/drawer/open")
            {
                Content = JsonContent.Create(new { mode, device })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Cajón abierto")
                : HardwareResult.Unavailable($"Cajón no disponible ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al abrir cajón");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> TestPrinterAsync(string printer, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printer))
            return HardwareResult.Unavailable("Selecciona una impresora");
        var template = await ticketTemplate.LoadAsync(cancellationToken);
        var ticket = ticketTemplate.BuildPreviewTicket(template);
        var logoBytes = await posLogo.GetLogoBytesAsync(cancellationToken);
        var logoMonochrome = template.PrintLogoMonochrome;
        try
        {
            var mode = await settings.GetSettingAsync("impresora_modo", "windows", cancellationToken);
            if (string.Equals(mode, "termica", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
            {
                return await SendRawTicketAsync(printer, ticket, logoBytes, cancellationToken, isTest: true, logoMonochrome: logoMonochrome);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/print/text")
            {
                Content = JsonContent.Create(new
                {
                    printer,
                    text = ticket,
                    logoBase64 = logoBytes is { Length: > 0 } ? Convert.ToBase64String(logoBytes) : null,
                    logoMonochrome
                })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Prueba enviada a la impresora")
                : HardwareResult.Unavailable(await ReadPrinterErrorAsync(response, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible en prueba de impresora");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> TestPrinterAsync(string printer, string ticketText,
        byte[]? logoBytes = null, bool? logoMonochrome = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printer))
            return HardwareResult.Unavailable("Selecciona una impresora");
        logoBytes ??= await posLogo.GetLogoBytesAsync(cancellationToken);
        logoMonochrome ??= string.Equals(
            await settings.GetSettingAsync("ticket_logo_bn", "true", cancellationToken),
            "true", StringComparison.OrdinalIgnoreCase);
        try
        {
            var mode = await settings.GetSettingAsync("impresora_modo", "windows", cancellationToken);
            if (string.Equals(mode, "termica", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, "raw", StringComparison.OrdinalIgnoreCase))
            {
                return await SendRawTicketAsync(printer, ticketText, logoBytes, cancellationToken, isTest: true, logoMonochrome: logoMonochrome.Value);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, "api/print/text")
            {
                Content = JsonContent.Create(new
                {
                    printer,
                    text = ticketText,
                    logoBase64 = logoBytes is { Length: > 0 } ? Convert.ToBase64String(logoBytes) : null,
                    logoMonochrome = logoMonochrome.Value
                })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Prueba enviada a la impresora")
                : HardwareResult.Unavailable(await ReadPrinterErrorAsync(response, cancellationToken));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible en prueba de impresora");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> TestDrawerAsync(string mode, string device,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mode) || string.IsNullOrWhiteSpace(device))
            return HardwareResult.Unavailable("Indica modo y dispositivo del cajón");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/drawer/open")
            {
                Content = JsonContent.Create(new { mode, device })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Pulso de prueba enviado al cajón")
                : HardwareResult.Unavailable($"Cajón no disponible ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible en prueba de cajón");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "health");
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Bridge conectado")
                : HardwareResult.Unavailable($"Bridge respondió {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible en diagnóstico");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<IReadOnlyList<string>> ListPrintersAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/printers");
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var printers = await response.Content.ReadFromJsonAsync<List<PrinterInfoDto>>(JsonOptions, cancellationToken);
            return printers?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al listar impresoras");
            return [];
        }
    }

    public async Task<IReadOnlyList<string>> ListSerialPortsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/serial-ports");
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return [];

            var ports = await response.Content.ReadFromJsonAsync<List<SerialPortDto>>(JsonOptions, cancellationToken);
            return ports?.Select(x => x.Name).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray() ?? [];
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al listar puertos COM");
            return [];
        }
    }

    public async Task<HardwareResult> ConnectScannerAsync(
        string port,
        int baudRate = 9600,
        int dataBits = 8,
        string parity = "None",
        string stopBits = "One",
        string handshake = "None",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(port))
            return HardwareResult.Unavailable("Indica el puerto COM del lector");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/scanner/connect")
            {
                Content = JsonContent.Create(new
                {
                    port,
                    baudRate,
                    dataBits,
                    parity,
                    stopBits,
                    handshake
                })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var error = await ReadErrorAsync(response, cancellationToken);
                return HardwareResult.Unavailable(error ?? $"No se pudo conectar ({(int)response.StatusCode})");
            }

            var state = await response.Content.ReadFromJsonAsync<ScannerStateDto>(JsonOptions, cancellationToken);
            return HardwareResult.Ok(state?.Connected == true
                ? $"Lector conectado ({state.Port})"
                : "Lector no conectado");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al conectar lector");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<HardwareResult> DisconnectScannerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/scanner/disconnect");
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            return response.IsSuccessStatusCode
                ? HardwareResult.Ok("Lector desconectado")
                : HardwareResult.Unavailable($"No se pudo desconectar ({(int)response.StatusCode})");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al desconectar lector");
            return HardwareResult.Unavailable("Bridge desconectado");
        }
    }

    public async Task<ScannerStateDto?> GetScannerStateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "api/scanner/state");
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return null;
            return await response.Content.ReadFromJsonAsync<ScannerStateDto>(JsonOptions, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al consultar estado del lector");
            return null;
        }
    }

    public async IAsyncEnumerable<ScannerBridgeEvent> SubscribeScannerEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var baseAddress = httpClient.BaseAddress
            ?? new Uri(configuration["HardwareBridge:Url"] ?? "http://127.0.0.1:7390/");
        // SSE stays open; the typed client has a short timeout for print/drawer calls.
        using var sseClient = new HttpClient { BaseAddress = baseAddress, Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/scanner/events");
        request.Headers.Authorization = new("Bearer", Token);
        using var response = await sseClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        string? eventName = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                yield break;

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line["event:".Length..].Trim();
                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line["data:".Length..].Trim();
                if (TryParseScannerEvent(data, eventName, out var parsed) && parsed is not null)
                    yield return parsed;
                eventName = null;
            }
        }
    }

    public static bool TryParseScannerEvent(string data, string? eventName, out ScannerBridgeEvent? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(data))
            return false;

        try
        {
            parsed = JsonSerializer.Deserialize<ScannerBridgeEvent>(data, JsonOptions);
        }
        catch (JsonException)
        {
            return false;
        }

        if (parsed is null)
            return false;

        if (string.IsNullOrWhiteSpace(parsed.Type))
            parsed = parsed with { Type = eventName ?? "scanner" };
        return true;
    }

    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(JsonOptions, cancellationToken);
            if (payload.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch
        {
            // ignore parse failures
        }

        return null;
    }

    private static async Task<string> ReadPrinterErrorAsync(
        HttpResponseMessage response, CancellationToken cancellationToken) =>
        await ReadErrorAsync(response, cancellationToken)
        ?? $"Impresora no disponible ({(int)response.StatusCode})";

    private async Task<(bool Enabled, string Mode, string Device)> ResolveDrawerConfigAsync(
        CancellationToken cancellationToken)
    {
        var printer = await GetConfiguredPrinterNameAsync(cancellationToken);
        var enabledSetting = (await settings.GetSettingAsync("cajon_habilitado", string.Empty, cancellationToken)).Trim();
        var enabled = string.Equals(enabledSetting, "true", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(enabledSetting, "false", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(printer));

        var mode = (await settings.GetSettingAsync(
            "cajon_modo", configuration["HardwareBridge:DrawerMode"] ?? "printer", cancellationToken)).Trim();
        var device = (await settings.GetSettingAsync(
            "cajon_dispositivo", configuration["HardwareBridge:DrawerDevice"] ?? string.Empty, cancellationToken)).Trim();

        if (string.IsNullOrWhiteSpace(device) && !string.IsNullOrWhiteSpace(printer))
        {
            mode = "printer";
            device = printer;
            await settings.LinkDrawerToPrinterAsync(printer, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(mode))
            mode = "printer";

        return (enabled, mode, device);
    }

    private async Task<HardwareResult> SendRawTicketAsync(
        string printer, string ticket, byte[]? logoBytes, CancellationToken cancellationToken,
        bool isTest = false, bool logoMonochrome = true)
    {
        var paperWidth = 80;
        var widthSetting = await settings.GetSettingAsync("ticket_ancho_mm", string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(widthSetting))
            widthSetting = await settings.GetSettingAsync("ticket_ancho", "80", cancellationToken);
        if (int.TryParse(widthSetting, out var parsed) && parsed is 58 or 80)
            paperWidth = parsed;

        using var request = new HttpRequestMessage(HttpMethod.Post, "api/print/ticket")
        {
            Content = JsonContent.Create(new
            {
                printer,
                text = ticket,
                logoBase64 = logoBytes is { Length: > 0 } ? Convert.ToBase64String(logoBytes) : null,
                paperWidthMm = paperWidth,
                logoMonochrome
            })
        };
        request.Headers.Authorization = new("Bearer", Token);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
            return HardwareResult.Ok(isTest ? "Prueba enviada a la impresora" : "Ticket enviado a la impresora");
        return HardwareResult.Unavailable(await ReadPrinterErrorAsync(response, cancellationToken));
    }

    public async Task<ScaleReadResult> ReadScaleAsync(
        string port,
        string driver = "GENERICA",
        int baudRate = 9600,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(port))
            return ScaleReadResult.Failed("Indica el puerto COM de la báscula.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "api/scale/read")
            {
                Content = JsonContent.Create(new
                {
                    port,
                    driver,
                    baudRate
                })
            };
            request.Headers.Authorization = new("Bearer", Token);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadFromJsonAsync<ScaleReadDto>(JsonOptions, cancellationToken);
            if (body is null)
                return ScaleReadResult.Failed("Respuesta vacía del bridge.");
            if (!response.IsSuccessStatusCode || !body.Success || body.Kilograms is null or <= 0)
                return ScaleReadResult.Failed(body.Error ?? "No se pudo leer el peso.");
            return ScaleReadResult.Ok(body.Kilograms.Value, body.Raw);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogDebug(ex, "Hardware bridge no disponible al leer báscula");
            return ScaleReadResult.Failed("Bridge desconectado");
        }
    }
}

public sealed record HardwareResult(bool Success, string Message)
{
    public static HardwareResult Ok(string message) => new(true, message);
    public static HardwareResult Unavailable(string message) => new(false, message);
}

public sealed record SerialPortDto(string Name);

public sealed record PrinterInfoDto(string Name);

public sealed record ScannerStateDto(bool Connected, string? Port, string? Error = null);

public sealed record ScannerBridgeEvent(
    string Type,
    string? Code = null,
    string? Port = null,
    string? Error = null,
    DateTimeOffset? At = null);

public sealed record ScaleReadDto(
    bool Success,
    decimal? Kilograms,
    string? Raw,
    string? Port,
    string? Driver,
    string? Error);

public sealed record ScaleReadResult(bool Success, string Message, decimal Kilograms = 0, string? Raw = null)
{
    public static ScaleReadResult Ok(decimal kg, string? raw) =>
        new(true, $"Peso: {kg:0.###} kg", kg, raw);
    public static ScaleReadResult Failed(string message) => new(false, message);
}
