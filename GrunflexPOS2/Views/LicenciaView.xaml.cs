using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GrunflexPOS2.Data;
using GrunflexPOS2.Domain.Abstractions;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;
using GrunflexPOS2.Services.Licensing;

namespace GrunflexPOS2.Views;

public partial class LicenciaView : UserControl
{
    private static readonly SolidColorBrush GreenBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
    private static readonly SolidColorBrush GreenBgBrush = new(Color.FromRgb(0xDC, 0xFC, 0xE7));
    private static readonly SolidColorBrush AmberBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
    private static readonly SolidColorBrush AmberBgBrush = new(Color.FromRgb(0xFE, 0xF3, 0xC7));
    private static readonly SolidColorBrush RedBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));
    private static readonly SolidColorBrush RedBgBrush = new(Color.FromRgb(0xFE, 0xE2, 0xE2));

    public LicenciaView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!UsuarioPermisos.PuedeAdministrarLicencias())
            {
                IsEnabled = false;
                MessageBox.Show(
                    "Solo un administrador puede configurar licencias.",
                    UsuarioPermisosGate.Titulo,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            RefreshUi();
        };
    }

    private void RefreshUi()
    {
        App.LicenseState.RefreshFromStores();

        var api = AppConfig.Cargar();
        TxtApiUrl.Text = api.ApiBaseUrl;
        TxtMachine.Text = Environment.MachineName;
        var aid = App.LicenseState.ActivationId;
        TxtActivation.Text = string.IsNullOrEmpty(aid)
            ? "(vacío — inserte licencia o sincronice cuando exista en servidor)"
            : aid;

        var lic = App.LicenseState;
        ActualizarCajasPlan(lic);
        TxtLicensed.Text = lic.IsLicensed
            ? "Activa (al menos un módulo comercial)"
            : "Sin módulos activos o sin token válido";
        TxtLicensed.Foreground = lic.IsLicensed ? GreenBrush : RedBrush;

        TxtExpiresLocal.Text = lic.ExpiresUtc is { } exp
            ? exp.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("es-CL"))
            : "—";

        TxtModules.Text =
            $"Multicaja={lic.Multicaja}, Soporte en línea={lic.OnlineSupport}, Respaldo nube={lic.CloudBackup}, Soporte prioritario={lic.PrioritySupport}";

        TxtLastValidated.Text = lic.LastValidatedUtc is { } lv
            ? lv.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("es-CL"))
            : "—";

        if (lic.LastCloudOkUtc is { } lc)
        {
            TxtLastCloud.Text = $"OK — {lc.ToLocalTime():dd/MM/yyyy HH:mm}";
            TxtLastCloud.Foreground = GreenBrush;
        }
        else
        {
            TxtLastCloud.Text = "(aún no)";
            TxtLastCloud.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
        }

        var graceFromToken = new ConfiguracionService().Get("licencia_offline_grace_days");
        var graceSource = !string.IsNullOrWhiteSpace(graceFromToken)
            ? "definida en la licencia"
            : "default del POS (appsettings)";
        TxtGrace.Text =
            $"{lic.OfflineGraceDays} día(s) sin contacto con API ({graceSource}) antes de bloquear el POS.";

        ActualizarEstadoGeneral(lic);

        if (lic.IsBeyondOfflineGrace)
        {
            BannerGrace.Visibility = Visibility.Visible;
            TxtBannerGrace.Text = LicenseAccessGate.MensajeFueraGraciaOffline(lic.OfflineGraceDays);
        }
        else
        {
            BannerGrace.Visibility = Visibility.Collapsed;
        }
    }

    private void ActualizarCajasPlan(ILicenseStateProvider lic)
    {
        var cfg = new ConfiguracionService();
        var maxCajas = CajaSlotGate.ResolveMaxBoxes(cfg);
        var rawBoxes = cfg.Get("licencia_number_of_boxes");
        var definidoEnLicencia = int.TryParse(rawBoxes, out var tokenBoxes) && tokenBoxes > 0;

        int activas;
        try
        {
            activas = App.DbContext.Cajas.Count(c => c.Activa);
        }
        catch
        {
            activas = 0;
        }

        if (definidoEnLicencia)
        {
            TxtCajasPlan.Text = maxCajas == 1
                ? "1 caja (definida en la licencia)"
                : $"{maxCajas} cajas (definidas en la licencia)";
        }
        else if (lic.Multicaja)
        {
            TxtCajasPlan.Text = $"{maxCajas} cajas (default plan multicaja)";
        }
        else
        {
            TxtCajasPlan.Text = "1 caja (plan mono)";
        }

        TxtCajasUso.Text = activas >= maxCajas
            ? $"{activas} caja(s) activa(s) de {maxCajas} — límite alcanzado"
            : $"{activas} caja(s) activa(s) de {maxCajas}";

        TxtCajasUso.Foreground = activas >= maxCajas ? AmberBrush : new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
    }

    private void ActualizarEstadoGeneral(ILicenseStateProvider lic)
    {
        if (lic.IsBeyondOfflineGrace)
        {
            TxtEstadoGeneralTitulo.Text = "Fuera de tolerancia offline";
            TxtEstadoGeneralSub.Text = LicenseAccessGate.MensajeFueraGraciaOffline(lic.OfflineGraceDays);
            AplicarEstadoGeneralVisual(RedBrush, RedBgBrush, "\uE783");
            return;
        }

        if (!lic.IsLicensed)
        {
            TxtEstadoGeneralTitulo.Text = "Licencia no activa";
            TxtEstadoGeneralSub.Text = "Inserte o renueve la licencia para habilitar los módulos comerciales.";
            AplicarEstadoGeneralVisual(AmberBrush, AmberBgBrush, "\uE7BA");
            return;
        }

        if (lic.LastCloudOkUtc == null)
        {
            TxtEstadoGeneralTitulo.Text = "Licencia local activa";
            TxtEstadoGeneralSub.Text = "El token local es válido. Aún no se ha confirmado una sincronización exitosa con la API.";
            AplicarEstadoGeneralVisual(AmberBrush, AmberBgBrush, "\uE73E");
            return;
        }

        TxtEstadoGeneralTitulo.Text = "Todo en orden";
        TxtEstadoGeneralSub.Text = "La licencia local está activa y sincronizada correctamente con la API.";
        AplicarEstadoGeneralVisual(GreenBrush, GreenBgBrush, "\uE73E");
    }

    private void AplicarEstadoGeneralVisual(SolidColorBrush fg, SolidColorBrush bg, string iconGlyph)
    {
        BorderEstadoGeneralIcon.Background = bg;
        TbEstadoGeneralIcon.Foreground = fg;
        TbEstadoGeneralIcon.Text = iconGlyph;
    }

    private async void BtnSync_Click(object sender, RoutedEventArgs e)
    {
        if (!UsuarioPermisosGate.EnsureLicencias())
            return;

        BtnActualizar.IsEnabled = false;
        try
        {
            var (ok, msg) = await LicensingCloudService.TryRefreshAsync(silent: false).ConfigureAwait(true);
            MessageBox.Show(
                string.IsNullOrEmpty(msg) ? (ok ? "Listo." : "No se pudo sincronizar.") : msg,
                ok ? "Licencia" : "Error",
                MessageBoxButton.OK,
                ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            RefreshUi();
            if (Window.GetWindow(this) is CajaView caja)
                caja.RefrescarEstadoPremium();
        }
        finally
        {
            BtnActualizar.IsEnabled = true;
        }
    }

    private async void BtnStatus_Click(object sender, RoutedEventArgs e)
    {
        TxtServerStatus.Text = "Consultando…";
        TxtServerStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        var st = await LicensingCloudService.TryGetServerStatusAsync().ConfigureAwait(true);
        if (st == null)
        {
            TxtServerStatus.Text = "Sin ActivationId local — no se puede consultar.";
            TxtServerStatus.Foreground = RedBrush;
            return;
        }

        if (!string.IsNullOrEmpty(st.Error))
        {
            TxtServerStatus.Text = "Error: " + st.Error;
            TxtServerStatus.Foreground = RedBrush;
            return;
        }

        if (st.HttpStatus is { } code && code != 200)
        {
            TxtServerStatus.Text = $"HTTP {code}: {st.RawBody}";
            TxtServerStatus.Foreground = RedBrush;
            return;
        }

        if (!st.Found)
        {
            TxtServerStatus.Text = "Servidor: ActivationId no encontrado.";
            TxtServerStatus.Foreground = AmberBrush;
            return;
        }

        var expLocal = st.ExpUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("es-CL"));
        TxtServerStatus.Text =
            (st.Expired ? "Servidor: licencia VENCIDA. " : "Servidor: vigente. ") +
            $"Expira (UTC almacenado): {expLocal}. " +
            $"Módulos servidor: Multicaja={st.Multicaja}, SoporteWeb={st.OnlineSupport}, Nube={st.CloudBackup}, Prioritario={st.PrioritySupport}.";
        TxtServerStatus.Foreground = st.Expired ? RedBrush : GreenBrush;
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        App.LicenseState.RefreshFromStores();
        var lic = App.LicenseState;
        var api = AppConfig.Cargar();
        var sb = new StringBuilder();
        sb.AppendLine("Grunflex POS — diagnóstico licencia");
        sb.AppendLine("Api: " + api.ApiBaseUrl);
        sb.AppendLine("Equipo: " + Environment.MachineName);
        sb.AppendLine("ActivationId: " + (lic.ActivationId ?? ""));
        sb.AppendLine("Licensed: " + lic.IsLicensed);
        sb.AppendLine("ExpiresUtc: " + (lic.ExpiresUtc?.ToString("O") ?? ""));
        sb.AppendLine("LastValidatedUtc: " + (lic.LastValidatedUtc?.ToString("O") ?? ""));
        sb.AppendLine("LastCloudOkUtc: " + (lic.LastCloudOkUtc?.ToString("O") ?? ""));
        sb.AppendLine("OfflineGraceDays: " + lic.OfflineGraceDays);
        sb.AppendLine("OfflineGraceSource: " + (App.LicenseState.ActivationId != null ? "license/appsettings" : "appsettings"));
        sb.AppendLine("BeyondGrace: " + lic.IsBeyondOfflineGrace);
        var cfgDiag = new ConfiguracionService();
        sb.AppendLine("NumberOfBoxes: " + CajaSlotGate.ResolveMaxBoxes(cfgDiag));
        try
        {
            sb.AppendLine("CajasActivas: " + App.DbContext.Cajas.Count(c => c.Activa));
        }
        catch
        {
            sb.AppendLine("CajasActivas: (no disponible)");
        }
        sb.AppendLine("Modules: " + lic.Multicaja + "," + lic.OnlineSupport + "," + lic.CloudBackup + "," + lic.PrioritySupport);
        try
        {
            Clipboard.SetText(sb.ToString());
            MessageBox.Show("Diagnóstico copiado al portapapeles.", "Licencia");
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo copiar: " + ex.Message);
        }
    }
}
