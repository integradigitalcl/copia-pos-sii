using System.Windows;

namespace GrunflexPOS2.Security;

/// <summary>Validaciones funcionales con mensaje estándar para la UI del POS.</summary>
public static class UsuarioPermisosGate
{
    public const string Titulo = "Permiso denegado";

    public static bool EnsureConfiguracion() =>
        Ensure(UsuarioPermisos.PuedeAccederConfiguracion,
            "Solo un administrador puede acceder a Configuración.");

    public static bool EnsureUsuarios() =>
        Ensure(UsuarioPermisos.PuedeAdministrarUsuarios,
            "Solo un administrador puede administrar usuarios y cajeros.");

    public static bool EnsureLicencias() =>
        Ensure(UsuarioPermisos.PuedeAdministrarLicencias,
            "Solo un administrador puede configurar licencias.");

    public static bool EnsureBaseDatos() =>
        Ensure(UsuarioPermisos.PuedeAdministrarBaseDatos,
            "Solo un administrador puede acceder a herramientas de base de datos.");

    public static bool EnsureRestaurarRespaldos() =>
        Ensure(UsuarioPermisos.PuedeRestaurarRespaldos,
            "Solo un administrador puede restaurar respaldos.");

    public static bool EnsureInventario() =>
        Ensure(UsuarioPermisos.PuedeAjustarInventario,
            "No tiene permiso para inventario avanzado. Solicite acceso a un administrador.");

    public static bool EnsureReportes() =>
        Ensure(UsuarioPermisos.PuedeVerReportes,
            "No tiene permiso para ver reportes e historial de ventas.");

    public static bool EnsureProductos() =>
        Ensure(UsuarioPermisos.PuedeGestionarProductos,
            "No tiene permiso para administrar productos.");

    public static bool EnsureVentas() =>
        Ensure(UsuarioPermisos.PuedeOperarVentas,
            "No tiene permiso para operar ventas.");

    public static bool EnsureCobrar() =>
        Ensure(UsuarioPermisos.PuedeCobrar,
            "No tiene permiso para cobrar ventas.");

    public static bool EnsureDescuentos() =>
        Ensure(UsuarioPermisos.PuedeAplicarDescuentos,
            "No tiene permiso para aplicar descuentos.");

    public static bool EnsureCancelarTickets() =>
        Ensure(UsuarioPermisos.PuedeCancelarTickets,
            "No tiene permiso para cancelar tickets.");

    public static bool EnsureDevolverArticulos() =>
        Ensure(UsuarioPermisos.PuedeDevolverArticulos,
            "No tiene permiso para devolver artículos.");

    private static bool Ensure(Func<bool> regla, string mensaje)
    {
        if (regla())
            return true;

        MessageBox.Show(mensaje, Titulo, MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
