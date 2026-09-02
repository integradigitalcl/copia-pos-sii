namespace GrunflexPOS.Web.Services.Licensing;

/// <summary>Validación local del límite de cajas (NumberOfBoxes) alineada con el API.</summary>
public static class CajaSlotGate
{
    public static int ResolveMaxBoxes(int numberOfBoxes, bool multicaja) =>
        numberOfBoxes > 0
            ? numberOfBoxes
            : multicaja ? 5 : 1;

    public static int ResolveMaxBoxes(WebLicenseState license) =>
        ResolveMaxBoxes(license.NumberOfBoxes, license.Multicaja);

    public static string LimiteCajasMensaje(WebLicenseState license) =>
        $"Su plan permite hasta {ResolveMaxBoxes(license)} caja(s) activa(s). " +
        "El servidor rechazará registros adicionales en auto-registro multicaja.";

    public static bool CanEnableMulticaja(bool multicajaLicensed, bool requireLicense) =>
        !requireLicense || multicajaLicensed;

    public static bool CanEnableMulticaja(WebLicenseState license, bool requireLicense) =>
        CanEnableMulticaja(license.Multicaja, requireLicense);
}
