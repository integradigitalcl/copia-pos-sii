using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using Velopack;
using Velopack.Sources;

namespace GrunflexPOS2.Services.Updates;

public enum UpdateState
{
    Idle,
    Checking,
    UpdateAvailable,
    Downloading,
    ReadyToApply,
    UpToDate,
    Disabled,
    Error
}

/// <summary>
/// Servicio de actualizaciones in-app usando Velopack.
///
/// Modelo de despliegue:
/// - El instalador comercial inicial sigue siendo Inno Setup (estable, ya probado).
/// - Una vez instalado, este servicio chequea <see cref="AppConfig.UpdateUrl"/> al
///   arrancar el POS y, si hay versión nueva, la descarga y la deja lista para
///   aplicar en el siguiente reinicio (o aplicar inmediatamente si el usuario acepta).
///
/// Se publica el feed (carpeta o URL) con <c>vpk pack ... --packDir publish-dir</c>
/// del CLI de Velopack (https://github.com/velopack/velopack).
///
/// Si <c>UpdateUrl</c> está vacío o no hay packaging Velopack todavía, el servicio
/// queda inactivo (<see cref="UpdateState.Disabled"/>) sin afectar nada.
/// </summary>
public sealed class UpdaterService
{
    public UpdateState State { get; private set; } = UpdateState.Idle;
    public string? LastError { get; private set; }
    public string? AvailableVersion { get; private set; }
    public string? CurrentVersion { get; private set; }
    public event EventHandler? StateChanged;

    private UpdateManager? _mgr;
    private UpdateInfo? _pendingUpdate;

    public bool Enabled =>
        State != UpdateState.Disabled
        && !string.IsNullOrWhiteSpace(GetUpdateUrl());

    /// <summary>Punto de entrada llamado desde App.OnStartup (debe ser MUY temprano).</summary>
    public static void InitializeStartup()
    {
        try
        {
            // VelopackApp maneja hooks de install/uninstall y reinicio post-update.
            VelopackApp.Build()
                .OnFirstRun(_ => PosDiagnostics.Log("Velopack: primer arranque tras instalación."))
                .OnRestarted(_ => PosDiagnostics.Log("Velopack: reinicio tras actualización aplicada."))
                .Run();
        }
        catch (Exception ex)
        {
            PosDiagnostics.Log("VelopackApp.Run falló (no es crítico si no estás instalado vía Velopack).", ex);
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        var url = GetUpdateUrl();
        if (string.IsNullOrWhiteSpace(url))
        {
            Set(UpdateState.Disabled, error: null);
            return;
        }

        Set(UpdateState.Checking);
        try
        {
            _mgr = BuildManager(url);
            CurrentVersion = _mgr.CurrentVersion?.ToString();
            var info = await _mgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                AvailableVersion = null;
                _pendingUpdate = null;
                Set(UpdateState.UpToDate);
                return;
            }
            _pendingUpdate = info;
            AvailableVersion = info.TargetFullRelease?.Version?.ToString();
            Set(UpdateState.UpdateAvailable);
        }
        catch (Exception ex)
        {
            Set(UpdateState.Error, ex.Message);
            PosDiagnostics.Log("Updater CheckAsync falló", ex);
        }
    }

    public async Task DownloadAsync(CancellationToken ct = default)
    {
        if (_mgr == null || _pendingUpdate == null) { await CheckAsync(ct); }
        if (_mgr == null || _pendingUpdate == null) return;

        Set(UpdateState.Downloading);
        try
        {
            await _mgr.DownloadUpdatesAsync(_pendingUpdate).ConfigureAwait(false);
            Set(UpdateState.ReadyToApply);
        }
        catch (Exception ex)
        {
            Set(UpdateState.Error, ex.Message);
            PosDiagnostics.Log("Updater DownloadAsync falló", ex);
        }
    }

    /// <summary>Aplica el update YA: cierra la app y reinicia con la nueva versión.</summary>
    public void ApplyAndRestart()
    {
        if (_mgr == null || _pendingUpdate == null) return;
        try { _mgr.ApplyUpdatesAndRestart(_pendingUpdate); }
        catch (Exception ex)
        {
            Set(UpdateState.Error, ex.Message);
            PosDiagnostics.Log("Updater ApplyAndRestart falló", ex);
        }
    }

    /// <summary>Aplica al cerrar (sin restart inmediato).</summary>
    public void ApplyOnExit()
    {
        if (_mgr == null || _pendingUpdate == null) return;
        try { _mgr.ApplyUpdatesAndExit(_pendingUpdate); }
        catch (Exception ex)
        {
            Set(UpdateState.Error, ex.Message);
            PosDiagnostics.Log("Updater ApplyOnExit falló", ex);
        }
    }

    private static UpdateManager BuildManager(string url)
    {
        // Soporta http(s) y file:// (carpeta local de pruebas).
        IUpdateSource source = url.StartsWith("file:") || url.Contains(@"\\") || (url.Length > 1 && url[1] == ':')
            ? new SimpleFileSource(new DirectoryInfo(url.Replace("file:///", "").Replace("file://", "")))
            : new SimpleWebSource(url);
        return new UpdateManager(source);
    }

    private static string? GetUpdateUrl()
    {
        try
        {
            var cfg = AppConfig.Cargar();
            return cfg.UpdateUrl;
        }
        catch { return null; }
    }

    private void Set(UpdateState s, string? error = null)
    {
        State = s;
        LastError = error;
        try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { }
    }
}
