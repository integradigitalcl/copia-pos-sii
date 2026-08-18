using GrunflexPOS.API.Data;
using GrunflexPOS.API.Models;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Hosting;

/// <summary>Elimina registros de idempotencia expirados (Completed/Failed).</summary>
public sealed class IdempotencyCleanupHostedService : BackgroundService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<IdempotencyCleanupHostedService> _log;

    public IdempotencyCleanupHostedService(IServiceProvider services, ILogger<IdempotencyCleanupHostedService> log)
    {
        _services = services;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
                await PurgeExpiredAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "idempotency.cleanup_error");
            }
        }
    }

    private async Task PurgeExpiredAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApiDbContext>();
        var now = DateTime.UtcNow;

        var removed = await db.Set<IdempotencyRecord>()
            .Where(x => x.ExpiresAt < now &&
                        (x.Status == IdempotencyRecordStatus.Completed || x.Status == IdempotencyRecordStatus.Failed))
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            _log.LogInformation("idempotency.cleanup purged={Count}", removed);
    }
}
