using System.IO;
using System.Text;
using System.Threading.RateLimiting;
using GrunflexPOS.API.Configuration;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.Inventory;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Middleware;
using GrunflexPOS.API.Security;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Hosting.WindowsServices;
using Prometheus;
using Serilog;
using Microsoft.OpenApi.Models;

// Si se invoca con argumentos especiales, salimos antes de crear el host.
// Esto permite que el instalador haga: GrunflexPOS.API.exe --healthcheck para diagnóstico.
if (args.Length > 0 && args[0].Equals("--healthcheck", StringComparison.OrdinalIgnoreCase))
{
    Console.WriteLine("Grunflex POS API healthcheck OK");
    return;
}

// Migración legacy LocalAppData -> ProgramData (solo en modo servicio).
ApiPaths.MigrateLegacyDataIfNeeded();

var builderOptions = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};

var builder = WebApplication.CreateBuilder(builderOptions);

// Habilita el modo Windows Service. El host detecta automáticamente si fue invocado por SCM;
// en modo consola sigue funcionando sin cambios (compat hacia atrás).
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "GrunflexPOSAPI";
});

// Clave JWT y contraseña admin: generadas en el primer arranque (api.secrets.json).
ApiLocalSecretsBootstrap.EnsureFileAndRegister(builder);

if (builder.Environment.IsDevelopment())
    builder.Configuration.AddUserSecrets<Program>(optional: true);

builder.Configuration.AddEnvironmentVariables(prefix: "GRUNFLEX_");

// Logging Serilog con sink de archivo apuntando a la carpeta correcta (Service o Console).
if (builder.Environment.IsEnvironment("Testing"))
{
    Log.Logger = new LoggerConfiguration()
        .MinimumLevel.Warning()
        .WriteTo.Console()
        .CreateLogger();
}
else
{
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(builder.Configuration)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithThreadId()
        .Enrich.WithProperty("Mode", ApiPaths.IsRunningAsWindowsService() ? "Service" : "Console")
        .WriteTo.File(
            path: Path.Combine(ApiPaths.LogsDirectory, "grunflex-api-.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 14,
            fileSizeLimitBytes: 20_000_000,
            rollOnFileSizeLimit: true,
            shared: true,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] [{Mode}] {Message:lj}{NewLine}{Exception}")
        .CreateLogger();
}

builder.Host.UseSerilog();

// Fase 5.2: HTTPS local opcional. Si Api:HttpsEnabled=true en appsettings, agregamos un
// endpoint HTTPS en puerto 7280 usando cert self-signed persistido. El HTTP en 7279
// sigue activo (compat). No requiere instalación de cert raíz para uso loopback;
// clientes en LAN pueden pinear el thumbprint.
var httpsEnabled = builder.Configuration.GetValue<bool>("Api:HttpsEnabled");
var httpsPort = builder.Configuration.GetValue<int>("Api:HttpsPort", 7280);
if (httpsEnabled)
{
    try
    {
        var cert = GrunflexPOS.API.Security.LocalHttpsCertificate.EnsureCertificate();
        builder.WebHost.ConfigureKestrel(opts =>
        {
            opts.ListenAnyIP(httpsPort, lo => lo.UseHttps(cert));
        });
        Console.WriteLine($"[Grunflex] HTTPS habilitado en puerto {httpsPort} con cert thumbprint {cert.Thumbprint}.");
    }
    catch (Exception ex)
    {
        Console.WriteLine("[Grunflex] No se pudo habilitar HTTPS local: " + ex.Message);
    }
}

var connectionString =
    builder.Configuration.GetConnectionString("Default") ??
    builder.Configuration["ConnectionStrings:Default"];

static bool EsSqlite(string? cs)
{
    if (string.IsNullOrWhiteSpace(cs))
        return false;
    var t = cs.Trim();
    return t.StartsWith("Data Source", StringComparison.OrdinalIgnoreCase)
           || t.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);
}

if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = $"Data Source={ApiPaths.SqlitePath};Cache=Shared";
}

builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<LicensingOptions>(builder.Configuration.GetSection(LicensingOptions.SectionName));
var jwt = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
if (string.IsNullOrWhiteSpace(jwt.SigningKey))
    throw new InvalidOperationException("Jwt:SigningKey no configurado.");

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();

if (EsSqlite(connectionString))
    builder.Services.AddDbContext<ApiDbContext>(options => options.UseSqlite(connectionString));
else
    builder.Services.AddDbContext<ApiDbContext>(options => options.UseNpgsql(connectionString));

builder.Services.AddSingleton<PosCommerceConnectionResolver>();
builder.Services.AddDbContext<PosCommerceDbContext>((sp, opts) =>
{
    var resolver = sp.GetRequiredService<PosCommerceConnectionResolver>();
    opts.UseSqlite(resolver.GetConnectionString());
});
builder.Services.AddScoped<MulticajaVentaProcessor>();
builder.Services.AddScoped<MulticajaAnulacionProcessor>();
builder.Services.AddScoped<MulticajaDevolucionProcessor>();
builder.Services.AddScoped<MulticajaInventarioProcessor>();
builder.Services.AddScoped<MulticajaCierreCajaProcessor>();
builder.Services.AddScoped<MulticajaMovimientoCajaProcessor>();
builder.Services.AddScoped<MulticajaCajaSesionesService>();
builder.Services.AddScoped<MulticajaSharedSecretFilter>();
builder.Services.AddScoped<MulticajaLicenseModuleFilter>();
builder.Services.AddScoped<LicenseSlotService>();
builder.Services.AddScoped<TerminalIdentityService>();
builder.Services.AddScoped<TerminalHeartbeatService>();
builder.Services.AddScoped<SyncChangeRecorder>();
builder.Services.AddSingleton<IMulticajaSyncNotifier, MulticajaSyncNotifier>();
builder.Services.AddSignalR();
builder.Services.AddScoped<IIdempotencyContextAccessor, IdempotencyContextAccessor>();
builder.Services.AddScoped<IIdempotencyService, IdempotencyService>();
builder.Services.AddScoped<MulticajaIdempotencyRunner>();
builder.Services.AddScoped<CommerceActiveSessionValidator>();
builder.Services.AddScoped<InventoryLockService>();
builder.Services.AddScoped<InventoryMovementService>();
builder.Services.AddScoped<InventoryTransactionService>();
builder.Services.AddScoped<InventoryReservationService>();
builder.Services.AddScoped<InventoryRetryPolicy>();

builder.Services.AddScoped<PagoService>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<RefreshTokenService>();
builder.Services.AddDataProtection();
builder.Services.AddSingleton<SensitiveDataEncryptionService>();
builder.Services.AddScoped<LicensingIssueService>();
builder.Services.AddSingleton<TenantBackupStorage>();

// Fase 2.5: discovery beacon UDP en LAN para que las cajas adicionales encuentren al servidor sin IP manual.
builder.Services.AddHostedService<DiscoveryBeaconService>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKeyResolver = (_, _, _, _) =>
            {
                var keys = new List<SecurityKey>
                {
                    new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey))
                };
                foreach (var previous in jwt.PreviousSigningKeys.Where(x => !string.IsNullOrWhiteSpace(x)))
                    keys.Add(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(previous)));
                return keys;
            },
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("ApiOperator", p => p.RequireRole("admin", "api-user"));
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 20,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            }));
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<ApiDbContext>("database");

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "GrunflexPOS API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "JWT Bearer token"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

builder.Services.AddHostedService<GrunflexPOS.API.Hosting.DatabaseSchemaInitializer>();
builder.Services.AddHostedService<GrunflexPOS.API.Hosting.ApiLicensingStartupCheck>();
builder.Services.AddHostedService<GrunflexPOS.API.Hosting.IdempotencyCleanupHostedService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
else
{
    app.UseHsts();
}

app.UseSerilogRequestLogging();
app.UseMiddleware<IdempotencyHeadersMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<LicenseIssuerApiKeyMiddleware>();
app.UseRateLimiter();
app.UseHttpMetrics();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");
// Sin base de datos: el emisor de licencias y otras herramientas locales usan esto para no cancelar por timeout.
app.MapGet("/health/live", static () => Results.Ok())
    .AllowAnonymous()
    .WithTags("Health");
app.MapGet("/health/ready", async (
    ApiDbContext apiDb,
    PosCommerceDbContext posDb,
    CancellationToken ct) =>
{
    try
    {
        var apiOk = await apiDb.Database.CanConnectAsync(ct);
        var posOk = await posDb.Database.CanConnectAsync(ct);
        if (!apiOk || !posOk)
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        return Results.Ok(new { api = apiOk, commerce = posOk, ready = true });
    }
    catch
    {
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
    .AllowAnonymous()
    .WithTags("Health");
app.MapHub<GrunflexPOS.API.Hubs.MulticajaSyncHub>("/hubs/multicaja-sync");
app.MapMetrics("/metrics");
app.MapControllers();

app.Run();

public partial class Program { }