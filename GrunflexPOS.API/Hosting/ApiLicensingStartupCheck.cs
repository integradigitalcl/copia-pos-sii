using GrunflexPOS.API.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace GrunflexPOS.API.Hosting;

/// <summary>Valida al arranque que la API pueda emitir/renovar licencias (RSA configurada).</summary>
public sealed class ApiLicensingStartupCheck : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ApiLicensingStartupCheck> _log;

    public ApiLicensingStartupCheck(IServiceProvider services, ILogger<ApiLicensingStartupCheck> log)
    {
        _services = services;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _services.CreateScope();
        var issue = scope.ServiceProvider.GetRequiredService<LicensingIssueService>();
        if (!issue.IsConfigured)
        {
            _log.LogWarning(
                "Licensing:PrivateKeyPem no configurada. " +
                "POST /api/licensing/activate y /refresh devolverán 503 hasta copiar claves del LicenseIssuer a api.secrets.json.");
        }
        else
        {
            _log.LogInformation("Licencias: clave RSA de firma configurada (activación/renovación online habilitadas).");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
