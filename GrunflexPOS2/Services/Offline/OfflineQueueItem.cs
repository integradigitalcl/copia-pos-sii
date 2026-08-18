using System;
using System.Text.Json.Serialization;

namespace GrunflexPOS2.Services.Offline;

/// <summary>Estado de operación en cola offline enterprise.</summary>
public enum OfflineOperationState
{
    Pending = 0,
    Sending = 1,
    Completed = 2,
    Failed = 3,
    Poisoned = 4
}

/// <summary>
/// Una entrada de la cola offline. Diseño minimalista, agnóstico del tipo de
/// operación, para que cualquier servicio pueda enqueue/replay sin acoplamiento.
/// </summary>
public sealed class OfflineQueueItem
{
    /// <summary>Id único e idempotente. Si el server lo recibe dos veces, debe deduplicar por este id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Tipo de operación (ej. "sale.create", "audit.log", "stock.adjust"). Lo interpreta el handler.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Payload serializado en JSON. Lo interpreta el handler de cada Kind.</summary>
    public string PayloadJson { get; set; } = string.Empty;

    public DateTime EnqueuedUtc { get; set; } = DateTime.UtcNow;
    public int AttemptCount { get; set; }
    public DateTime? LastAttemptUtc { get; set; }
    public string? LastError { get; set; }

    /// <summary>True si el item ya fue procesado con éxito. Items completados se purgan periódicamente.</summary>
    public bool Done { get; set; }

    /// <summary>True si el ítem no debe reintentarse (operador o límite de reintentos).</summary>
    public bool FailedPermanent { get; set; }

    /// <summary>Veces seguidas con el mismo texto de error (detección de bucle).</summary>
    public int ConsecutiveSameErrorCount { get; set; }

    /// <summary>SHA-256 hex (minúsculas) del <see cref="PayloadJson"/> UTF-8; integridad tras crash/edición manual.</summary>
    public string? PayloadSha256 { get; set; }

    /// <summary>Versión del esquema del ítem en disco (≥2 incluye checksum).</summary>
    public int ItemSchemaVersion { get; set; }

    public OfflineOperationState State { get; set; } = OfflineOperationState.Pending;

    public DateTime? NextRetryAt { get; set; }

    public string? RequestId { get; set; }

    [JsonIgnore]
    public TimeSpan Age => DateTime.UtcNow - EnqueuedUtc;
}
