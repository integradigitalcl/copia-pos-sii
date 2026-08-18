using PosEdge.Infrastructure;

namespace PosEdge.Api.Ops;

public sealed class OpsMetricsWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<OpsMetricsWorker> _log;

    public OpsMetricsWorker(IServiceProvider sp, ILogger<OpsMetricsWorker> log)
    {
        _sp = sp;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("POSEDGE_OPS_METRICS_SECONDS"), out var s) && s > 0 ? s : 5;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<PosEdgeDbContext>();
                await OpsMetrics.UpdateOnceAsync(db, stoppingToken);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ops metrics update failed");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken); } catch { }
        }
    }
}

