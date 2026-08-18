using Grunflex.Licensing;
using GrunflexPOS.API.Commerce;

namespace GrunflexPOS.API.Services;

/// <summary>Reglas de permisos alineadas con <c>CajerosView</c> del POS.</summary>
public static class MulticajaPermisosHelper
{
    public static bool EsAdmin(CommerceUsuario u) => UsuarioRolPermisos.EsAdmin(u.Rol);

    public static bool PuedeAnularTickets(CommerceUsuario u) =>
        UsuarioRolPermisos.PuedeCancelarTickets(u.Rol);

    public static bool PuedeDevolverStock(CommerceUsuario u) =>
        UsuarioRolPermisos.PuedeDevolverArticulos(u.Rol);

    public static bool PuedeAjustarInventario(CommerceUsuario u) =>
        UsuarioRolPermisos.PuedeAjustarInventario(u.Rol);

    public static bool PuedeAdministrarUsuarios(CommerceUsuario u) =>
        UsuarioRolPermisos.PuedeAdministrarUsuarios(u.Rol);
}
