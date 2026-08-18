using Grunflex.Licensing;
using GrunflexPOS2.Models.Entities;

namespace GrunflexPOS2.Security;

/// <summary>Acceso a permisos del usuario en sesión (<see cref="App.UsuarioActual"/>).</summary>
public static class UsuarioPermisos
{
    public static string? RolActual => App.UsuarioActual?.Rol;

    public static bool EsAdmin() => UsuarioRolPermisos.EsAdmin(RolActual);

    public static bool Tiene(string permiso) => UsuarioRolPermisos.Tiene(RolActual, permiso);

    public static bool PuedeOperarPos() => UsuarioRolPermisos.PuedeOperarPos(RolActual);

    public static bool PuedeAccederConfiguracion() => UsuarioRolPermisos.PuedeAccederConfiguracion(RolActual);

    public static bool PuedeAdministrarUsuarios() => UsuarioRolPermisos.PuedeAdministrarUsuarios(RolActual);

    public static bool PuedeAdministrarLicencias() => UsuarioRolPermisos.PuedeAdministrarLicencias(RolActual);

    public static bool PuedeAdministrarBaseDatos() => UsuarioRolPermisos.PuedeAdministrarBaseDatos(RolActual);

    public static bool PuedeRestaurarRespaldos() => UsuarioRolPermisos.PuedeRestaurarRespaldos(RolActual);

    public static bool PuedeAjustarInventario() => UsuarioRolPermisos.PuedeAjustarInventario(RolActual);

    public static bool PuedeVerReportes() => UsuarioRolPermisos.PuedeVerReportes(RolActual);

    public static bool PuedeGestionarProductos() => UsuarioRolPermisos.PuedeGestionarProductos(RolActual);

    public static bool PuedeOperarVentas() => UsuarioRolPermisos.PuedeOperarVentas(RolActual);

    public static bool PuedeCobrar() => UsuarioRolPermisos.PuedeCobrar(RolActual);

    public static bool PuedeAplicarDescuentos() => UsuarioRolPermisos.PuedeAplicarDescuentos(RolActual);

    public static bool PuedeCancelarTickets() => UsuarioRolPermisos.PuedeCancelarTickets(RolActual);

    public static bool PuedeDevolverArticulos() => UsuarioRolPermisos.PuedeDevolverArticulos(RolActual);

    public static bool Puede(Usuario? usuario, Func<string?, bool> regla) => regla(usuario?.Rol);
}
