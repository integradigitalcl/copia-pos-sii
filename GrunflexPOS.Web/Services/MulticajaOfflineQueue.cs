using System.Text.Json;

namespace GrunflexPOS.Web.Services;

public sealed class MulticajaOfflineQueue(ILogger<MulticajaOfflineQueue> logger)
{
    public const string VentaCommit = "multicaja-venta-commit";
    public const string AnularVenta = "multicaja-anular-venta";
    public const string DevolucionLinea = "multicaja-devolucion-linea";
    public const string CierreSesion = "multicaja-cierre-sesion";
    public const string MovimientoCaja = "multicaja-movimiento-caja";
    public const string InventarioAjuste = "multicaja-inventario-ajustar";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static string QueueDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GrunflexPOS",
        "multicaja-queue");

    public bool TryEnqueue(string kind, object payload, string requestId)
    {
        requestId = (requestId ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(requestId))
            return false;

        Directory.CreateDirectory(QueueDirectory);
        var path = Path.Combine(QueueDirectory, $"{requestId}.json");
        if (File.Exists(path))
        {
            logger.LogDebug("Cola multicaja: duplicado omitido {RequestId}", requestId);
            return true;
        }

        var item = new MulticajaOfflineQueueItem
        {
            Id = requestId,
            Kind = kind,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAtUtc = DateTime.UtcNow
        };
        File.WriteAllText(path, JsonSerializer.Serialize(item, JsonOptions));
        logger.LogInformation("Cola multicaja: encolado {Kind} id={RequestId}", kind, requestId);
        return true;
    }

    public int PendingCount() =>
        Directory.Exists(QueueDirectory)
            ? Directory.EnumerateFiles(QueueDirectory, "*.json").Count(IsPendingFile)
            : 0;

    public IReadOnlyList<MulticajaOfflineQueueItem> ListPending()
    {
        if (!Directory.Exists(QueueDirectory))
            return [];

        return Directory.EnumerateFiles(QueueDirectory, "*.json")
            .Select(ReadItem)
            .Where(item => item is { Done: false, FailedPermanent: false })
            .OrderBy(item => item!.CreatedAtUtc)
            .Cast<MulticajaOfflineQueueItem>()
            .ToList();
    }

    public void MarkDone(string requestId)
    {
        var item = ReadItem(Path.Combine(QueueDirectory, $"{requestId}.json"));
        if (item is null)
            return;
        item.Done = true;
        item.CompletedAtUtc = DateTime.UtcNow;
        SaveItem(item);
    }

    public void MarkFailed(string requestId, string error, bool permanent = false)
    {
        var item = ReadItem(Path.Combine(QueueDirectory, $"{requestId}.json"));
        if (item is null)
            return;
        item.Attempts++;
        item.LastError = error;
        item.FailedPermanent = permanent;
        item.LastAttemptUtc = DateTime.UtcNow;
        SaveItem(item);
    }

    private static bool IsPendingFile(string path)
    {
        var item = ReadItem(path);
        return item is { Done: false, FailedPermanent: false };
    }

    private static MulticajaOfflineQueueItem? ReadItem(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<MulticajaOfflineQueueItem>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveItem(MulticajaOfflineQueueItem item) =>
        File.WriteAllText(Path.Combine(QueueDirectory, $"{item.Id}.json"),
            JsonSerializer.Serialize(item, JsonOptions));
}

public sealed class MulticajaOfflineQueueItem
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public bool Done { get; set; }
    public bool FailedPermanent { get; set; }
}

public sealed record MulticajaReplayResult(int Processed, int Succeeded, int Failed, int Remaining)
{
    public static MulticajaReplayResult Empty => new(0, 0, 0, 0);
}
