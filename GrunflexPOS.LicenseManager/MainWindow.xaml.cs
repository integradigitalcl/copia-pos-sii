using System.Windows;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Data;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;

namespace GrunflexPOS.LicenseManager;

public partial class MainWindow : Window
{
    private readonly ConfiguracionService _cfg = new();
    private readonly LicenseStateProvider _licenseState = new();
    private bool _adminValidated;

    public MainWindow()
    {
        InitializeComponent();
        AppendStatus("Gestor iniciado. Valida credenciales de administrador para continuar.");
    }

    private void ValidarAdmin_Click(object sender, RoutedEventArgs e)
    {
        string username = (TxtUsuario.Text ?? string.Empty).Trim();
        string password = TxtPassword.Password ?? string.Empty;

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            MessageBox.Show("Ingresa usuario y contrasena.");
            return;
        }

        using var db = new GrunflexDbContext();
        var usuario = db.Usuarios.FirstOrDefault(u => u.Username == username);
        if (usuario == null || !PasswordHasher.TryVerifyAndUpgrade(password, usuario.Password, out var upgraded))
        {
            _adminValidated = false;
            SetAdminUiState();
            MessageBox.Show("Credenciales invalidas.");
            return;
        }

        if (upgraded != null)
        {
            usuario.Password = upgraded;
            db.SaveChanges();
        }

        bool isAdminByUser = string.Equals(usuario.Username, "admin", StringComparison.OrdinalIgnoreCase);
        bool isAdminByRole = string.Equals(usuario.Rol, "admin", StringComparison.OrdinalIgnoreCase);
        if (!isAdminByUser && !isAdminByRole)
        {
            _adminValidated = false;
            SetAdminUiState();
            MessageBox.Show("El usuario no tiene privilegios de administrador.");
            return;
        }

        _adminValidated = true;
        SetAdminUiState();
        AppendStatus($"Administrador validado: {usuario.Username}.");
        TxtActivationId.Text = (_cfg.Get("licencia_activation_id") ?? string.Empty).Trim();
    }

    private void AplicarLicencia_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminValidated)
        {
            MessageBox.Show("Primero valida un usuario administrador.");
            return;
        }

        string licencia = (TxtLicencia.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(licencia))
        {
            MessageBox.Show("Pega una licencia para aplicar.");
            return;
        }

        if (!LicenseService.TryValidateAndApply(licencia, _cfg, out var mensaje))
        {
            AppendStatus($"Error al aplicar licencia: {mensaje}");
            MessageBox.Show("Licencia invalida.\n" + mensaje);
            return;
        }

        _licenseState.RefreshFromStores();
        AppendStatus("Licencia aplicada correctamente.");
        AppendStatus($"Expiracion UTC: {_cfg.Get("licencia_exp_utc")}");
        AppendStatus($"Gracia offline: {LicenseService.ResolveOfflineGraceDays(0)} día(s)");
        AppendStatus($"Activation ID: {_cfg.Get("licencia_activation_id")}");
        MessageBox.Show("Licencia aplicada correctamente.");
    }

    private void CargarActual_Click(object sender, RoutedEventArgs e)
    {
        string licencia = (_cfg.Get("licencia_key") ?? string.Empty).Trim();
        TxtActivationId.Text = (_cfg.Get("licencia_activation_id") ?? string.Empty).Trim();
        TxtLicencia.Text = licencia;

        if (string.IsNullOrWhiteSpace(licencia))
        {
            AppendStatus("No hay licencia almacenada.");
            return;
        }

        bool valida = LicenseService.TryValidateAndApply(licencia, _cfg, out var mensaje);
        _licenseState.RefreshFromStores();
        AppendStatus($"Licencia actual cargada. Valida: {valida}. Estado: {mensaje}");
        AppendStatus($"Expiracion UTC: {_cfg.Get("licencia_exp_utc")}");
    }

    private void SetAdminUiState()
    {
        TxtLicencia.IsEnabled = _adminValidated;
        TxtActivationId.IsEnabled = _adminValidated;
        BtnAplicarLicencia.IsEnabled = _adminValidated;
        BtnActivarOnline.IsEnabled = _adminValidated;
    }

    private async void ActivarOnline_Click(object sender, RoutedEventArgs e)
    {
        if (!_adminValidated)
        {
            MessageBox.Show("Primero valida un usuario administrador.");
            return;
        }

        string activationId = (TxtActivationId.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(activationId))
        {
            MessageBox.Show("Ingresa un Activation ID.");
            return;
        }

        BtnActivarOnline.IsEnabled = false;
        try
        {
            var (ok, token, msg) = await LicensingActivateApi.TryActivateAsync(activationId, Environment.MachineName);
            if (!ok || string.IsNullOrWhiteSpace(token))
            {
                AppendStatus($"Activación online fallida: {msg}");
                MessageBox.Show("No se pudo activar online:\n" + msg);
                return;
            }

            TxtLicencia.Text = token.Trim();
            if (!LicenseService.TryValidateAndApply(TxtLicencia.Text, _cfg, out var validateMsg))
            {
                AppendStatus($"Token recibido pero inválido: {validateMsg}");
                MessageBox.Show("Token recibido, pero no se pudo validar:\n" + validateMsg);
                return;
            }

            _licenseState.RefreshFromStores();
            AppendStatus("Activación online OK y licencia aplicada.");
            AppendStatus($"Expiracion UTC: {_cfg.Get("licencia_exp_utc")}");
            AppendStatus($"Activation ID: {_cfg.Get("licencia_activation_id")}");
            MessageBox.Show("Activación online completada.");
        }
        finally
        {
            BtnActivarOnline.IsEnabled = _adminValidated;
        }
    }

    private void AppendStatus(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        TxtEstado.AppendText(line + Environment.NewLine);
        TxtEstado.ScrollToEnd();
    }
}
