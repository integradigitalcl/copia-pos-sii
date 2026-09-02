namespace GrunflexPOS.Web.Services;

public sealed class MulticajaOfflineReplayService(
    IServiceScopeFactory scopeFactory,
    ILogger<MulticajaOfflineReplayService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var multicaja = scope.ServiceProvider.GetRequiredService<MulticajaClient>();
                if (!await multicaja.IsEnabledAsync(stoppingToken) || multicaja.PendingOfflineCount == 0)
                    continue;

                var replay = await multicaja.ReplayPendingAsync(stoppingToken);
                if (replay.Succeeded > 0)
                    logger.LogInformation("Replay multicaja: {Succeeded} sincronizada(s), {Remaining} pendiente(s)",
                        replay.Succeeded, replay.Remaining);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Replay periódico multicaja omitido");
            }
        }
    }
}
