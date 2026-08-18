using System.Windows;

namespace GrunflexPOS2.Views
{
    public partial class TicketPdfSuccessWindow : Window
    {
        public TicketPdfSuccessWindow(
            string rutaArchivo,
            string titulo = "Ticket reimpreso correctamente",
            string subtitulo = "El ticket se ha guardado en la siguiente ubicación:")
        {
            InitializeComponent();
            TxtTitulo.Text = titulo;
            TxtSubtitulo.Text = subtitulo;
            TxtRuta.Text = rutaArchivo ?? string.Empty;
        }

        private void Aceptar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void Cerrar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
