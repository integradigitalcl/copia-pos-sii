using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.Telemetry;

/// <summary>
/// Telemetría liviana del POS (Fase 6).
///
/// Diseño:
/// - <b>Sin red obligatoria</b>: por default escribe eventos como JSONL en
///   <c>%LocalAppData%\GrunflexPOS\logs\pos-events-YYYYMMDD.jsonl</c>. Sirve para análisis
///   posterior (soporte, métricas de uso, debugging de incidentes).
/// - <b>Contadores en memoria</b>: cada Track incrementa un contador por nombre y los
///   últimos N eventos quedan en buffer en memoria, accesibles para la UI (diagnóstico).
/// - <b>Envío opcional a API</b>: futura ampliación — un BackgroundService podría enviar
///   en lote los JSONL a un endpoint <c>POST /api/telemetry</c> de la API local (o cloud).
///
/// Convención de nombres de evento (snake.dot):
///   app.started, app.exited, sale.completed, sale.failed, printer.error,
///   api.error, multicaja.reconnect, license.expired, license.warning,
///   backup.created, backup.failed, update.available, update.applied,
///   diagnostic.run, diagnostic.autofix
///
/// Props: cualquier dict serializable. Evite incluir datos sensibles (passwords, tokens).
/// </summary>
public static class Telemetry
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<string, long> Counters = new();
    private static readonly ConcurrentQueue<TelemetryEvent> Recent = new();
    private const int MaxRecent = 200;

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private static string LogsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "GrunflexPOS", "logs");

    /// <summary>Registra un evento con propiedades arbitrarias.</summary>
    public static void Track(string name, IReadOnlyDictionary<string, object?>? props = null)
    {
        try
        {
            var evt = new TelemetryEvent
            {
                Timestamp = DateTime.UtcNow,
                Name = name,
                Properties = props ?? EmptyProps
            };

            Counters.AddOrUpdate(name, 1, (_, v) => v + 1);
            Recent.Enqueue(evt);
            while (Recent.Count > MaxRecent && Recent.TryDequeue(out _)) { }

            WriteJsonLine(evt);
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("Telemetry.Track falló", ex);
        }
    }

    /// <summary>Helper para track sin props.</summary>
    public static void Track(string name) => Track(name, null);

    /// <summary>Helper para track con un solo par.</summary>
    public static void Track(string name, string key, object? value)
        => Track(name, new Dictionary<string, object?> { [key] = value });

    private static void WriteJsonLine(TelemetryEvent evt)
    {
        try
        {
            Directory.CreateDirectory(LogsDir);
            var path = Path.Combine(LogsDir, $"pos-events-{DateTime.Now:yyyyMMdd}.jsonl");
            lock (Gate)
            {
                using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                using var sw = new StreamWriter(fs);
                sw.WriteLine(JsonSerializer.Serialize(evt, JsonOpts));
            }
        }
        catch { /* logger es best-effort */ }
    }

    public static IReadOnlyDictionary<string, long> Snapshot()
    {
        var copy = new Dictionary<string, long>(Counters.Count);
        foreach (var kv in Counters) copy[kv.Key] = kv.Value;
        return copy;
    }

    public static IReadOnlyList<TelemetryEvent> RecentEvents()
        => Recent.ToArray();

    private static readonly IReadOnlyDictionary<string, object?> EmptyProps =
        new Dictionary<string, object?>();
}

public sealed class TelemetryEvent
{
    public DateTime Timestamp { get; set; }
    public string Name { get; set; } = string.Empty;
    public IReadOnlyDictionary<string, object?> Properties { get; set; } = new Dictionary<string, object?>();
}
