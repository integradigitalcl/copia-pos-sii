using System.Windows;
using GrunflexPOS2.Views;

namespace GrunflexPOS2.Views
{
    public partial class LoginView : Window
    {
        public LoginView()
        {
            InitializeComponent();
        }

        private void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            // 🔐 Login simple de prueba
            if (TxtUsuario.Text == "admin" && TxtPassword.Password == "1234")
            {
                CajaView caja = new CajaView();
                caja.Show();
                this.Close();
            }
            else
            {
                MessageBox.Show(
                    "Usuario o contraseña incorrectos",
                    "Error de login",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
        }
    }
}