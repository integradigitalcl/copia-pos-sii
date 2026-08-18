namespace GrunflexPOS2.Domain.Abstractions;

/// <summary>Fuente única de lectura de derechos de producto (multicaja, nube, soporte).</summary>
public interface ILicenseStateProvider
{
    bool IsLicensed { get; }

    /// <summary>Licencia almacenada validada criptográficamente y no vencida.</summary>
    bool IsValid { get; }

    /// <summary>Hay token guardado pero está vencido o marcado como expirado.</summary>
    bool IsExpired { get; }

    /// <summary>Motivo del último fallo de validación local.</summary>
    string? BlockReason { get; }

    DateTime? ExpiresUtc { get; }

    bool Multicaja { get; }

    bool OnlineSupport { get; }

    bool CloudBackup { get; }

    bool PrioritySupport { get; }

    string? ActivationId { get; }

    DateTime? LastValidatedUtc { get; }

    /// <summary>Última vez que el servidor confirmó la licencia (refresh HTTP OK).</summary>
    DateTime? LastCloudOkUtc { get; }

    /// <summary>Días sin contacto con el servidor antes de bloquear el POS (desde token de licencia o appsettings).</summary>
    int OfflineGraceDays { get; }

    /// <summary>True si pasó más de <see cref="OfflineGraceDays"/> sin sincronización exitosa con la API.</summary>
    bool IsBeyondOfflineGrace { get; }

    void RefreshFromStores();
}
