using System;
using System.Windows;
using GrunflexPOS2.Services;
using GrunflexPOS2.Views;

namespace GrunflexPOS2.Licensing;

/// <summary>Enforcement estricto de licencia: bloquea POS sin licencia válida y módulos sin derecho.</summary>
public static class LicenseAccessGate
{
    public const string MensajeLimiteCajasBase =
        "Ha alcanzado el límite de cajas/terminales de su plan.\n\n" +
        "Para agregar otra caja debe ampliar su licencia Multicaja (más equipos en el plan). " +
        "Contacte a ventas@grunflex.cl o a su distribuidor GrünFlex.";

    public const string MensajeLimiteCajas = MensajeLimiteCajasBase;

    public static string MensajeLimiteCajasConPlan(int maxBoxes) =>
        $"{MensajeLimiteCajasBase}\n\nCajas permitidas en su plan: {maxBoxes}.";

    public const string MensajeMulticajaRequerida =
        "Esta función requiere licencia Multicaja activa.\n\n" +
        "Active o renueve su licencia con el módulo Multicaja para usar cajas en red.";

    public static string MensajeFueraGraciaOffline(int diasGracia) =>
        $"Lleva más de {diasGracia} día(s) sin sincronizar con el servidor de licencias.\n\n" +
        "Debe conectarse a internet y sincronizar para volver a operar el POS. " +
        "Contacte a ventas@grunflex.cl o a su distribuidor si necesita ayuda.";

    public static void Refresh()
    {
        App.LicenseState.RefreshFromStores();
    }

    public static bool IsPosAccessAllowed()
    {
        Refresh();
        return App.LicenseState.IsValid && !App.LicenseState.IsBeyondOfflineGrace;
    }

    /// <summary>Muestra activación/renovación/sincronización si no hay acceso al POS. Retorna true si quedó permitido.</summary>
    public static bool EnsureValidForPosAccess(Window? owner = null)
    {
        Refresh();
        if (IsPosAccessAllowed())
            return true;

        var modo = ResolveActivationMode();
        return ShowActivationDialog(owner, modo);
    }

    private static ActivacionLicenciaModo ResolveActivationMode()
    {
        if (App.LicenseState.IsBeyondOfflineGrace)
            return ActivacionLicenciaModo.FueraGraciaOffline;

        if (App.LicenseState.IsExpired)
            return ActivacionLicenciaModo.Expirada;

        return ActivacionLicenciaModo.PrimeraVez;
    }

    public static bool ShowActivationDialog(Window? owner, ActivacionLicenciaModo modo)
    {
        var dlg = new ActivacionLicenciaWindow(modo)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true)
            return false;

        Refresh();
        return IsPosAccessAllowed();
    }

    public static void EnsureMulticajaModule()
    {
        Refresh();
        if (!App.LicenseState.Multicaja)
            throw new InvalidOperationException(MensajeMulticajaRequerida);
    }

    public static bool TryEnsureMulticajaModule(out string error)
    {
        Refresh();
        if (App.LicenseState.Multicaja)
        {
            error = string.Empty;
            return true;
        }

        error = MensajeMulticajaRequerida;
        return false;
    }

    /// <summary>Cierra la sesión operativa si la licencia deja de ser válida o supera la gracia offline.</summary>
    public static void HandleLicenseRevokedDuringSession()
    {
        Refresh();
        if (IsPosAccessAllowed())
            return;

        var fueraGracia = App.LicenseState.IsValid && App.LicenseState.IsBeyondOfflineGrace;

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (System.Windows.Application.Current.MainWindow is CajaView caja)
                caja.PermitirCerrar();

            MessageBox.Show(
                fueraGracia
                    ? MensajeFueraGraciaOffline(App.LicenseState.OfflineGraceDays)
                    : App.LicenseState.IsExpired
                        ? "Su licencia ha expirado. Debe ingresar una licencia vigente para continuar operando."
                        : "La licencia dejó de ser válida. Debe ingresar una licencia vigente para continuar operando.",
                "Licencia — Grunflex POS",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            if (System.Windows.Application.Current.MainWindow != null &&
                System.Windows.Application.Current.MainWindow is not ActivacionLicenciaWindow)
            {
                try { System.Windows.Application.Current.MainWindow.Close(); } catch { }
            }

            if (!EnsureValidForPosAccess())
            {
                System.Windows.Application.Current.Shutdown();
                return;
            }

            var login = new LoginWindow();
            System.Windows.Application.Current.MainWindow = login;
            login.Show();
        });
    }
}

