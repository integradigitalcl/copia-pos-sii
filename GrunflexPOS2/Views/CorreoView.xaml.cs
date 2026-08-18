using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class CorreoView : UserControl
    {
        private readonly ConfiguracionService _configService;
        private readonly EmailService _emailService;

        public CorreoView()
        {
            InitializeComponent();

            _configService = new ConfiguracionService();
            _emailService = new EmailService();

            this.Loaded += CorreoView_Loaded;
        }

        private void CorreoView_Loaded(object sender, RoutedEventArgs e)
        {
            bool activo = _configService.GetCorreoActivo();
            string correo = _configService.GetCorreoEmail();
            string clave = _configService.GetCorreoClave();

            chkCorreo.IsChecked = activo;
            txtCorreo.Text = correo;
            txtClave.Password = clave;

            panelCorreo.IsEnabled = activo;

            txtHost.Text = _configService.Get("correo_host");
            txtPuerto.Text = _configService.Get("correo_puerto");
            chkSSL.IsChecked = _configService.Get("correo_ssl") == "true";
        }

        private void chkCorreo_Checked(object sender, RoutedEventArgs e)
        {
            panelCorreo.IsEnabled = true;
        }

        private void chkCorreo_Unchecked(object sender, RoutedEventArgs e)
        {
            panelCorreo.IsEnabled = false;
        }

        // 🚀 NUEVO BOTÓN INTELIGENTE
        private void BtnConfigurarGmail_Click(object sender, RoutedEventArgs e)
        {
            string correo = txtCorreo.Text.Trim().ToLower();

            if (string.IsNullOrWhiteSpace(correo))
            {
                MessageBox.Show("Primero ingresa tu correo.");
                return;
            }

            if (!correo.Contains("@gmail.com"))
            {
                MessageBox.Show("Este botón es solo para cuentas Gmail.");
                return;
            }

            txtHost.Text = "smtp.gmail.com";
            txtPuerto.Text = "587";
            chkSSL.IsChecked = true;

            MessageBox.Show("Configuración Gmail aplicada automáticamente.\nSolo debes ingresar tu contraseña de aplicación.");
        }

        private void BtnProbarCorreo_Click(object sender, RoutedEventArgs e)
        {
            string correo = txtCorreo.Text.Trim();
            string clave = txtClave.Password.Trim();
            bool activo = chkCorreo.IsChecked == true;

            string host = txtHost.Text.Trim();
            string puertoTexto = txtPuerto.Text.Trim();
            bool ssl = chkSSL.IsChecked == true;

            int puerto = 587;
            int.TryParse(puertoTexto, out puerto);

            if (activo)
            {
                if (string.IsNullOrWhiteSpace(correo))
                {
                    MessageBox.Show("Debes ingresar un correo electrónico.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(clave))
                {
                    MessageBox.Show("Debes ingresar la clave del correo.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(host))
                {
                    MessageBox.Show("Debes ingresar el servidor SMTP.");
                    return;
                }
            }

            _configService.SetCorreoActivo(activo);
            _configService.SetCorreoEmail(correo);
            _configService.SetCorreoClave(clave);

            _configService.Set("correo_host", host);
            _configService.Set("correo_puerto", puerto.ToString());
            _configService.Set("correo_ssl", ssl ? "true" : "false");

            if (activo)
            {
                string error;

                bool enviado = _emailService.EnviarCorreo(
                    correo,
                    "Prueba de correo - Grunflex POS",
                    "Este es un correo de prueba enviado desde Grunflex POS.",
                    host,
                    puerto,
                    ssl,
                    correo,
                    clave,
                    out error
                );

                if (enviado)
                {
                    MessageBox.Show("Correo enviado correctamente.");
                }
                else
                {
                    MessageBox.Show($"Error al enviar correo:\n{error}");
                }
            }
            else
            {
                MessageBox.Show("Configuración guardada.");
            }
        }
    }
}