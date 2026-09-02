using GrunflexPOS.Web.Components;
using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services;
using GrunflexPOS.Web.Services.Diagnostics;
using GrunflexPOS.Web.Services.Licensing;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Infrastructure;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
QuestPDF.Settings.License = LicenseType.Community;

var logDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "GrunflexPOS",
    "logs");
Directory.CreateDirectory(logDirectory);
builder.Logging.AddProvider(new PosFileLoggerProvider(logDirectory));

var detailedErrors =
    builder.Configuration.GetValue("DetailedErrors", true) ||
    builder.Configuration.GetValue("CircuitOptions:DetailedErrors", false) ||
    string.Equals(
        Environment.GetEnvironmentVariable("ASPNETCORE_DETAILEDERRORS"),
        "true",
        StringComparison.OrdinalIgnoreCase);

builder.Services.Configure<CircuitOptions>(options => options.DetailedErrors = detailedErrors);
builder.Services.AddSingleton<CircuitHandler, PosCircuitDiagnostics>();

// Local POS runs on loopback so Chrome can reach the application without
// exposing the cashier UI to the network.
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://127.0.0.1:7373");
var configuredDatabasePath = builder.Configuration["Data:DatabasePath"];
var databasePath = string.IsNullOrWhiteSpace(configuredDatabasePath)
    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GrunflexPOS", "grunflex-pos.db")
    : configuredDatabasePath;
var databaseDirectory = Path.GetDirectoryName(databasePath);
if (!string.IsNullOrWhiteSpace(databaseDirectory))
    Directory.CreateDirectory(databaseDirectory);
builder.Services.AddDbContextFactory<LocalPosDbContext>(options => options.UseSqlite($"Data Source={databasePath}"));
builder.Services.AddScoped<PosSessionState>();
builder.Services.AddSingleton<MulticajaOfflineQueue>();
builder.Services.AddHostedService<MulticajaOfflineReplayService>();
builder.Services.AddScoped<WebLicenseService>();
builder.Services.AddScoped<WebLicenseState>();
builder.Services.AddScoped<LicensingCloudClient>();
builder.Services.AddScoped<CloudBackupService>();
builder.Services.AddHostedService<LicenseSyncHostedService>();
builder.Services.AddHostedService<CloudBackupHostedService>();
builder.Services.AddScoped<LocalPosStore>();
builder.Services.AddScoped<PosEmailService>();
builder.Services.AddScoped<InvoiceEmissionService>();
builder.Services.AddSingleton<BridgeTokenResolver>();
builder.Services.AddHttpClient();
builder.Services.AddScoped<LocalReportsService>();
builder.Services.AddSingleton<ReportsPdfService>();
builder.Services.AddScoped<BoletaPdfService>();
builder.Services.AddScoped<TicketTemplateService>();
builder.Services.AddScoped<PosLogoService>();
builder.Services.AddHttpClient<HardwareBridgeClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["HardwareBridge:Url"] ?? "http://127.0.0.1:7390/");
    client.Timeout = TimeSpan.FromSeconds(8);
});
builder.Services.AddHttpClient<PaymentGatewayClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["PaymentApi:Url"] ?? "http://127.0.0.1:7279/api/pago/");
    client.Timeout = TimeSpan.FromSeconds(25);
});
builder.Services.AddHttpClient<MulticajaClient>(client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Multicaja:ApiBaseUrl"] ?? "http://127.0.0.1:7279/");
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

try
{
    await MulticajaInstallBootstrap.ApplyAsync(
        app.Services,
        app.Configuration,
        app.Logger);
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "No se pudo aplicar la configuración multicaja al iniciar.");
}

app.Logger.LogInformation(
    "Grunflex POS Web iniciado. DetailedErrors={DetailedErrors}. Log={LogDirectory}",
    detailedErrors,
    logDirectory);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp =>
    {
        errorApp.Run(async context =>
        {
            var feature = context.Features.Get<IExceptionHandlerFeature>();
            if (feature?.Error is not null)
            {
                var logger = context.RequestServices
                    .GetRequiredService<ILoggerFactory>()
                    .CreateLogger("GrunflexPOS.Web.Unhandled");
                logger.LogError(feature.Error, "Error no controlado procesando {Path}", context.Request.Path);
            }

            context.Response.Redirect("/Error");
            await Task.CompletedTask;
        });
    });
}

app.UseStaticFiles();
app.UseAntiforgery();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "grunflex-pos-web" }));
app.MapGet("/api/health", () => Results.Ok(new
{
    service = "GrunflexPOS.Web",
    status = "ok",
    mode = "local",
    timestamp = DateTimeOffset.UtcNow
}));
app.MapGet("/api/pos-logo", async (PosLogoService logos, CancellationToken cancellationToken) =>
{
    var bytes = await logos.GetLogoBytesAsync(cancellationToken);
    if (bytes is null || bytes.Length == 0)
        return Results.NotFound();
    return Results.File(bytes, "image/png", enableRangeProcessing: false);
});

var invoiceMockInbox = new System.Collections.Concurrent.ConcurrentQueue<object>();
app.MapPost("/api/invoice/receive", async (HttpRequest request) =>
{
    using var doc = await JsonDocument.ParseAsync(request.Body);
    var root = doc.RootElement.Clone();
    var requestId = root.TryGetProperty("requestId", out var rid) ? rid.GetString() : null;
    if (string.IsNullOrWhiteSpace(requestId))
        requestId = Guid.NewGuid().ToString("N");
    var ticket = root.TryGetProperty("ticketNumber", out var tn) && tn.TryGetInt64(out var ticketNumber)
        ? ticketNumber
        : 0L;
    var mockId = requestId.Length >= 8 ? requestId[..8] : requestId;
    var response = new
    {
        success = true,
        providerDocumentId = "MOCK-" + mockId.ToUpperInvariant(),
        folio = ticket > 0 ? ticket.ToString() : DateTime.UtcNow.ToString("yyyyMMddHHmmss"),
        trackId = requestId,
        message = "Documento aceptado por receptor mock local."
    };
    invoiceMockInbox.Enqueue(new { receivedAtUtc = DateTimeOffset.UtcNow, payload = root, response });
    return Results.Ok(response);
});
app.MapGet("/api/invoice/receive", () => Results.Ok(new
{
    service = "invoice-mock-receiver",
    status = "ok",
    pending = invoiceMockInbox.Count,
    hint = "POST sale documents to this URL from Config → Facturación."
}));
app.MapGet("/api/invoice/mock-inbox", () => Results.Ok(invoiceMockInbox.ToArray()));

app.MapGet("/api/bridge/health", async (HardwareBridgeClient bridge) =>
{
    var result = await bridge.CheckHealthAsync();
    return Results.Ok(new
    {
        service = "GrunflexPOS.HardwareBridge",
        status = result.Success ? "ok" : "unavailable",
        message = result.Message
    });
});
app.MapGet("/api/catalog", async (LocalPosStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetProductsAsync(cancellationToken)));
app.MapGet("/api/dashboard", async (LocalPosStore store, CancellationToken cancellationToken) =>
    Results.Ok(await store.GetDashboardAsync(cancellationToken)));
app.MapPost("/api/sales", async (CreateSaleRequest request, LocalPosStore store, CancellationToken cancellationToken) =>
{
    var products = await store.GetProductsAsync(cancellationToken);
    var byCode = products.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
    var items = new List<CartItem>();
    foreach (var item in request.Items)
    {
        if (!byCode.TryGetValue(item.Code, out var product) || item.Quantity <= 0)
            return Results.BadRequest(new { error = $"Producto o cantidad inválida: {item.Code}" });
        items.Add(new CartItem(product, item.Quantity));
    }

    var result = await store.RecordSaleAsync(items, request.UserName ?? "api", request.PaymentMethod ?? "Efectivo",
        cancellationToken: cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (args.Contains("--bootstrap-first-run", StringComparer.OrdinalIgnoreCase) ||
    string.Equals(
        Environment.GetEnvironmentVariable("GRUNFLEX_BOOTSTRAP"),
        "1",
        StringComparison.OrdinalIgnoreCase))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<LocalPosStore>();
        await store.EnsureCreatedAsync();
        app.Logger.LogInformation("Bootstrap first-run OK. Database={DatabasePath}", databasePath);
        Environment.ExitCode = 0;
        return;
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Bootstrap first-run failed for {DatabasePath}", databasePath);
        Environment.ExitCode = 1;
        return;
    }
}

app.Run();

public sealed record CreateSaleRequest(IReadOnlyCollection<CreateSaleItem> Items, string? UserName, string? PaymentMethod);
public sealed record CreateSaleItem(string Code, decimal Quantity);