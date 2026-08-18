using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Grunflex.Idempotency;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Connectivity;

namespace GrunflexPOS2.Services.Offline;

/// <summary>
/// Cola persistente en disco para operaciones que requieren conectividad.
///
/// Garantías:
/// - <b>At-least-once</b> + idempotencia: cada item tiene un Id estable; el handler debe
///   tolerar reintentos del mismo Id sin duplicar.
/// - <b>Persistencia</b>: cada item se guarda como JSON en <c>queue\&lt;id&gt;.json</c>. Sobrevive
///   crashes del POS.
/// - <b>Checksum</b> del payload (SHA-256) para detectar corrupción o edición manual.
/// - <b>Replay exclusivo</b>: un solo replay concurrente (evita doble envío bajo timer + reconexión).
/// - <b>Backoff exponencial</b> entre intentos del replay (30s, 2m, 10m, 30m, 2h, 6h tope).
/// - <b>Replay automático</b> cuando la conectividad pasa a Online.
/// </summary>
public sealed class OfflineQueue : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };
    private static readonly object JournalLock = new();
    private readonly ConnectivityMonitor? _connectivity;
    private readonly ConcurrentDictionary<string, IOfflineHandler> _handlers = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _inflight = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _replayGate = new(1, 1);
    private Timer? _timer;
    private readonly TimeSpan[] _backoff = new[]
    {
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(6)
    };

    public static string QueueDirectory =>
        Path.Combine(LocalDatabasePaths.DataDirectory, "queue");

    private static string JournalPath => Path.Combine(QueueDirectory, "replay_journal.log");

    public OfflineQueue(ConnectivityMonitor? monitor)
    {
        _connectivity = monitor;
        Directory.CreateDirectory(QueueDirectory);
        if (_connectivity != null)
            _connectivity.StateChanged += OnConnectivityChanged;
    }

    public void RegisterHandler(string kind, IOfflineHandler handler)
        => _handlers[kind] = handler;

    public void Start()
    {
        _timer ??= new Timer(_ => SafeReplay(), null,
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(60));
    }

    public void Stop()
    {
        try { _timer?.Dispose(); } catch { }
        _timer = null;
    }

    public OfflineQueueItem Enqueue(string kind, object payload, string? stableId = null)
    {
        var item = new OfflineQueueItem
        {
            Id = string.IsNullOrWhiteSpace(stableId) ? Guid.NewGuid().ToString("N") : stableId.Trim(),
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOpts)
        };
        item.PayloadSha256 = IdempotencyPayloadHasher.HashJson(item.PayloadJson);
        item.ItemSchemaVersion = 2;
        SaveItem(item);
        Journal("enqueue", item.Id, item.Kind, $"bytes={Encoding.UTF8.GetByteCount(item.PayloadJson)}");
        PosDiagnostics.Log($"multicaja.queue enqueued kind={kind} id={item.Id[..8]} sha256={item.PayloadSha256![..12]}...");
        return item;
    }

    public int PendingCount() => ListItems().Count(i => !i.Done && !i.FailedPermanent);

    public int FailedPermanentCount() => ListItems().Count(i => i.FailedPermanent && !i.Done);

    /// <summary>Pendientes y bytes en disco de la cola (solo archivos <c>*.json</c> ítems).</summary>
    public static (int pending, long totalBytes) GetDiskMetrics()
    {
        var pending = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(QueueDirectory)) return (0, 0);
            foreach (var f in Directory.EnumerateFiles(QueueDirectory, "*.json"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    bytes += fi.Length;
                    var json = File.ReadAllText(f);
                    var item = JsonSerializer.Deserialize<OfflineQueueItem>(json);
                    if (item != null && !item.Done && !item.FailedPermanent)
                        pending++;
                }
                catch
                {
                    bytes += new FileInfo(f).Length;
                }
            }
        }
        catch { /* */ }

        return (pending, bytes);
    }

    public IReadOnlyList<OfflineQueueItem> ListItems()
    {
        var list = new List<OfflineQueueItem>();
        if (!Directory.Exists(QueueDirectory)) return list;
        foreach (var f in Directory.EnumerateFiles(QueueDirectory, "*.json"))
        {
            try
            {
                var json = File.ReadAllText(f);
                var item = JsonSerializer.Deserialize<OfflineQueueItem>(json);
                if (item != null) list.Add(item);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log($"multicaja.integrity corrupt_json path={Path.GetFileName(f)}", ex);
            }
        }

        return list.OrderBy(i => i.EnqueuedUtc).ToList();
    }

    private void OnConnectivityChanged(object? sender, ConnectivityState s)
    {
        if (s == ConnectivityState.Online) SafeReplay();
    }

    private void SafeReplay()
    {
        try { _ = ReplayAsync(CancellationToken.None); }
        catch (Exception ex) { PosDiagnostics.Log("OfflineQueue replay falló", ex); }
    }

    public async Task ReplayAsync(CancellationToken ct)
    {
        if (!await _replayGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            PosDiagnostics.Log("multicaja.integrity replay_skipped_concurrent");
            return;
        }

        try
        {
            if (_connectivity != null && _connectivity.State == ConnectivityState.Offline)
                return;

            var cfg = AppConfig.Cargar();
            var maxAttempts = cfg.MulticajaOfflineQueueMaxAttempts <= 0 ? 32 : cfg.MulticajaOfflineQueueMaxAttempts;
            var sameStreakLimit = cfg.MulticajaOfflineQueueSameErrorStreak <= 0 ? 8 : cfg.MulticajaOfflineQueueSameErrorStreak;

            var (diskPending, diskBytes) = GetDiskMetrics();
            PosDiagnostics.Log(
                $"multicaja.health queue pending={diskPending} bytes={diskBytes} connectivity={_connectivity?.State}");

            foreach (var item in ListItems())
            {
                if (ct.IsCancellationRequested) return;
                if (item.Done) { TryDelete(item); continue; }
                if (item.FailedPermanent)
                {
                    PosDiagnostics.Log(
                        $"multicaja.queue skip_failed_permanent kind={item.Kind} id={item.Id[..8]} err={item.LastError}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(item.Kind) || string.IsNullOrWhiteSpace(item.PayloadJson))
                {
                    item.FailedPermanent = true;
                    item.LastError = "CORRUPT_PAYLOAD: kind o payload vacío";
                    SaveItem(item);
                    Journal("corrupt_payload", item.Id, item.Kind ?? "", "");
                    PosDiagnostics.Log($"multicaja.integrity corrupt_payload id={item.Id[..8]}");
                    continue;
                }

                if (!TryVerifyOrMigrateChecksum(item, out var chkErr))
                {
                    item.FailedPermanent = true;
                    item.LastError = "CORRUPT_CHECKSUM:" + chkErr;
                    SaveItem(item);
                    Journal("checksum_fail", item.Id, item.Kind, chkErr);
                    PosDiagnostics.Log($"multicaja.integrity checksum_fail id={item.Id[..8]} err={chkErr}");
                    continue;
                }

                if (!_handlers.TryGetValue(item.Kind, out var handler)) continue;
                if (!_inflight.TryAdd(item.Id, 0)) continue;

                try
                {
                    var nextEligible = item.NextRetryAt ?? NextAttemptUtc(item);
                    if (DateTime.UtcNow < nextEligible) continue;

                    item.State = OfflineOperationState.Sending;
                    item.AttemptCount++;
                    item.LastAttemptUtc = DateTime.UtcNow;

                    PosDiagnostics.Log(
                        $"multicaja.replay kind={item.Kind} id={item.Id[..8]} attempt={item.AttemptCount} max={maxAttempts}");

                    var (ok, err) = await handler.TryHandleAsync(item, ct).ConfigureAwait(false);
                    if (ok)
                    {
                        item.Done = true;
                        item.State = OfflineOperationState.Completed;
                        item.LastError = null;
                        item.ConsecutiveSameErrorCount = 0;
                        TryDelete(item);
                        Journal("replay_ok", item.Id, item.Kind, $"attempts={item.AttemptCount}");
                        PosDiagnostics.Log($"multicaja.replay ok kind={item.Kind} id={item.Id[..8]} attempts={item.AttemptCount}");
                    }
                    else
                    {
                        var prevErr = item.LastError;
                        item.LastError = err;
                        if (string.Equals(prevErr, err, StringComparison.Ordinal))
                            item.ConsecutiveSameErrorCount++;
                        else
                            item.ConsecutiveSameErrorCount = 1;

                        var permanent = item.AttemptCount >= maxAttempts ||
                                        item.ConsecutiveSameErrorCount >= sameStreakLimit;
                        if (permanent)
                        {
                            item.FailedPermanent = true;
                            item.State = OfflineOperationState.Poisoned;
                            item.LastError =
                                $"FAILED_PERMANENT attempts={item.AttemptCount} streak={item.ConsecutiveSameErrorCount} last={err}";
                            Journal("replay_exhausted", item.Id, item.Kind, item.LastError);
                            PosDiagnostics.Log($"multicaja.retry exhausted kind={item.Kind} id={item.Id[..8]}");
                        }
                        else
                        {
                            item.State = OfflineOperationState.Failed;
                            item.NextRetryAt = NextAttemptUtc(item);
                            PosDiagnostics.Log($"multicaja.retry backoff kind={item.Kind} id={item.Id[..8]} err={err}");
                        }

                        SaveItem(item);
                    }
                }
                catch (Exception ex)
                {
                    item.LastError = ex.Message;
                    item.ConsecutiveSameErrorCount++;
                    if (item.AttemptCount >= maxAttempts)
                    {
                        item.FailedPermanent = true;
                        item.LastError = "FAILED_PERMANENT:" + ex.Message;
                    }

                    SaveItem(item);
                    Journal("replay_throw", item.Id, item.Kind, ex.Message);
                    PosDiagnostics.Log($"multicaja.replay handler_throw kind={item.Kind} id={item.Id[..8]}", ex);
                }
                finally
                {
                    _inflight.TryRemove(item.Id, out _);
                }
            }
        }
        finally
        {
            _replayGate.Release();
        }
    }

    private bool TryVerifyOrMigrateChecksum(OfflineQueueItem item, out string? error)
    {
        error = null;
        var hex = Sha256Hex(item.PayloadJson);
        if (string.IsNullOrWhiteSpace(item.PayloadSha256))
        {
            item.PayloadSha256 = hex;
            item.ItemSchemaVersion = Math.Max(2, item.ItemSchemaVersion);
            SaveItem(item);
            Journal("checksum_migrated", item.Id, item.Kind, "");
            PosDiagnostics.Log($"multicaja.integrity checksum_migrated id={item.Id[..8]}");
            return true;
        }

        if (!string.Equals(item.PayloadSha256, hex, StringComparison.OrdinalIgnoreCase))
        {
            error = "payload_sha256_mismatch";
            return false;
        }

        return true;
    }

    private DateTime NextAttemptUtc(OfflineQueueItem item)
    {
        if (item.AttemptCount == 0 || item.LastAttemptUtc == null) return DateTime.MinValue;
        var idx = Math.Min(item.AttemptCount - 1, _backoff.Length - 1);
        return item.LastAttemptUtc.Value + _backoff[idx];
    }

    private void SaveItem(OfflineQueueItem item)
    {
        Directory.CreateDirectory(QueueDirectory);
        var path = Path.Combine(QueueDirectory, item.Id + ".json");
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(item, JsonOpts));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private void TryDelete(OfflineQueueItem item)
    {
        try { File.Delete(Path.Combine(QueueDirectory, item.Id + ".json")); }
        catch { }
    }

    private static string Sha256Hex(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void Journal(string evt, string id, string kind, string? detail)
    {
        try
        {
            Directory.CreateDirectory(QueueDirectory);
            var line =
                $"{DateTime.UtcNow:O}\t{evt}\t{id}\t{kind}\t{detail?.Replace('\t', ' ') ?? ""}{Environment.NewLine}";
            lock (JournalLock)
            {
                File.AppendAllText(JournalPath, line);
            }
        }
        catch { /* journaling best-effort */ }
    }

    public void Dispose()
    {
        Stop();
        if (_connectivity != null) _connectivity.StateChanged -= OnConnectivityChanged;
        _replayGate.Dispose();
    }
}

public interface IOfflineHandler
{
    /// <returns><c>(ok=true, _)</c> si se procesó; <c>(false, err)</c> para reintentar.</returns>
    Task<(bool ok, string? error)> TryHandleAsync(OfflineQueueItem item, CancellationToken ct);
}
