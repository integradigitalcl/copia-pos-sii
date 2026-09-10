using System.IO.Ports;
using System.Net;
using System.Text.Json;
using GrunflexPOS.HardwareBridge.Configuration;
using GrunflexPOS.HardwareBridge.Hardware;
using GrunflexPOS.HardwareBridge.Models;
using GrunflexPOS.HardwareBridge.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

// The bridge is intentionally local-only. Do not make this configurable.
builder.WebHost.ConfigureKestrel(options =>
    options.Listen(IPAddress.Loopback, 7390));
builder.Services.Configure<HardwareBridgeOptions>(
    builder.Configuration.GetSection(HardwareBridgeOptions.SectionName));
builder.Services.AddSingleton<BridgeTokenStore>();
builder.Services.AddSingleton<PrinterService>();
builder.Services.AddSingleton<DrawerService>();
builder.Services.AddSingleton<ScannerService>();
builder.Services.AddSingleton<ScaleService>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("localhost-only", policy =>
    {
        policy.SetIsOriginAllowed(LocalOriginPolicy.IsAllowed)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});
builder.Services
    .AddAuthentication("BridgeToken")
    .AddScheme<AuthenticationSchemeOptions, BridgeTokenAuthenticationHandler>(
        "BridgeToken", _ => { });
builder.Services.AddAuthorization();
builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
});

var app = builder.Build();

// Fail startup rather than running with an unknown or ephemeral production token.
app.Services.GetRequiredService<BridgeTokenStore>().GetToken();

app.UseCors("localhost-only");
app.Use(async (context, next) =>
{
    var origin = context.Request.Headers.Origin.ToString();
    if (!LocalOriginPolicy.IsAllowed(origin))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Only localhost origins are allowed." });
        return;
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "GrunflexPOS.HardwareBridge",
    version = "1",
    listen = "http://127.0.0.1:7390"
})).RequireAuthorization();

app.MapGet("/api/printers", (PrinterService printers) =>
    Results.Ok(printers.ListPrinters().Select(x => new PrinterInfo(x))))
    .RequireAuthorization();

app.MapGet("/api/serial-ports", () =>
    Results.Ok(SerialPort.GetPortNames()
        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
        .Select(x => new SerialPortInfo(x))))
    .RequireAuthorization();

app.MapPost("/api/print/raw", (PrintJobRequest? request,
    PrinterService printers,
    IOptions<HardwareBridgeOptions> options) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Printer) ||
        string.IsNullOrWhiteSpace(request.DataBase64))
    {
        return Results.BadRequest(new { error = "printer and dataBase64 are required." });
    }

    try
    {
        var maxBytes = options.Value.MaxPrintBytes;
        if (maxBytes is < 1 or > 16 * 1024 * 1024)
            return Results.Problem("HardwareBridge:MaxPrintBytes is invalid.", statusCode: 500);

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(request.DataBase64);
        }
        catch (FormatException)
        {
            return Results.BadRequest(new { error = "dataBase64 is not valid base64." });
        }

        if (bytes.Length == 0 || bytes.Length > maxBytes)
            return Results.BadRequest(new { error = $"Print data must be 1..{maxBytes} bytes." });

        printers.PrintRaw(request.Printer, bytes);
        return Results.Ok(new { accepted = true, bytes = bytes.Length });
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.MapPost("/api/print/text", (PrintTextRequest? request, PrinterService printers) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Printer) ||
        string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "printer and text are required." });
    }

    try
    {
        byte[]? logoBytes = null;
        if (!string.IsNullOrWhiteSpace(request.LogoBase64))
        {
            try
            {
                logoBytes = Convert.FromBase64String(request.LogoBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "logoBase64 is not valid base64." });
            }
        }

        var paperWidth = request.PaperWidthMm is 58 or 80 ? request.PaperWidthMm.Value : 80;
        printers.PrintText(request.Printer, request.Text, logoBytes, request.LogoMonochrome ?? true, paperWidth);
        return Results.Ok(new { accepted = true, lines = request.Text.Split('\n').Length });
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.MapPost("/api/print/ticket", (PrintTicketRequest? request, PrinterService printers) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Printer) ||
        string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.BadRequest(new { error = "printer and text are required." });
    }

    try
    {
        byte[]? logoBytes = null;
        if (!string.IsNullOrWhiteSpace(request.LogoBase64))
        {
            try
            {
                logoBytes = Convert.FromBase64String(request.LogoBase64);
            }
            catch (FormatException)
            {
                return Results.BadRequest(new { error = "logoBase64 is not valid base64." });
            }
        }

        var paperWidth = request.PaperWidthMm is 58 or 80 ? request.PaperWidthMm.Value : 80;
        printers.PrintEscPosTicket(request.Printer, request.Text, logoBytes, paperWidth, request.LogoMonochrome ?? true);
        return Results.Ok(new { accepted = true, lines = request.Text.Split('\n').Length });
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.MapPost("/api/drawer/open", (DrawerOpenRequest? request, DrawerService drawer) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Mode) ||
        string.IsNullOrWhiteSpace(request.Device))
    {
        return Results.BadRequest(new { error = "mode and device are required." });
    }

    try
    {
        switch (request.Mode.Trim().ToLowerInvariant())
        {
            case "com":
                drawer.OpenByCom(request.Device);
                break;
            case "printer":
            case "raw-printer":
                drawer.OpenByPrinter(request.Device);
                break;
            default:
                return Results.BadRequest(new { error = "mode must be 'com' or 'printer'." });
        }

        return Results.Ok(new { opened = true, mode = request.Mode.Trim().ToLowerInvariant() });
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.MapGet("/api/scanner/state", (ScannerService scanner) =>
    Results.Ok(scanner.GetState()))
    .RequireAuthorization();

app.MapPost("/api/scanner/connect", (ScannerConnectRequest? request,
    ScannerService scanner,
    IOptions<HardwareBridgeOptions> options) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Port))
        return Results.BadRequest(new { error = "port is required." });

    try
    {
        var defaults = options.Value.Scanner;
        var state = scanner.Connect(
            request.Port,
            HardwareValidation.RequireBaudRate(request.BaudRate ?? defaults.BaudRate),
            HardwareValidation.RequireDataBits(request.DataBits ?? defaults.DataBits),
            HardwareValidation.ParseEnum(request.Parity, HardwareValidation.ParseEnum(
                defaults.Parity, Parity.None)),
            HardwareValidation.ParseEnum(request.StopBits, HardwareValidation.ParseEnum(
                defaults.StopBits, StopBits.One)),
            HardwareValidation.ParseEnum(request.Handshake, HardwareValidation.ParseEnum(
                defaults.Handshake, Handshake.None)));
        return Results.Ok(state);
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.MapPost("/api/scanner/disconnect", (ScannerService scanner) =>
    Results.Ok(scanner.Disconnect()))
    .RequireAuthorization();

app.MapGet("/api/scanner/events", async (
    HttpResponse response,
    ScannerService scanner,
    CancellationToken cancellationToken) =>
{
    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache";
    response.Headers.Connection = "keep-alive";

    await foreach (var scannerEvent in scanner.Subscribe(cancellationToken))
    {
        var json = JsonSerializer.Serialize(scannerEvent);
        await response.WriteAsync($"event: scanner\ndata: {json}\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);
    }
}).RequireAuthorization();

app.MapPost("/api/scale/read", (ScaleReadRequest? request,
    ScaleService scale,
    IOptions<HardwareBridgeOptions> options) =>
{
    if (request is null || string.IsNullOrWhiteSpace(request.Port))
        return Results.BadRequest(new { error = "port is required." });

    try
    {
        var defaults = options.Value.Scanner;
        var result = scale.ReadWeight(
            request.Port,
            request.Driver ?? "GENERICA",
            HardwareValidation.RequireBaudRate(request.BaudRate ?? defaults.BaudRate),
            HardwareValidation.RequireDataBits(request.DataBits ?? defaults.DataBits),
            HardwareValidation.ParseEnum(request.Parity, HardwareValidation.ParseEnum(
                defaults.Parity, Parity.None)),
            HardwareValidation.ParseEnum(request.StopBits, HardwareValidation.ParseEnum(
                defaults.StopBits, StopBits.One)));
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex)
    {
        return HardwareError(ex);
    }
}).RequireAuthorization();

app.Run();

static IResult HardwareError(Exception exception) =>
    exception switch
    {
        ArgumentException or ArgumentOutOfRangeException or InvalidOperationException =>
            Results.BadRequest(new { error = exception.Message }),
        _ => Results.Problem("The hardware operation failed.", statusCode: 502)
    };
