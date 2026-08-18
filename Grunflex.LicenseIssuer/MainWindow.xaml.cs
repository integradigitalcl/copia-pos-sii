using System.Windows;
using Grunflex.LicenseIssuer.Dialogs;
using Grunflex.LicenseIssuer.ViewModels;

namespace Grunflex.LicenseIssuer;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.StopHostMonitoring();
    }

    private async void NavDashboard_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToSection("Dashboard");
        await _viewModel.RefreshDashboardStatsAsync();
    }

    private async void NavLicencias_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToSection("Licencias");
        await _viewModel.LoadLicensesFromServerAsync();
    }

    private void NavGenerar_Click(object sender, RoutedEventArgs e) => _viewModel.NavigateToSection("GenerarLicencia");

    private async void NavClientes_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToSection("Clientes");
        await _viewModel.LoadClientsFromServerAsync();
    }

    private async void NavActivaciones_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToSection("Activaciones");
        await _viewModel.LoadActivationsFromServerAsync();
    }

    private async void NavReportes_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.NavigateToSection("Reportes");
        await _viewModel.RefreshDashboardStatsAsync();
    }

    private void NavConfiguracion_Click(object sender, RoutedEventArgs e) => _viewModel.NavigateToSection("Configuracion");

    private void ConfigTabGeneral_Click(object sender, RoutedEventArgs e) => _viewModel.SelectConfigTab("General");

    private void ConfigTabCorreo_Click(object sender, RoutedEventArgs e) => _viewModel.SelectConfigTab("Correo");

    private void ConfigTabSeguridad_Click(object sender, RoutedEventArgs e) => _viewModel.SelectConfigTab("Seguridad");

    private void ConfigTabRespaldos_Click(object sender, RoutedEventArgs e) => _viewModel.SelectConfigTab("Respaldos");

    private void ConfigTabSistema_Click(object sender, RoutedEventArgs e) => _viewModel.SelectConfigTab("Sistema");

    private async void NuevoCliente_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new EditClientDialog("Nuevo cliente", "", "", "", "") { Owner = this };
        if (dlg.ShowDialog() != true)
            return;

        if (await _viewModel.TryCreateClientAsync(dlg.NameResult, dlg.BusinessResult, dlg.EmailResult, dlg.PhoneResult))
            MessageBox.Show("Cliente registrado en el servidor.", "Clientes", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(
                "No se pudo crear el cliente. Verifique que GrunflexPOS.API esté en ejecución y la URL en Configuración.",
                "Clientes",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
    }
}