using System;
using System.Linq;
using System.Windows;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Views
{
    public partial class LoginView : Window
    {
        private readonly UsuarioService _usuarioService = new();

        public LoginView()
        {
            InitializeComponent();
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string username = TxtUsuario.Text.Trim();
                string password = TxtPassword.Password.Trim();

                var usuario = await _usuarioService.Login(username, password);

                if (usuario == null)
                {
                    MessageBox.Show("Usuario o contraseña incorrectos");
                    return;
                }

                App.UsuarioActual = usuario;

                var caja = App.DbContext.Cajas.FirstOrDefault();

                if (caja == null)
                {
                    MessageBox.Show("No hay caja en la base de datos");
                    return;
                }

                var sesion = App.DbContext.CajaSesiones
                    .FirstOrDefault(c => c.Abierta && c.CajaId == caja.Id);

                if (sesion == null)
                {
                    var nombreEquipo = Environment.MachineName;
                    int numeroCaja = Math.Abs(nombreEquipo.GetHashCode() % 100) + 1;

                    sesion = new CajaSesion
                    {
                        Id = Guid.NewGuid(),
                        CajaId = caja.Id,
                        NumeroCaja = numeroCaja,

                        Cajero = usuario.Username,
                        UsuarioAperturaId = usuario.Id,

                        FechaApertura = DateTime.UtcNow,
                        MontoApertura = 0,
                        Abierta = true,

                        TotalVentas = 0,
                        TotalIngresos = 0,
                        TotalRetiros = 0
                    };

                    App.DbContext.CajaSesiones.Add(sesion);
                    App.DbContext.SaveChanges();
                }

                App.CajaActualId = sesion.CajaId;

                var cajaView = new CajaView();
                System.Windows.Application.Current.MainWindow = cajaView;
                cajaView.Show();

                this.Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show("ERROR REAL:\n" + ex.ToString());
            }
        }
    }
}
