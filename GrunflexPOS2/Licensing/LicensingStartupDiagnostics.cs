using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Licensing;

/// <summary>Diagnóstico preventivo de licencias al arranque (claves RSA, vencimiento).</summary>
public static class LicensingStartupDiagnostics
{
    public static IReadOnlyList<string> Run(ConfiguracionService cfg)
    {
        var issues = new List<string>();
        App.LicenseState.RefreshFromStores();

        if (!LicenseService.HasLocalPublicKeyMaterial(cfg))
            issues.Add(
                "No hay clave pública RSA para validar licencias GFv2. " +
                "Use activación online, sincronice con el servidor o reinstale con build que incluya licensing-public.pem.");

        var exp = App.LicenseState.ExpiresUtc;
        if (exp.HasValue)
        {
            var days = (exp.Value - DateTime.UtcNow).TotalDays;
            if (days <= 0)
                issues.Add("La licencia está vencida. Debe renovar antes de operar.");
            else if (days <= 7)
                issues.Add($"La licencia vence en {Math.Ceiling(days)} día(s). Renueve pronto.");
        }

        if (App.LicenseState.IsBeyondOfflineGrace)
            issues.Add(LicenseAccessGate.MensajeFueraGraciaOffline(App.LicenseState.OfflineGraceDays));

        return issues;
    }
}
