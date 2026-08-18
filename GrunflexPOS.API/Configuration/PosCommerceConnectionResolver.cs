using System.IO;
using Microsoft.Extensions.Configuration;

namespace GrunflexPOS.API.Configuration;

/// <summary>
/// Resuelve la cadena SQLite del POS operacional (<c>grunflex.db</c>), distinta de la BD
/// auxiliar de la API (<c>grunflex_api.db</c>) donde viven JWT y tablas de licenciamiento.
/// </summary>
public sealed class PosCommerceConnectionResolver
{
    private readonly IConfiguration _cfg;

    public PosCommerceConnectionResolver(IConfiguration cfg)
    {
        _cfg = cfg;
    }

    /// <summary>
    /// Prioridad: <c>Multicaja:PosConnectionString</c> → <c>ConnectionStrings:Pos</c> →
    /// ruta estándar <c>%ProgramData%\GrunflexPOS\data\grunflex.db</c> (misma que caja principal).
    /// </summary>
    public string GetConnectionString()
    {
        var direct =
            (_cfg["Multicaja:PosConnectionString"] ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(direct))
            return EnsureCacheShared(direct);

        var alt = (_cfg["ConnectionStrings:Pos"] ?? string.Empty).Trim();
        if (!string.IsNullOrEmpty(alt))
            return EnsureCacheShared(alt);

        var pd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GrunflexPOS",
            "data",
            "grunflex.db");
        return $"Data Source={pd};Cache=Shared";
    }

    private static string EnsureCacheShared(string cs)
    {
        if (cs.Contains("Cache=", StringComparison.OrdinalIgnoreCase))
            return cs;
        var sep = cs.EndsWith(';') ? "" : ";";
        return cs + sep + "Cache=Shared";
    }
}
