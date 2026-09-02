namespace GrunflexPOS.Web.Services.Licensing;

public sealed class WebLicenseState(
    WebLicenseService licenseService,
    LocalPosStore store,
    IConfiguration configuration)
{
    public event Action? Changed;

    public bool RequireLicense =>
        configuration.GetValue("Licensing:RequireLicense", false);

    public bool IsValid { get; private set; }
    public bool IsExpired { get; private set; }
    public string? BlockReason { get; private set; }
    public bool Multicaja { get; private set; }
    public bool OnlineSupport { get; private set; }
    public bool CloudBackup { get; private set; }
    public bool PrioritySupport { get; private set; }
    public string StatusText { get; private set; } = "Sin licencia";
    public string ExpiryText { get; private set; } = string.Empty;
    public string? ActivationId { get; private set; }
    public DateTime? ExpiresUtc { get; private set; }
    public DateTime? LastCloudOkUtc { get; private set; }
    public int OfflineGraceDays { get; private set; } = Grunflex.Licensing.GrunflexLicenseDefaults.OfflineGraceDays;
    public int NumberOfBoxes { get; private set; }

    public bool IsBeyondOfflineGrace =>
        IsValid && LastCloudOkUtc is not null && OfflineGraceDays > 0 &&
        DateTime.UtcNow - ToUtc(LastCloudOkUtc.Value) > TimeSpan.FromDays(OfflineGraceDays);

    public bool IsPosAccessAllowed => !RequireLicense || (IsValid && !IsBeyondOfflineGrace);

    public bool ActivationRequired => RequireLicense && !IsPosAccessAllowed;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var status = await licenseService.EvaluateStoredAsync(cancellationToken);
        IsValid = status == LicenseStatus.Valid;
        IsExpired = status == LicenseStatus.Expired;

        StatusText = await store.GetSettingAsync("licencia_estado", "Sin licencia", cancellationToken);
        ExpiryText = await store.GetSettingAsync("licencia_vencimiento", string.Empty, cancellationToken);
        ActivationId = NullIfEmpty(await store.GetSettingAsync("licencia_activation_id", cancellationToken: cancellationToken));
        ExpiresUtc = ParseUtc(await store.GetSettingAsync("licencia_exp_utc", cancellationToken: cancellationToken));
        LastCloudOkUtc = ParseUtc(await store.GetSettingAsync("licencia_last_cloud_ok_utc", cancellationToken: cancellationToken));

        var graceRaw = await store.GetSettingAsync("licencia_offline_grace_days", cancellationToken: cancellationToken);
        OfflineGraceDays = int.TryParse(graceRaw, out var grace) && grace > 0
            ? grace
            : licenseService.ResolveOfflineGraceDays(0);

        var boxesRaw = await store.GetSettingAsync("licencia_number_of_boxes", cancellationToken: cancellationToken);
        NumberOfBoxes = int.TryParse(boxesRaw, out var boxes) ? boxes : 0;

        Multicaja = IsValid && await GetBoolAsync("licencia_multicaja", cancellationToken);
        OnlineSupport = IsValid && await GetBoolAsync("licencia_online_support", cancellationToken);
        CloudBackup = IsValid && await GetBoolAsync("licencia_cloud_backup", cancellationToken);
        PrioritySupport = IsValid && await GetBoolAsync("licencia_priority_support", cancellationToken);

        BlockReason = ResolveBlockReason(status);
        Changed?.Invoke();
    }

    private string? ResolveBlockReason(LicenseStatus status) => status switch
    {
        LicenseStatus.Valid when IsBeyondOfflineGrace =>
            $"Lleva más de {OfflineGraceDays} día(s) sin sincronizar con el servidor de licencias.",
        LicenseStatus.Valid => null,
        LicenseStatus.Missing => "No hay licencia cargada.",
        LicenseStatus.Expired => "La licencia está vencida.",
        _ => "La licencia no es válida."
    };

    private async Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken) =>
        string.Equals(
            await store.GetSettingAsync(key, cancellationToken: cancellationToken),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private static DateTime? ParseUtc(string? raw) =>
        DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var value)
            ? ToUtc(value)
            : null;

    private static DateTime ToUtc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(value, DateTimeKind.Utc)
            : value.ToUniversalTime();

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
