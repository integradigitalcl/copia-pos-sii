using System;
using System.Collections.Generic;
using System.Linq;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Detección runtime de configuraciones peligrosas o degradación (logs <c>multicaja.risk</c>, <c>multicaja.guard</c>).
/// </summary>
public static class MulticajaRiskScanner
{
    private static bool ApiBaseLooksLoopback(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return true;
        var u = url.Trim().ToLowerInvariant();
        return u.Contains("//localhost", StringComparison.Ordinal) ||
               u.Contains("//127.0.0.1", StringComparison.Ordinal) ||
               u.Contains("//[::1]", StringComparison.Ordinal) ||
               u.Contains("//::1", StringComparison.Ordinal);
    }

    public static void Scan(string trigger)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return;

        var cfg = AppConfig.Cargar();
        var codes = new List<string>();

        var cs = cfg.ConnectionString ?? string.Empty;
        if (cs.Contains(@"\\", StringComparison.Ordinal))
            codes.Add("HYBRID_UNC_IN_CONNECTIONSTRING");

        if (ApiBaseLooksLoopback(cfg.ApiBaseUrl))
            codes.Add("API_BASE_LOOPBACK_ON_SECONDARY");

        try
        {
            var (pending, bytes) = OfflineQueue.GetDiskMetrics();
            MulticajaRuntime.LastOfflineQueuePending = pending;
            MulticajaRuntime.LastOfflineQueueBytes = bytes;

            var maxP = cfg.MulticajaOfflineQueueMaxPendingItems;
            if (maxP > 0 && pending >= maxP)
                codes.Add("QUEUE_PENDING_AT_OR_OVER_CAP");

            var maxB = cfg.MulticajaOfflineQueueMaxTotalBytes;
            if (maxB > 0 && bytes >= maxB)
                codes.Add("QUEUE_BYTES_AT_OR_OVER_CAP");
        }
        catch (Exception ex)
        {
            codes.Add("QUEUE_METRICS_ERROR");
            PosDiagnostics.Log("multicaja.risk queue_metrics", ex);
        }

        var y = DateTime.UtcNow.Year;
        if (y < 2020 || y > 2100)
            codes.Add("CLOCK_SUSPECT_UTC");

        try
        {
            var st = App.Connectivity?.State;
            if (st == ConnectivityState.Offline)
                codes.Add("CONNECTIVITY_OFFLINE");
            else if (st == ConnectivityState.Degraded)
                codes.Add("CONNECTIVITY_DEGRADED");
        }
        catch { /* */ }

        if (codes.Any(c => c.StartsWith("HYBRID_", StringComparison.Ordinal)))
        {
            MulticajaRuntime.BlockMonetaryForInvalidConfig = true;
            PosDiagnostics.Log(
                $"multicaja.guard CRITICAL hybrid_connection_string trigger={trigger}; bloqueo monetario activado.");
        }

        MulticajaRuntime.LastRiskScanUtc = DateTime.UtcNow;
        PosDiagnostics.Log(
            codes.Count == 0
                ? $"multicaja.risk trigger={trigger} ok"
                : $"multicaja.risk trigger={trigger} flags={string.Join(",", codes)}");
    }
}
