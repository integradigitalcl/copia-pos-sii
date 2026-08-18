namespace Grunflex.LicenseIssuer.Persistence;

/// <summary>Configuración UI persistida en disco local (no secretos RSA).</summary>
public sealed class IssuerSettingsDto
{
    /// <summary>URL base de la API (ej. http://127.0.0.1:7279). Opcional.</summary>
    public string? ApiBaseUrl { get; set; }

    public string SmtpHost { get; set; } = string.Empty;

    public string SmtpPort { get; set; } = "587";

    public string SenderEmail { get; set; } = string.Empty;

    public bool ForcePasswordRotation { get; set; } = true;

    public bool LockAfterFailedAttempts { get; set; } = true;

    public bool EnableAuditLog { get; set; } = true;

    public string BackupFrequency { get; set; } = "Diario";

    public string BackupPath { get; set; } = @"C:\Respaldos\Grunflex";

    public string TimeZone { get; set; } = "UTC-04:00 (Santiago)";

    public string Language { get; set; } = "Español";

    public bool OnlineValidation { get; set; } = true;

    public bool SystemNotifications { get; set; } = true;

    public bool AutomaticBackups { get; set; } = true;

    public bool MaintenanceMode { get; set; }

    /// <summary>Último ActivationId usado en generación (opcional).</summary>
    public string? ActivationId { get; set; }
}
