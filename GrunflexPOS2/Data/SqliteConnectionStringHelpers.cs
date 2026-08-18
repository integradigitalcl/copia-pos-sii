using System;

namespace GrunflexPOS2.Data
{
    /// <summary>
    /// Parseo mínimo de cadenas SQLite (Data Source / Filename) compartido entre AppConfig y SMB bootstrap.
    /// </summary>
    internal static class SqliteConnectionStringHelpers
    {
        public static string ExtractDataSource(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs))
                return string.Empty;

            var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var p in parts)
            {
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = p[..eq].Trim();
                if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
                    continue;
                return p[(eq + 1)..].Trim().Trim('"');
            }

            return string.Empty;
        }

        public static string ReplaceDataSource(string cs, string newDataSource)
        {
            if (string.IsNullOrWhiteSpace(cs))
                return cs;

            var parts = cs.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < parts.Length; i++)
            {
                var p = parts[i];
                var eq = p.IndexOf('=');
                if (eq <= 0)
                    continue;
                var key = p[..eq].Trim();
                if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase) &&
                    !key.Equals("Filename", StringComparison.OrdinalIgnoreCase))
                    continue;
                parts[i] = $"{key}={newDataSource}";
                return string.Join(';', parts);
            }

            return cs;
        }

        /// <summary>
        /// Si la ruta UNC quedó con barras duplicadas (\\\\192…\\\\GrunflexPOS), colapsa pares
        /// consecutivos hasta forma válida \\host\share\archivo. Evita "carpeta no encontrada"
        /// aunque SMB esté bien.
        /// </summary>
        public static string NormalizeDoubledUncSlashesInSqliteConnectionString(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs))
                return cs;

            var ds = ExtractDataSource(cs);
            if (string.IsNullOrWhiteSpace(ds) || !ds.StartsWith(@"\\", StringComparison.Ordinal))
                return cs;

            var normalized = ds;
            string prev;
            do
            {
                prev = normalized;
                normalized = normalized.Replace(@"\\\\", @"\\", StringComparison.Ordinal);
            } while (!string.Equals(prev, normalized, StringComparison.Ordinal));

            return string.Equals(ds, normalized, StringComparison.Ordinal) ? cs : ReplaceDataSource(cs, normalized);
        }
    }
}
