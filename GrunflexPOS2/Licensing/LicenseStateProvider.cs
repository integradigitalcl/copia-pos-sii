using GrunflexPOS2.Data;

using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.UI;

using GrunflexPOS2.Services;



namespace GrunflexPOS2.Licensing;



/// <summary>Estado derivado de <see cref="ProductEntitlements"/> y claves en configuración local.</summary>

public sealed class LicenseStateProvider : ILicenseStateProvider

{

    private readonly ConfiguracionService _cfg = new();

    private string? _lastBlockReason;



    public bool IsLicensed =>

        ProductEntitlements.Multicaja

        || ProductEntitlements.OnlineSupport

        || ProductEntitlements.CloudBackup

        || ProductEntitlements.PrioritySupport;



    public bool IsValid { get; private set; }



    public bool IsExpired { get; private set; }



    public string? BlockReason => _lastBlockReason;



    public DateTime? ExpiresUtc

    {

        get

        {

            var raw = _cfg.Get("licencia_exp_utc");

            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)

                ? d

                : null;

        }

    }



    public bool Multicaja => IsValid && ProductEntitlements.Multicaja;



    public bool OnlineSupport => IsValid && ProductEntitlements.OnlineSupport;



    public bool CloudBackup => IsValid && ProductEntitlements.CloudBackup;



    public bool PrioritySupport => IsValid && ProductEntitlements.PrioritySupport;



    public string? ActivationId

    {

        get

        {

            var v = _cfg.Get("licencia_activation_id")?.Trim();

            return string.IsNullOrEmpty(v) ? null : v;

        }

    }



    public DateTime? LastValidatedUtc

    {

        get

        {

            var raw = _cfg.Get("licencia_validada_utc");

            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)

                ? d

                : null;

        }

    }



    public DateTime? LastCloudOkUtc

    {

        get

        {

            var raw = _cfg.Get("licencia_last_cloud_ok_utc");

            return DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)

                ? d

                : null;

        }

    }



    public int OfflineGraceDays
    {
        get
        {
            var raw = _cfg.Get("licencia_offline_grace_days");
            if (int.TryParse(raw, out var fromLicense) && fromLicense > 0)
                return fromLicense;

            return AppConfig.Cargar().LicensingOfflineGraceDays;
        }
    }



    public bool IsBeyondOfflineGrace

    {

        get

        {

            if (!IsValid)

                return false;



            var last = LastCloudOkUtc;

            if (last == null)

                return false;



            var days = OfflineGraceDays;

            if (days <= 0)

                return false;



            var u = last.Value.Kind == DateTimeKind.Unspecified

                ? DateTime.SpecifyKind(last.Value, DateTimeKind.Utc)

                : last.Value.ToUniversalTime();



            return DateTime.UtcNow - u > TimeSpan.FromDays(days);

        }

    }



    public void RefreshFromStores()

    {

        var status = LicenseService.EvaluateStored(_cfg, out var msg);

        _lastBlockReason = msg;

        IsValid = status == LicenseStatus.Valid;

        IsExpired = status == LicenseStatus.Expired;

        BrandThemeService.ApplyFromLicenseState();
    }

}

