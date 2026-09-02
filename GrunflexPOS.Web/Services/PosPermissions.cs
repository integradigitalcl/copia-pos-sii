namespace GrunflexPOS.Web.Services;

public static class PosPermissions
{
    public static bool IsAdmin(string permissions, string role) =>
        permissions.Equals("all", StringComparison.OrdinalIgnoreCase) ||
        role.Equals("Administrador", StringComparison.OrdinalIgnoreCase);

    public static bool Has(string permissions, string role, string code) =>
        IsAdmin(permissions, role) ||
        permissions.Contains(code, StringComparison.OrdinalIgnoreCase);

    public static bool CanAccessVentas(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "sales") || Has(permissions, role, "ventas");

    public static bool CanAccessProducts(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "products") || Has(permissions, role, "productos");

    public static bool CanAccessInventory(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "inventory") || Has(permissions, role, "inventario");

    public static bool CanAccessReports(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "reports") || Has(permissions, role, "historial");

    public static bool CanCancelSales(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "cancel");

    public static bool CanApplyDiscount(string permissions, string role) =>
        IsAdmin(permissions, role) || Has(permissions, role, "discount") || Has(permissions, role, "descuentos");

    public static bool CanManageConfiguration(string permissions, string role) => IsAdmin(permissions, role);

    public static bool CanManageUsers(string permissions, string role) => IsAdmin(permissions, role);
}
