namespace Grunflex.Licensing;

/// <summary>Contrato JSON compartido entre POS, API y LicenseIssuer (licencia firmada GFv2).</summary>
public sealed class GrunflexLicensePayload
{
    public string? Customer { get; set; }

    public string? Machine { get; set; }

    public DateTime ExpUtc { get; set; }

    public bool Multicaja { get; set; }

    public bool OnlineSupport { get; set; }

    public bool CloudBackup { get; set; }

    public bool PrioritySupport { get; set; }

    /// <summary>Identificador de activación comercial (opcional; usado para respaldos/soporte en nube).</summary>
    public string? ActivationId { get; set; }

    /// <summary>
    /// Días máximos sin sincronización exitosa con la API antes de bloquear el POS.
    /// 0 = usar el default del POS (<see cref="GrunflexLicenseDefaults.OfflineGraceDays"/> / appsettings).
    /// </summary>
    public int OfflineGraceDays { get; set; }

    /// <summary>Cajas/terminales permitidos en el plan (0 = no especificado en token).</summary>
    public int NumberOfBoxes { get; set; }
}
