using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services.API;

namespace GrunflexPOS2.Views;

public partial class RespaldoNubeView : UserControl
{
    private readonly ObservableCollection<BackupListRow> _rows = new();

    public RespaldoNubeView()
    {
        InitializeComponent();
        GridRespaldos.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            if (!UsuarioPermisos.PuedeRestaurarRespaldos())
            {
                IsEnabled = false;
                MessageBox.Show(
                    "Solo un administrador puede administrar respaldos en nube.",
                    UsuarioPermisosGate.Titulo,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            _ = CargarListaAsync();
        };
    }

    private async void BtnActualizarLista_Click(object sender, RoutedEventArgs e)
    {
        BtnActualizarLista.IsEnabled = false;
        try
        {
            await CargarListaAsync().ConfigureAwait(true);
        }
        finally
        {
            BtnActualizarLista.IsEnabled = true;
        }
    }

    private async Task CargarListaAsync()
    {
        TxtEstado.Text = "Cargando…";
        _rows.Clear();
        var (ok, items, msg) = await BackupsApiClient.ListAsync().ConfigureAwait(true);
        if (!ok)
        {
            TxtEstado.Text = msg;
            return;
        }

        foreach (var x in items)
            _rows.Add(x);

        TxtEstado.Text = $"{items.Count} archivo(s) en el servidor.";
    }

    private async void BtnEliminarSeleccionado_Click(object sender, RoutedEventArgs e)
    {
        if (!UsuarioPermisosGate.EnsureRestaurarRespaldos())
            return;

        if (GridRespaldos.SelectedItem is not BackupListRow row)
        {
            MessageBox.Show("Seleccione una fila.", "Respaldo en nube");
            return;
        }

        if (MessageBox.Show(
                $"¿Eliminar del servidor?\n{row.RelativePath}",
                "Confirmar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        BtnEliminarSeleccionado.IsEnabled = false;
        try
        {
            var (ok, msg) = await BackupsApiClient.DeleteAsync(row.RelativePath).ConfigureAwait(true);
            MessageBox.Show(msg, ok ? "Listo" : "Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            await CargarListaAsync().ConfigureAwait(true);
        }
        finally
        {
            BtnEliminarSeleccionado.IsEnabled = true;
        }
    }
}
