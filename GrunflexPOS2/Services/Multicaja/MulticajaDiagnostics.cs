using System.Text;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>Volcado legible de estado multicaja para soporte (log único <c>multicaja.diagnostics</c>).</summary>
public static class MulticajaDiagnostics
{
    public static void WriteStartupDump()
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        var cfg = AppConfig.Cargar();
        var (pending, bytes) = OfflineQueue.GetDiskMetrics();
        var sb = new StringBuilder();
        sb.AppendLine("multicaja.diagnostics === arranque API-only ===");
        sb.AppendLine($"machine={Environment.MachineName} utc={DateTime.UtcNow:O}");
        sb.AppendLine($"apiBaseUrl={cfg.ApiBaseUrl}");
        sb.AppendLine($"cajaId={cfg.CajaId}");
        sb.AppendLine($"requireSecret={cfg.MulticajaRequireSharedSecret} hasSecret={!string.IsNullOrWhiteSpace(cfg.MulticajaSharedSecret)}");
        sb.AppendLine($"blockCriticalOffline={cfg.MulticajaBlockCriticalWhenOffline} enqueueCritical={cfg.MulticajaEnqueueCriticalWhenOffline}");
        sb.AppendLine($"blockMonetary={MulticajaRuntime.BlockMonetaryForInvalidConfig} startupApiUnreachable={MulticajaRuntime.StartupApiUnreachable}");
        sb.AppendLine($"queueDir={OfflineQueue.QueueDirectory} pending={pending} bytes={bytes}");
        sb.AppendLine($"queueMaxPending={cfg.MulticajaOfflineQueueMaxPendingItems} queueMaxBytes={cfg.MulticajaOfflineQueueMaxTotalBytes}");
        sb.AppendLine($"connectivity={App.Connectivity?.State} latencyMs={App.Connectivity?.LastLatencyMs}");
        sb.AppendLine($"connectionStringHasUNC={(cfg.ConnectionString ?? "").Contains(@"\\", StringComparison.Ordinal)}");
        sb.AppendLine("multicaja.diagnostics === fin ===");
        PosDiagnostics.Log(sb.ToString());
    }
}
