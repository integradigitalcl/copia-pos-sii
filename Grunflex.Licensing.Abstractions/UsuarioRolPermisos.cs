namespace Grunflex.Licensing;

/// <summary>
/// Reglas compartidas de <c>Usuario.Rol</c> entre POS, API y LicenseIssuer.
/// Valores: <c>Admin</c>, <c>Cajero</c>, <c>SinPermisos</c>, <c>PERMISOS:TOKEN,...</c>.
/// </summary>
public static class UsuarioRolPermisos
{
    public static class Codigo
    {
        public const string Ventas = "VENTAS";
        public const string Clientes = "CLIENTES";
        public const string Productos = "PRODUCTOS";
        public const string Inventario = "INVENTARIO";
        public const string CancelarTickets = "CANCELAR_TICKETS";
        public const string Descuentos = "DESCUENTOS";
        public const string VerHistorial = "VER_HISTORIAL";
        public const string Cobrar = "COBRAR";
        public const string Facturar = "FACTURAR";
    }

    public static readonly string[] AdminPreset =
    {
        Codigo.Ventas, Codigo.Clientes, Codigo.Productos, Codigo.Inventario,
        Codigo.CancelarTickets, Codigo.Descuentos, Codigo.VerHistorial,
        Codigo.Cobrar, Codigo.Facturar
    };

    public static readonly string[] CajeroPreset =
    {
        Codigo.Ventas, Codigo.Productos, Codigo.Cobrar
    };

    public static IReadOnlySet<string> Parse(string? rol)
    {
        var permisos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var r = (rol ?? string.Empty).Trim();

        if (r.StartsWith("PERMISOS:", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in r.Substring("PERMISOS:".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(p))
                    permisos.Add(p.ToUpperInvariant());
            }

            return permisos;
        }

        if (string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in AdminPreset)
                permisos.Add(p);
            return permisos;
        }

        if (string.Equals(r, "Cajero", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var p in CajeroPreset)
                permisos.Add(p);
            return permisos;
        }

        return permisos;
    }

    public static bool EsAdmin(string? rol)
    {
        var r = (rol ?? string.Empty).Trim();
        if (string.Equals(r, "Admin", StringComparison.OrdinalIgnoreCase))
            return true;

        var permisos = Parse(r);
        return AdminPreset.All(permisos.Contains);
    }

    public static bool Tiene(string? rol, string permiso)
    {
        if (EsAdmin(rol))
            return true;

        return Parse(rol).Contains(permiso);
    }

    public static bool PuedeOperarPos(string? rol)
    {
        var r = (rol ?? string.Empty).Trim();
        if (string.Equals(r, "SinPermisos", StringComparison.OrdinalIgnoreCase))
            return false;

        return EsAdmin(rol) || Parse(rol).Count > 0;
    }

    public static bool PuedeAccederConfiguracion(string? rol) => EsAdmin(rol);

    public static bool PuedeAdministrarUsuarios(string? rol) => EsAdmin(rol);

    public static bool PuedeAdministrarLicencias(string? rol) => EsAdmin(rol);

    public static bool PuedeAdministrarBaseDatos(string? rol) => EsAdmin(rol);

    public static bool PuedeRestaurarRespaldos(string? rol) => EsAdmin(rol);

    public static bool PuedeAjustarInventario(string? rol) => Tiene(rol, Codigo.Inventario);

    public static bool PuedeVerReportes(string? rol) => Tiene(rol, Codigo.VerHistorial);

    public static bool PuedeGestionarProductos(string? rol) => Tiene(rol, Codigo.Productos);

    public static bool PuedeOperarVentas(string? rol) => Tiene(rol, Codigo.Ventas);

    public static bool PuedeCobrar(string? rol) => Tiene(rol, Codigo.Cobrar);

    public static bool PuedeAplicarDescuentos(string? rol) => Tiene(rol, Codigo.Descuentos);

    public static bool PuedeCancelarTickets(string? rol) => Tiene(rol, Codigo.CancelarTickets);

    public static bool PuedeDevolverArticulos(string? rol) =>
        PuedeCancelarTickets(rol) || PuedeAjustarInventario(rol);

    public static string ConstruirRol(IEnumerable<string> permisos)
    {
        var list = permisos
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (AdminPreset.All(list.Contains))
            return "Admin";

        if (CajeroPreset.All(list.Contains) && list.Count <= CajeroPreset.Length)
            return "Cajero";

        if (list.Count == 0)
            return "SinPermisos";

        return "PERMISOS:" + string.Join(",", list);
    }
}
