namespace GrunflexPOS.API.DTOs;

public sealed class LicenseIssuerStatsResponse
{
    public int ActiveLicenses { get; set; }

    public int ExpiredLicenses { get; set; }

    public int ActivationsTotal { get; set; }

    public int ClientsTotal { get; set; }

    public int LicensesTotal { get; set; }

    /// <summary>Licencias no vencidas por tipo de plan (según LicenseType).</summary>
    public int PlanBasico { get; set; }

    public int PlanMedium { get; set; }

    public int PlanPlus { get; set; }

    /// <summary>Otros tipos (p. ej. Suscripción, Permanente sin coincidencia).</summary>
    public int PlanOtro { get; set; }

    /// <summary>Licencias creadas por mes, 6 valores del más antiguo al más reciente (UTC).</summary>
    public List<int> NewLicensesByMonthLast6 { get; set; } = new();

    /// <summary>Etiquetas cortas de mes en el mismo orden (ej. ene. 2026).</summary>
    public List<string> SparkMonthLabels { get; set; } = new();

    public int NewLicensesThisCalendarMonthUtc { get; set; }

    public int NewLicensesPreviousCalendarMonthUtc { get; set; }

    public int ExpiringWithin7Days { get; set; }

    /// <summary>Activaciones con dispositivo pendiente de configurar.</summary>
    public int PendingDeviceActivations { get; set; }
}
