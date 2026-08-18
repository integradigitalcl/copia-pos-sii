using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Navigation;
using System.Windows.Threading;
using System.Windows.Controls;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;
using Microsoft.Win32;

namespace GrunflexPOS2.Views;

public partial class ActivacionLicenciaWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _segundosRestantes = 3;
    private readonly ActivacionLicenciaModo _modo;

    public ActivacionLicenciaWindow(ActivacionLicenciaModo modo = ActivacionLicenciaModo.PrimeraVez)
    {
        _modo = modo;
        InitializeComponent();
        TxtNombreEquipo.Text = Environment.MachineName;

        switch (_modo)
        {
            case ActivacionLicenciaModo.Expirada:
                Title = "Licencia expirada — Grunflex POS";
                PanelBienvenida.Visibility = Visibility.Collapsed;
                PanelLicencia.Visibility = Visibility.Visible;
                TxtTituloLicencia.Text = "Licencia expirada";
                TxtBannerExpirada.Visibility = Visibility.Visible;
                TxtBannerExpirada.Text =
                    "Su licencia ha expirado. Ingrese una licencia vigente para volver a operar el POS.";
                BtnContinuar.Visibility = Visibility.Collapsed;
                break;

            case ActivacionLicenciaModo.FueraGraciaOffline:
                Title = "Sincronización requerida — Grunflex POS";
                PanelBienvenida.Visibility = Visibility.Collapsed;
                PanelLicencia.Visibility = Visibility.Visible;
                TxtTituloLicencia.Text = "Sincronización con servidor requerida";
                TxtBannerExpirada.Visibility = Visibility.Visible;
                TxtBannerExpirada.Text = LicenseAccessGate.MensajeFueraGraciaOffline(App.LicenseState.OfflineGraceDays);
                BtnContinuar.Visibility = Visibility.Collapsed;
                BtnSincronizarServidor.Visibility = Visibility.Visible;
                var aid = App.LicenseState.ActivationId;
                if (!string.IsNullOrWhiteSpace(aid))
                    TxtActivationId.Text = aid;
                break;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += TimerOnTick;
        Loaded += (_, _) =>
        {
            if (_modo != ActivacionLicenciaModo.PrimeraVez)
                return;

            ActualizarTextoEspera();
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        _segundosRestantes--;
        if (_segundosRestantes <= 0)
        {
            _timer.Stop();
            BtnContinuar.IsEnabled = true;
            BtnContinuar.Content = "Continuar";
            return;
        }

        ActualizarTextoEspera();
    }

    private void ActualizarTextoEspera()
    {
        BtnContinuar.Content = $"Espere {_segundosRestantes} segundos para continuar…";
    }

    private void BtnContinuar_Click(object sender, RoutedEventArgs e)
    {
        PanelBienvenida.Visibility = Visibility.Collapsed;
        PanelLicencia.Visibility = Visibility.Visible;
        TxtLicencia.Focus();
    }

    private void BtnBuscarArchivo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Licencia (*.lic)|*.lic|Texto (*.txt)|*.txt|Todos los archivos (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true)
            return;

        try
        {
            TxtLicencia.Text = File.ReadAllText(dlg.FileName).Trim();
            TxtErrorLicencia.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            MostrarError("No se pudo leer el archivo: " + ex.Message);
        }
    }

    private async void BtnSincronizarServidor_Click(object sender, RoutedEventArgs e)
    {
        var clickBtn = sender as Button;
        if (clickBtn != null)
            clickBtn.IsEnabled = false;
        try
        {
            var (ok, msg) = await LicensingCloudService.TryRefreshAsync(silent: false).ConfigureAwait(true);
            if (!ok)
            {
                MostrarError(string.IsNullOrWhiteSpace(msg)
                    ? "No se pudo sincronizar con el servidor. Verifique conexión e intente de nuevo."
                    : msg);
                return;
            }

            if (!TryFinishIfAccessAllowed())
                return;
        }
        finally
        {
            if (clickBtn != null)
                clickBtn.IsEnabled = true;
        }
    }

    private async void BtnActivarOnline_Click(object sender, RoutedEventArgs e)
    {
        var aid = TxtActivationId.Text.Trim();
        if (string.IsNullOrWhiteSpace(aid))
        {
            MostrarError("Ingrese el código de activación (ActivationId).");
            return;
        }

        var clickBtn = sender as Button;
        if (clickBtn != null)
            clickBtn.IsEnabled = false;
        try
        {
            var (ok, token, msg) = await LicensingActivateApi.TryActivateAsync(aid, Environment.MachineName)
                .ConfigureAwait(true);
            if (!ok || string.IsNullOrWhiteSpace(token))
            {
                MostrarError(msg);
                return;
            }

            TxtLicencia.Text = token;
            try
            {
                if (!TryAplicarLicenciaYContinuar(token, markCloudSyncOk: true))
                    return;
            }
            catch (Exception ex)
            {
                MostrarError(FormatearErrorPersistencia(ex));
                return;
            }
        }
        finally
        {
            if (clickBtn != null)
                clickBtn.IsEnabled = true;
        }
    }

    private void BtnActivar_Click(object sender, RoutedEventArgs e)
    {
        var licencia = TxtLicencia.Text.Trim();
        if (string.IsNullOrWhiteSpace(licencia))
        {
            MostrarError("Ingrese la licencia o seleccione un archivo.");
            return;
        }

        try
        {
            if (!TryAplicarLicenciaYContinuar(licencia))
                return;
        }
        catch (Exception ex)
        {
            MostrarError(FormatearErrorPersistencia(ex));
        }
    }

    private bool TryAplicarLicenciaYContinuar(string licencia, bool markCloudSyncOk = false)
    {
        var cfg = new ConfiguracionService(App.DbContext);
        if (!LicenseService.TryValidateAndApply(licencia, cfg, out var mensaje))
        {
            MostrarError(mensaje);
            return false;
        }

        if (markCloudSyncOk)
            cfg.Set("licencia_last_cloud_ok_utc", DateTime.UtcNow.ToString("O"));

        cfg.Set("licencia_multicaja", App.LicenseState.Multicaja ? "true" : "false");
        cfg.Set("licencia_online_support", App.LicenseState.OnlineSupport ? "true" : "false");
        LicenseService.TryApplyStoredLicense(cfg, out _);
        App.LicenseState.RefreshFromStores();

        return TryFinishIfAccessAllowed();
    }

    private bool TryFinishIfAccessAllowed()
    {
        App.LicenseState.RefreshFromStores();
        if (!App.LicenseState.IsValid)
        {
            MostrarError(App.LicenseState.BlockReason ?? "La licencia no es válida.");
            return false;
        }

        if (_modo == ActivacionLicenciaModo.FueraGraciaOffline && App.LicenseState.IsBeyondOfflineGrace)
        {
            MostrarError(
                "La licencia local sigue vigente, pero debe sincronizar con el servidor " +
                "usando «Sincronizar licencia con servidor» o «Obtener licencia desde servidor».");
            return false;
        }

        DialogResult = true;
        Close();
        return true;
    }

    private static string FormatearErrorPersistencia(Exception ex)
    {
        var inner = ex;
        while (inner.InnerException != null)
            inner = inner.InnerException;
        if (inner != ex && !string.IsNullOrWhiteSpace(inner.Message))
            return inner.Message;
        return ex.Message;
    }

    private void MostrarError(string mensaje)
    {
        TxtErrorLicencia.Text = mensaje;
        TxtErrorLicencia.Visibility = Visibility.Visible;
    }

    private void BtnSalir_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
        if (_modo is ActivacionLicenciaModo.Expirada or ActivacionLicenciaModo.FueraGraciaOffline)
            System.Windows.Application.Current.Shutdown();
    }

    private void Soporte_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // ignorar
        }

        e.Handled = true;
    }
}
