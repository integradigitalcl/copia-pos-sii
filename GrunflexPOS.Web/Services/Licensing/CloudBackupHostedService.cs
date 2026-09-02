namespace GrunflexPOS.Web.Services.Licensing;

public sealed class CloudBackupHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<CloudBackupHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(8));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var license = scope.ServiceProvider.GetRequiredService<WebLicenseState>();
                await license.RefreshAsync(stoppingToken);
                if (!license.CloudBackup)
                    continue;

                var backup = scope.ServiceProvider.GetRequiredService<CloudBackupService>();
                var result = await backup.TryUploadOnceAsync(stoppingToken);
                if (result.Ok)
                    logger.LogInformation("Respaldo en nube automático completado.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogDebug(ex, "Respaldo en nube automático omitido");
            }
        }
    }
}
