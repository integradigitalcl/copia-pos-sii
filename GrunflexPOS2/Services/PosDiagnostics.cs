using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace GrunflexPOS2.Services;

/// <summary>
/// Logger ligero del POS. Escribe a archivo con rotación diaria, retención y límite de tamaño.
/// No depende de Serilog para mantener el arranque rápido y robusto ante fallos de configuración.
///
/// Reglas:
///   - Ruta: %LocalAppData%\GrunflexPOS\logs\pos-YYYYMMDD.log
///   - Tamaño máximo por archivo: 10 MB. Al superarse rota a pos-YYYYMMDD-001.log, 002.log, ...
///   - Retención: borra archivos pos-*.log con más de 14 días (best-effort, una vez por día).
/// </summary>
internal static class PosDiagnostics
{
    private static readonly object Gate = new();
    private const long MaxFileBytes = 10L * 1024 * 1024;
    private const int RetentionDays = 14;
    private static DateTime _lastCleanup = DateTime.MinValue;

    public static string LogDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "logs");

    public static void Log(string message, Exception? ex = null)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{Thread.CurrentThread.ManagedThreadId,3}] {message}";
        Debug.WriteLine("[GrunflexPOS] " + line);
        if (ex != null) Debug.WriteLine(ex.ToString());

        try
        {
            Directory.CreateDirectory(LogDirectory);
            lock (Gate)
            {
                CleanupIfNeeded();
                var path = ResolveCurrentLogPath();
                var sb = new StringBuilder(line);
                sb.AppendLine();
                if (ex != null)
                {
                    sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                    sb.AppendLine(ex.StackTrace);
                    var inner = ex.InnerException;
                    while (inner != null)
                    {
                        sb.AppendLine("  -> " + inner.GetType().FullName + ": " + inner.Message);
                        inner = inner.InnerException;
                    }
                }
                File.AppendAllText(path, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging nunca debe romper al POS.
        }
    }

    private static string ResolveCurrentLogPath()
    {
        var today = DateTime.Now.ToString("yyyyMMdd");
        var basePath = Path.Combine(LogDirectory, $"pos-{today}.log");
        if (!File.Exists(basePath) || new FileInfo(basePath).Length < MaxFileBytes)
            return basePath;

        // Buscar siguiente sufijo libre
        for (int i = 1; i < 1000; i++)
        {
            var rolled = Path.Combine(LogDirectory, $"pos-{today}-{i:D3}.log");
            if (!File.Exists(rolled) || new FileInfo(rolled).Length < MaxFileBytes)
                return rolled;
        }
        return basePath; // fallback: seguir escribiendo
    }

    private static void CleanupIfNeeded()
    {
        if (DateTime.Now.Date <= _lastCleanup) return;
        _lastCleanup = DateTime.Now.Date;
        try
        {
            var cutoff = DateTime.Now - TimeSpan.FromDays(RetentionDays);
            foreach (var f in Directory.EnumerateFiles(LogDirectory, "pos-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
                catch { }
            }
            // También limpia logs antiguos del instalador / multicaja
            foreach (var f in Directory.EnumerateFiles(LogDirectory, "setup-multicaja*.log"))
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 5 * 1024 * 1024 || fi.LastWriteTime < cutoff)
                        File.Delete(f);
                }
                catch { }
            }
            foreach (var f in Directory.EnumerateFiles(LogDirectory, "diagnostico-*.txt"))
            {
                try
                {
                    if (File.GetLastWriteTime(f) < cutoff)
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
