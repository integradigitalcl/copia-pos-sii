using System.IO;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Offline;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>Encola operaciones multicaja críticas cuando la API no responde (misma idempotencia que el servidor).</summary>
public static class MulticajaOfflineEnqueue
{
    public static bool TryEnqueue(string kind, object payload, string requestId)
    {
        var cfg = AppConfig.Cargar();
        if (!cfg.MulticajaEnqueueCriticalWhenOffline || App.OfflineQueue == null)
            return false;

        var id = (requestId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(id))
            return false;

        var maxP = cfg.MulticajaOfflineQueueMaxPendingItems;
        var maxB = cfg.MulticajaOfflineQueueMaxTotalBytes;
        if (maxP > 0 || maxB > 0)
        {
            var (pending, bytes) = OfflineQueue.GetDiskMetrics();
            if (maxP > 0 && pending >= maxP)
            {
                PosDiagnostics.Log($"multicaja.risk enqueue_rejected queue_pending_cap pending={pending} max={maxP}");
                return false;
            }

            if (maxB > 0 && bytes >= maxB)
            {
                PosDiagnostics.Log($"multicaja.risk enqueue_rejected queue_bytes_cap bytes={bytes} max={maxB}");
                return false;
            }
        }

        Directory.CreateDirectory(OfflineQueue.QueueDirectory);
        var path = Path.Combine(OfflineQueue.QueueDirectory, id + ".json");
        if (File.Exists(path))
        {
            PosDiagnostics.Log($"multicaja.queue duplicate skip id={id[..Math.Min(8, id.Length)]} kind={kind}");
            return true;
        }

        App.OfflineQueue.Enqueue(kind, payload, id);
        PosDiagnostics.Log($"multicaja.queue enqueued kind={kind} id={id[..Math.Min(8, id.Length)]}");
        return true;
    }
}
