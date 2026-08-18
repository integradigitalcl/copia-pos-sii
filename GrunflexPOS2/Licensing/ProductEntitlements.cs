namespace GrunflexPOS2.Licensing;

/// <summary>
/// Funciones comerciales (multicaja, soporte en línea). Hoy se leen de configuración;
/// más adelante se pueden activar desde servidor de licencias sin cambiar la UI.
/// </summary>
public static class ProductEntitlements
{
    public static bool Multicaja { get; private set; }

    public static bool OnlineSupport { get; private set; }

    public static bool CloudBackup { get; private set; }

    public static bool PrioritySupport { get; private set; }

    public static void Apply(
        bool multicaja,
        bool onlineSupport,
        bool cloudBackup = false,
        bool prioritySupport = false)
    {
        Multicaja = multicaja;
        OnlineSupport = onlineSupport;
        CloudBackup = cloudBackup;
        PrioritySupport = prioritySupport;
    }
}
