using System.Windows;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views;

public partial class InsertarLicenciaWindow : Window
{
    public string LicenseText => TxtLicencia.Text.Trim();

    public InsertarLicenciaWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => TxtLicencia.Focus();
    }

    private void BtnAceptar_Click(object sender, RoutedEventArgs e)
    {
        var texto = LicenseText;
        if (string.IsNullOrWhiteSpace(texto))
        {
            MessageBox.Show("Pegue o escriba el token de licencia.", "Licencia", MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var cfg = new ConfiguracionService();
        if (!LicenseService.TryValidateAndApply(texto, cfg, out var mensaje))
        {
            MessageBox.Show(mensaje, "Licencia no válida", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        App.LicenseState.RefreshFromStores();
        if (!LicenseAccessGate.IsPosAccessAllowed())
        {
            MessageBox.Show(
                App.LicenseState.BlockReason ?? "La licencia no habilita el acceso al POS.",
                "Licencia",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    private void BtnCancelar_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
