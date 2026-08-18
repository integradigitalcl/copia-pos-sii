using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Services.Licensing;

/// <summary>Validación local de límite de cajas (NumberOfBoxes) alineada con LicenseSlotService del API.</summary>
public static class CajaSlotGate
{
    public static int ResolveMaxBoxes(ConfiguracionService cfg)
    {
        var raw = cfg.Get("licencia_number_of_boxes");
        if (int.TryParse(raw, out var n) && n > 0)
            return n;

        return App.LicenseState.Multicaja ? 5 : 1;
    }

    public static bool CanCreateActiveCaja(GrunflexDbContext db, ConfiguracionService cfg, out string? mensaje)
    {
        mensaje = null;
        var max = ResolveMaxBoxes(cfg);
        var activas = db.Cajas.Count(c => c.Activa);
        if (activas + 1 <= max)
            return true;

        mensaje = LicenseAccessGate.MensajeLimiteCajasConPlan(max);
        return false;
    }
}
