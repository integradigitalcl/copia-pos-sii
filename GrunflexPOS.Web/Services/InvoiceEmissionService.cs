using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using GrunflexPOS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.Web.Services;

public sealed class InvoiceEmissionService(
    LocalPosStore store,
    IHttpClientFactory httpClientFactory,
    ILogger<InvoiceEmissionService> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record InvoiceSettings(
        bool Enabled,
        string Endpoint,
        string ApiKey,
        string DocumentType,
        string SellerRut,
        string SellerName,
        string SellerAddress,
        string SellerEmail,
        decimal IvaPercent,
        bool PricesIncludeTax);

    public sealed record BuyerInfo(string Rut, string Name, string Email);

    public async Task<InvoiceSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        var enabled = string.Equals(await store.GetSettingAsync("facturacion_activa", "false", cancellationToken),
            "true", StringComparison.OrdinalIgnoreCase);
        var endpoint = (await store.GetSettingAsync("facturacion_endpoint", cancellationToken: cancellationToken)).Trim();
        var apiKey = await store.GetSettingAsync("facturacion_api_key", cancellationToken: cancellationToken);
        var docType = (await store.GetSettingAsync("facturacion_tipo_documento", "boleta", cancellationToken)).Trim().ToLowerInvariant();
        if (docType is not ("boleta" or "factura"))
            docType = "boleta";
        var ivaRaw = await store.GetSettingAsync("facturacion_iva_porcentaje", "19", cancellationToken);
        var iva = decimal.TryParse(ivaRaw.Replace(",", "."), NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
            ? p
            : 19m;
        var includeTax = !string.Equals(
            await store.GetSettingAsync("facturacion_precios_con_iva", "true", cancellationToken),
            "false", StringComparison.OrdinalIgnoreCase);

        return new InvoiceSettings(
            enabled,
            endpoint,
            apiKey,
            docType,
            (await store.GetSettingAsync("facturacion_emisor_rut", cancellationToken: cancellationToken)).Trim(),
            (await store.GetSettingAsync("facturacion_emisor_razon", cancellationToken: cancellationToken)).Trim(),
            (await store.GetSettingAsync("facturacion_emisor_direccion", cancellationToken: cancellationToken)).Trim(),
            (await store.GetSettingAsync("facturacion_emisor_email", cancellationToken: cancellationToken)).Trim(),
            iva,
            includeTax);
    }

    public async Task<(bool Ok, string Message)> ProbeAsync(string? endpointOverride = null, CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        var endpoint = string.IsNullOrWhiteSpace(endpointOverride) ? settings.Endpoint : endpointOverride!.Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return (false, "Ingrese la URL del proveedor de facturación.");

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return (false, "La URL debe comenzar con http:// o https://");

        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(12);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.ApiKey);
            using var response = await client.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return (true, $"Endpoint respondió {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        catch (TaskCanceledException)
        {
            return (false, "Tiempo de espera agotado al contactar el endpoint.");
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    public async Task<(bool Ok, string Message)> EmitTestAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            return (false, "Configure el endpoint antes de probar.");

        var sampleProduct = new PosProduct(0, "TEST-001", "Producto de prueba facturación", "Test", 1000m, 1, "un.", "#2563EB");
        var items = new[] { new CartItem(sampleProduct, 1) };
        return await EmitSaleAsync(
            ticketNumber: 0,
            items,
            total: 1000m,
            paymentMethod: "Efectivo",
            userName: "test",
            documentTypeOverride: settings.DocumentType,
            buyer: null,
            isTest: true,
            cancellationToken);
    }

    public async Task<(bool Ok, string Message)> EmitSaleAsync(
        long ticketNumber,
        IReadOnlyCollection<CartItem> items,
        decimal total,
        string paymentMethod,
        string userName,
        string? documentTypeOverride = null,
        BuyerInfo? buyer = null,
        bool isTest = false,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (!settings.Enabled && !isTest)
            return (false, "Facturación desactivada.");
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            return (false, "Endpoint de facturación no configurado.");

        var documentType = (documentTypeOverride ?? settings.DocumentType).Trim().ToLowerInvariant();
        if (documentType is not ("boleta" or "factura"))
            documentType = "boleta";

        if (documentType == "factura" &&
            (buyer is null || string.IsNullOrWhiteSpace(buyer.Rut) || string.IsNullOrWhiteSpace(buyer.Name)))
            return (false, "La factura requiere RUT y razón social del receptor.");

        var requestId = Guid.NewGuid().ToString("N");
        var tax = ComputeTax(total, settings.IvaPercent, settings.PricesIncludeTax);
        var payload = new InvoiceEmitRequest
        {
            RequestId = requestId,
            Test = isTest,
            DocumentType = documentType,
            TicketNumber = ticketNumber,
            Folio = ticketNumber,
            IssuedAtUtc = DateTimeOffset.UtcNow,
            Currency = "CLP",
            PaymentMethod = paymentMethod,
            Cashier = userName,
            Seller = new InvoiceParty
            {
                Rut = settings.SellerRut,
                BusinessName = settings.SellerName,
                Address = settings.SellerAddress,
                Email = settings.SellerEmail
            },
            Buyer = buyer is null
                ? null
                : new InvoiceParty
                {
                    Rut = buyer.Rut.Trim(),
                    BusinessName = buyer.Name.Trim(),
                    Email = buyer.Email.Trim()
                },
            Lines = items.Select(x =>
            {
                var unit = x.EffectiveUnitPrice;
                var lineTotal = Math.Round(unit * x.Quantity * (1m - x.DiscountPercentage / 100m), 2, MidpointRounding.AwayFromZero);
                return new InvoiceLineDto
                {
                    Code = x.Product.Code,
                    Name = x.Product.Name,
                    Quantity = x.Quantity,
                    UnitPrice = unit,
                    DiscountPercent = x.DiscountPercentage,
                    Total = lineTotal
                };
            }).ToList(),
            Totals = new InvoiceTotalsDto
            {
                Net = tax.Net,
                Tax = tax.Tax,
                TaxPercent = settings.IvaPercent,
                Discount = Math.Max(0, items.Sum(x =>
                    Math.Round(x.EffectiveUnitPrice * x.Quantity * (x.DiscountPercentage / 100m), 2, MidpointRounding.AwayFromZero))),
                Total = total
            }
        };

        var payloadJson = JsonSerializer.Serialize(payload, JsonOptions);
        try
        {
            using var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(25);
            using var request = new HttpRequestMessage(HttpMethod.Post, settings.Endpoint)
            {
                Content = JsonContent.Create(payload, options: JsonOptions)
            };
            if (!string.IsNullOrWhiteSpace(settings.ApiKey))
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + settings.ApiKey);
            request.Headers.TryAddWithoutValidation("X-Request-Id", requestId);

            using var response = await client.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            InvoiceEmitResponse? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<InvoiceEmitResponse>(responseText, JsonOptions);
            }
            catch (JsonException)
            {
                // provider may return non-JSON
            }

            var ok = response.IsSuccessStatusCode && (parsed?.Success ?? true);
            var message = parsed?.Message
                ?? (ok
                    ? $"Documento {documentType} emitido ({(int)response.StatusCode})."
                    : $"Proveedor respondió {(int)response.StatusCode}: {Trim(responseText, 180)}");

            if (!string.IsNullOrWhiteSpace(parsed?.ProviderDocumentId))
                message += $" · Id {parsed.ProviderDocumentId}";
            if (!string.IsNullOrWhiteSpace(parsed?.Folio))
                message += $" · Folio {parsed.Folio}";

            if (!isTest)
            {
                await store.SaveInvoiceEmissionAsync(new LocalInvoiceEmission
                {
                    TicketNumber = ticketNumber,
                    RequestId = requestId,
                    DocumentType = documentType,
                    Status = ok ? "ok" : "error",
                    ProviderDocumentId = parsed?.ProviderDocumentId ?? string.Empty,
                    ProviderFolio = parsed?.Folio ?? string.Empty,
                    Message = message,
                    PayloadJson = payloadJson,
                    ResponseJson = Trim(responseText, 4000),
                    CreatedAtUtc = DateTime.UtcNow
                }, cancellationToken);
            }

            return (ok, message);
        }
        catch (Exception ex)
        {
            var message = "Error al emitir: " + ex.GetBaseException().Message;
            logger.LogWarning(ex, "Emisión de facturación falló para ticket {Ticket}", ticketNumber);
            if (!isTest)
            {
                await store.SaveInvoiceEmissionAsync(new LocalInvoiceEmission
                {
                    TicketNumber = ticketNumber,
                    RequestId = requestId,
                    DocumentType = documentType,
                    Status = "error",
                    Message = message,
                    PayloadJson = payloadJson,
                    ResponseJson = string.Empty,
                    CreatedAtUtc = DateTime.UtcNow
                }, cancellationToken);
            }
            return (false, message);
        }
    }

    private static (decimal Net, decimal Tax) ComputeTax(decimal total, decimal ivaPercent, bool pricesIncludeTax)
    {
        if (ivaPercent <= 0)
            return (total, 0);
        if (pricesIncludeTax)
        {
            var net = Math.Round(total / (1m + ivaPercent / 100m), 2, MidpointRounding.AwayFromZero);
            return (net, Math.Round(total - net, 2, MidpointRounding.AwayFromZero));
        }

        var tax = Math.Round(total * (ivaPercent / 100m), 2, MidpointRounding.AwayFromZero);
        return (total, tax);
    }

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}

public sealed class InvoiceEmitRequest
{
    public string RequestId { get; set; } = string.Empty;
    public bool Test { get; set; }
    public string DocumentType { get; set; } = "boleta";
    public long TicketNumber { get; set; }
    public long Folio { get; set; }
    public DateTimeOffset IssuedAtUtc { get; set; }
    public string Currency { get; set; } = "CLP";
    public string PaymentMethod { get; set; } = string.Empty;
    public string Cashier { get; set; } = string.Empty;
    public InvoiceParty Seller { get; set; } = new();
    public InvoiceParty? Buyer { get; set; }
    public List<InvoiceLineDto> Lines { get; set; } = [];
    public InvoiceTotalsDto Totals { get; set; } = new();
}

public sealed class InvoiceParty
{
    public string Rut { get; set; } = string.Empty;
    public string BusinessName { get; set; } = string.Empty;
    public string Address { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
}

public sealed class InvoiceLineDto
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal Total { get; set; }
}

public sealed class InvoiceTotalsDto
{
    public decimal Net { get; set; }
    public decimal Tax { get; set; }
    public decimal TaxPercent { get; set; }
    public decimal Discount { get; set; }
    public decimal Total { get; set; }
}

public sealed class InvoiceEmitResponse
{
    public bool Success { get; set; } = true;
    public string? ProviderDocumentId { get; set; }
    public string? Folio { get; set; }
    public string? TrackId { get; set; }
    public string? PdfUrl { get; set; }
    public string? Message { get; set; }
}
