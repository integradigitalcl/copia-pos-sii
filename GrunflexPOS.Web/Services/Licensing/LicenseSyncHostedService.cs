namespace GrunflexPOS.Web.Services.Licensing;

public sealed class LicenseSyncHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<LicenseSyncHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(12), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<LocalPosStore>();
                var activationId = (await store.GetSettingAsync("licencia_activation_id", cancellationToken: stoppingToken)).Trim();
                if (!string.IsNullOrWhiteSpace(activationId))
                {
                    var client = scope.ServiceProvider.GetRequiredService<LicensingCloudClient>();
                    var result = await client.RefreshAsync(silent: true, stoppingToken);
                    if (result.Ok)
                        logger.LogInformation("Licencia sincronizada con servidor.");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Sync periódico de licencia omitido");
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
