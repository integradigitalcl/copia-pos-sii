using System.IO;
using System.Text;

namespace Grunflex.LicenseIssuer.Persistence;

public static class IssuerAuditLog
{
    public static string GetLogPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Grunflex",
            "LicenseIssuer",
            "audit.log");

    public static void Append(string message)
    {
        try
        {
            var dir = Path.GetDirectoryName(GetLogPath());
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            File.AppendAllText(GetLogPath(), line, Encoding.UTF8);
        }
        catch
        {
            // no bloquear UI
        }
    }

    public static IReadOnlyList<string> ReadTail(int maxLines)
    {
        try
        {
            var path = GetLogPath();
            if (!File.Exists(path))
                return Array.Empty<string>();

            var lines = File.ReadAllLines(path);
            if (lines.Length <= maxLines)
                return lines;

            return lines[^maxLines..];
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
