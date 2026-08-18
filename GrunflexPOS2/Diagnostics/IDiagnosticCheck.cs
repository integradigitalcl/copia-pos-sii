using System;
using System.Threading;
using System.Threading.Tasks;

namespace GrunflexPOS2.Diagnostics;

/// <summary>Severidad / estado consolidado de un chequeo.</summary>
public enum DiagnosticStatus
{
    Pending,
    Running,
    Ok,
    Warning,
    Error,
    Skipped
}

/// <summary>Resultado de un chequeo.</summary>
public sealed class DiagnosticResult
{
    public DiagnosticStatus Status { get; init; } = DiagnosticStatus.Pending;
    /// <summary>Mensaje corto orientado al usuario final (1 línea).</summary>
    public string Summary { get; init; } = string.Empty;
    /// <summary>Detalle técnico expandible (puede ser multilinea).</summary>
    public string TechnicalDetails { get; init; } = string.Empty;
    /// <summary>Si el chequeo encontró un problema y existe auto-reparación disponible.</summary>
    public bool CanAutoFix { get; init; }
    /// <summary>Acción de auto-reparación. Devuelve mensaje al usuario.</summary>
    public Func<CancellationToken, Task<string>>? AutoFix { get; init; }
}

/// <summary>Un chequeo individual del módulo de diagnóstico.</summary>
public interface IDiagnosticCheck
{
    /// <summary>Identificador estable (para logs y persistencia).</summary>
    string Id { get; }
    /// <summary>Nombre mostrado al usuario (es-CL, corto).</summary>
    string DisplayName { get; }
    /// <summary>Categoría visual: "Sistema", "Red", "Multicaja", "Licencia", etc.</summary>
    string Category { get; }
    Task<DiagnosticResult> RunAsync(CancellationToken ct);
}
