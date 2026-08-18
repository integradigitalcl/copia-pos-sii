using System.Windows;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.API;

namespace GrunflexPOS2.Views;

public partial class SupportTicketWindow : Window
{
    public SupportTicketWindow()
    {
        InitializeComponent();
        var cfg = new ConfiguracionService();
        var aid = cfg.Get("licencia_activation_id")?.Trim();
        TxtActivation.Text = string.IsNullOrEmpty(aid)
            ? "Sin ActivationId en configuración. Active la licencia en línea o use GFv2 con ActivationId."
            : $"ActivationId: {aid}";
    }

    private async void BtnEnviar_Click(object sender, RoutedEventArgs e)
    {
        var cfg = new ConfiguracionService();
        var aid = cfg.Get("licencia_activation_id")?.Trim();
        if (string.IsNullOrWhiteSpace(aid))
        {
            MessageBox.Show("No hay ActivationId. Active la licencia en línea primero.");
            return;
        }

        var subject = TxtAsunto.Text.Trim();
        var body = TxtDetalle.Text.Trim();
        if (subject.Length < 3 || body.Length < 8)
        {
            MessageBox.Show("Complete asunto (mín. 3) y detalle (mín. 8 caracteres).");
            return;
        }

        BtnEnviar.IsEnabled = false;
        var (ok, msg) = await SupportTicketApi.SubmitAsync(aid, subject, body).ConfigureAwait(true);
        BtnEnviar.IsEnabled = true;
        MessageBox.Show(msg, ok ? "Soporte" : "Error");
        if (ok)
            DialogResult = true;
    }

    private void BtnCancelar_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
