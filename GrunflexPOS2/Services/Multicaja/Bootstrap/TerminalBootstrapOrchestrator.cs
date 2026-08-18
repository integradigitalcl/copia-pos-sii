using System;
using System.Threading;
using System.Threading.Tasks;
using GrunflexPOS2.Data;
using GrunflexPOS2.Infrastructure.Setup;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Licensing;
using GrunflexPOS2.Services.Multicaja.Sync;

using GrunflexPOS2.Services.Multicaja.Terminal;

namespace GrunflexPOS2.Services.Multicaja.Bootstrap;

public sealed class TerminalBootstrapOrchestrator
{
    private readonly ApiReadinessService _readiness = new();
    private readonly MulticajaSyncCoordinator _sync;
    private readonly TerminalService? _terminal;
    private readonly TerminalRegistrationClient? _legacyTerminal;

    public TerminalBootstrapOrchestrator(
        MulticajaSyncCoordinator sync,
        TerminalService? terminal,
        TerminalRegistrationClient? legacyTerminal)
    {
        _sync = sync;
        _terminal = terminal;
        _legacyTerminal = legacyTerminal;
    }

    public async Task<BootstrapResult> RunAsync(Action<string>? onStatus, CancellationToken ct = default)
    {
        if (!MulticajaRuntime.UseApiOnlyClient)
            return BootstrapResult.NotApplicable();

        App.LicenseState.RefreshFromStores();
        if (!App.LicenseState.Multicaja)
            return BootstrapResult.Fail(LicenseAccessGate.MensajeMulticajaRequerida);

        onStatus?.Invoke("Aplicando configuración de terminal…");
        TerminalConfigBootstrap.ApplyIfNeeded();
        TerminalConfigBootstrap.PersistToMachineScopeIfNeeded();

        var cfg = AppConfig.Cargar();
        var validation = ValidateClientConfig(cfg);
        if (validation != null)
            return BootstrapResult.Fail(validation);

        onStatus?.Invoke("Esperando servidor (API)…");
        var apiOk = await _readiness.WaitUntilHealthyAsync(
            cfg.ApiBaseUrl,
            TimeSpan.FromSeconds(90),
            onStatus,
            ct).ConfigureAwait(false);

        if (!apiOk)
        {
            return BootstrapResult.Fail(
                "No se pudo conectar con el servidor principal.\n\n" +
                $"URL: {cfg.ApiBaseUrl}\n\n" +
                "Verifique que la API esté en marcha en la caja principal, el firewall (puerto 7279) " +
                "y que la IP en la plantilla sea la de red (no localhost).");
        }

        if (_terminal != null)
        {
            onStatus?.Invoke("Registrando terminal (identidad persistente)…");
            await _terminal.EnsureRegisteredAsync(ct).ConfigureAwait(false);
            if (!_terminal.LastGranted)
            {
                return BootstrapResult.Fail(
                    string.IsNullOrWhiteSpace(_terminal.LastReason)
                        ? LicenseAccessGate.MensajeLimiteCajasConPlan(5)
                        : _terminal.LastReason!);
            }

            await _terminal.SendHeartbeatNowAsync(reconnect: true, ct).ConfigureAwait(false);
        }
        else if (_legacyTerminal != null)
        {
            onStatus?.Invoke("Registrando terminal en el servidor…");
            var registered = await _legacyTerminal.EnsureRegisteredAsync(ct).ConfigureAwait(false);
            if (!registered)
            {
                return BootstrapResult.Fail(
                    string.IsNullOrWhiteSpace(_legacyTerminal.LastReason)
                        ? LicenseAccessGate.MensajeLimiteCajas
                        : _legacyTerminal.LastReason!);
            }

            await _legacyTerminal.SendHeartbeatNowAsync(ct).ConfigureAwait(false);
            _legacyTerminal.StartHeartbeat(TimeSpan.FromSeconds(90));
        }

        onStatus?.Invoke("Sincronizando catálogo y cajeros…");
        var sync = await _sync.PullBootstrapFullAsync(ct).ConfigureAwait(false);
        if (!sync.Ok)
        {
            return BootstrapResult.Fail(
                "No se pudo sincronizar el catálogo desde el servidor:\n\n" +
                (sync.Error ?? "Error desconocido"));
        }

        if (Guid.TryParse(cfg.CajaId, out var cajaId) && cajaId != Guid.Empty)
        {
            onStatus?.Invoke("Vinculando equipo con la caja…");
            try
            {
                await MulticajaOperacionesClient.VincularEquipoCajaAsync(cajaId, Environment.MachineName, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("bootstrap vincular-equipo", ex);
            }
        }

        onStatus?.Invoke("Terminal lista.");
        PosDiagnostics.Log("multicaja.bootstrap ok");
        return BootstrapResult.Ready();
    }

    public static string? ValidateClientConfig(AppConfig cfg)
    {
        if (!cfg.EsCajaAdicional || !cfg.UseMulticajaApiOnlyClient)
            return null;

        if (cfg.TieneConexionUnc)
        {
            return "Esta terminal usa recurso de red SMB (UNC), obsoleto en modo API-only.\n\n" +
                   "Regenere la plantilla grunflex-terminal.json desde la caja principal (Conectar caja).";
        }

        if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
            return "Falta Api:BaseUrl en appsettings.local.json.";

        if (!Uri.TryCreate(cfg.ApiBaseUrl.Trim(), UriKind.Absolute, out var uri))
            return "Api:BaseUrl no es una URL válida.";

        if (uri.IsLoopback)
        {
            return "Api.BaseUrl no puede ser localhost en una caja adicional.\n\n" +
                   $"Valor actual: {cfg.ApiBaseUrl}\n\n" +
                   "Use la IP LAN del servidor (ej. http://192.168.1.10:7279/).";
        }

        if (!Guid.TryParse((cfg.CajaId ?? "").Trim(), out var cajaId) || cajaId == Guid.Empty)
        {
            return "CajaId inválido o vacío.\n\n" +
                   "En la caja principal: Cajas → Conectar más cajas → cree la caja y copie " +
                   "grunflex-terminal.json a este equipo (Escritorio), o verifique que la API esté en marcha " +
                   "para el registro automático.";
        }

        return null;
    }
}
