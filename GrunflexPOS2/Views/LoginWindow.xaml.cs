using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Grunflex.Licensing;
using GrunflexPOS2.Licensing;
using GrunflexPOS2.Services;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2;
using GrunflexPOS2.Data;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services.Multicaja;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views
{
    public partial class LoginWindow : Window
    {
        private UsuarioService _usuarioService = new UsuarioService();

        public LoginWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => OfrecerCrearAdministradorSiCorresponde();
        }

        /// <summary>
        /// Respaldo: si el asistente de primera instalación no corrió (p. ej. instalador viejo),
        /// ofrecer crear el administrador antes del primer login en caja principal.
        /// </summary>
        private void OfrecerCrearAdministradorSiCorresponde()
        {
            if (MulticajaRuntime.UseApiOnlyClient)
                return;

            var cfg = AppConfig.Cargar();
            if (cfg.EsCajaAdicional || App.DbContext == null)
                return;

            try
            {
                if (App.DbContext.Usuarios.Any())
                    return;
            }
            catch
            {
                return;
            }

            var crearAdmin = new CrearUsuarioInicialWindow();
            crearAdmin.ShowDialog();
        }

        private void TxtPassword_PasswordChanged(object sender, RoutedEventArgs e)
        {
            ActualizarHintPassword();
        }

        private void TxtPasswordPlain_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ActualizarHintPassword();
        }

        private void ActualizarHintPassword()
        {
            var vacio = txtPassword.Visibility == Visibility.Visible
                ? string.IsNullOrEmpty(txtPassword.Password)
                : string.IsNullOrEmpty(txtPasswordPlain.Text);
            TxtPasswordHint.Visibility = vacio ? Visibility.Visible : Visibility.Collapsed;
        }

        private void BtnTogglePassword_Click(object sender, RoutedEventArgs e)
        {
            if (txtPassword.Visibility == Visibility.Visible)
            {
                txtPasswordPlain.Text = txtPassword.Password;
                txtPassword.Visibility = Visibility.Collapsed;
                txtPasswordPlain.Visibility = Visibility.Visible;
                txtPasswordPlain.Focus();
            }
            else
            {
                txtPassword.Password = txtPasswordPlain.Text;
                txtPasswordPlain.Visibility = Visibility.Collapsed;
                txtPassword.Visibility = Visibility.Visible;
                txtPassword.Focus();
            }

            ActualizarHintPassword();
        }

        private async Task IntentarLoginAsync()
        {
            if (!LicenseAccessGate.EnsureValidForPosAccess(this))
                return;

            var username = txtUsuario.Text;
            var password = txtPassword.Visibility == Visibility.Visible
                ? txtPassword.Password
                : txtPasswordPlain.Text;

            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            {
                MessageBox.Show("Ingrese usuario y contraseña");
                return;
            }

            if (MulticajaRuntime.UseApiOnlyClient)
            {
                var rLogin = await MulticajaOperacionesClient.LoginAsync(username, password).ConfigureAwait(true);
                if (rLogin == null || !rLogin.Ok)
                {
                    MessageBox.Show(rLogin?.Error ?? "No se pudo validar con el servidor.");
                    return;
                }

                if (!UsuarioRolPermisos.PuedeOperarPos(rLogin.Rol))
                {
                    MessageBox.Show(
                        "Su usuario no tiene permisos para operar el POS. Contacte al administrador.",
                        UsuarioPermisosGate.Titulo,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                App.UsuarioActual = new Usuario
                {
                    Id = rLogin.Id,
                    Username = rLogin.Username,
                    Nombre = rLogin.Nombre,
                    Rol = rLogin.Rol
                };

                var necesitaSync = App.LastMulticajaCatalogSyncUtc == DateTime.MinValue
                    || DateTime.UtcNow - App.LastMulticajaCatalogSyncUtc > TimeSpan.FromMinutes(10);
                if (necesitaSync)
                {
                    var sync = App.MulticajaBackground != null
                        ? await App.MulticajaBackground.Sync.SyncNowAsync(
                            GrunflexPOS2.Services.Multicaja.Sync.SyncReason.Login).ConfigureAwait(true)
                        : await MulticajaShadowCatalogSync.PullTodoAsync().ConfigureAwait(true);
                    if (sync.Ok)
                        App.LastMulticajaCatalogSyncUtc = DateTime.UtcNow;
                }

                var cfg = AppConfig.Cargar();
                Guid cajaId;
                if (!Guid.TryParse(cfg.CajaId, out cajaId) || cajaId == Guid.Empty)
                {
                    var reg = await MulticajaOperacionesClient.AutoRegistroCajaAsync(Environment.MachineName)
                        .ConfigureAwait(true);
                    if (reg == null || !reg.Ok)
                    {
                        MessageBox.Show(reg?.Error ?? "No se pudo registrar la caja en el servidor.");
                        return;
                    }

                    cajaId = reg.CajaId;
                    cfg.CajaId = cajaId.ToString();
                    App.PersistirCajaTerminalId(cajaId);
                }

                if (!await MulticajaOperacionesClient.CajaExisteAsync(cajaId).ConfigureAwait(true))
                {
                    MessageBox.Show("La caja configurada no existe en el servidor.");
                    return;
                }

                await MulticajaOperacionesClient.VincularEquipoCajaAsync(cajaId, Environment.MachineName)
                    .ConfigureAwait(true);

                var nombreCaja = $"Caja terminal {Environment.MachineName}";
                if (!await App.DbContext.Cajas.AnyAsync(c => c.Id == cajaId).ConfigureAwait(true))
                {
                    var emp = await App.DbContext.Empresas.OrderBy(e => e.FechaCreacion).FirstOrDefaultAsync()
                        .ConfigureAwait(true);
                    if (emp == null)
                    {
                        MessageBox.Show(
                            "La base de datos local de esta caja adicional no está inicializada (falta empresa). " +
                            "Cierre el POS y vuelva a abrirlo; si persiste, reinstale o borre la BD sombra según soporte.");
                        return;
                    }

                    App.DbContext.Cajas.Add(new Caja
                    {
                        Id = cajaId,
                        Nombre = nombreCaja,
                        EmpresaId = emp.Id,
                        Activa = true,
                        FechaCreacion = DateTime.UtcNow
                    });
                    await App.DbContext.SaveChangesAsync().ConfigureAwait(true);
                }

                var sesDto = await MulticajaOperacionesClient.ObtenerSesionAbiertaAsync(cajaId).ConfigureAwait(true)
                             ?? await MulticajaOperacionesClient.AbrirSesionAsync(cajaId, App.UsuarioActual!.Id,
                                 App.UsuarioActual.Username, 0).ConfigureAwait(true);
                if (sesDto == null)
                {
                    MessageBox.Show("No se pudo abrir sesión de caja en el servidor.");
                    return;
                }

                App.MulticajaSesionEnServidor = new CajaSesion
                {
                    Id = sesDto.Id,
                    CajaId = sesDto.CajaId,
                    NumeroCaja = sesDto.NumeroCaja,
                    Cajero = sesDto.Cajero,
                    UsuarioAperturaId = sesDto.UsuarioAperturaId,
                    FechaApertura = sesDto.FechaApertura,
                    MontoApertura = sesDto.MontoApertura,
                    Abierta = sesDto.Abierta,
                    TotalVentas = sesDto.TotalVentas,
                    TotalIngresos = sesDto.TotalIngresos,
                    TotalRetiros = sesDto.TotalRetiros,
                    Diferencia = 0
                };

                App.CajaActualId = cajaId;

                var view = new CajaView();
                System.Windows.Application.Current.MainWindow = view;
                view.Show();
                Close();
                return;
            }

            var usuario = await _usuarioService.Login(username, password);

            if (usuario != null)
            {
                if (!UsuarioRolPermisos.PuedeOperarPos(usuario.Rol))
                {
                    MessageBox.Show(
                        "Su usuario no tiene permisos para operar el POS. Contacte al administrador.",
                        UsuarioPermisosGate.Titulo,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                // 🔥 Guardar usuario global
                App.UsuarioActual = usuario;

                // Selecciona la caja asignada al terminal (CajaId en appsettings.local.json).
                var cfg = AppConfig.Cargar();
                Caja? caja = null;
                if (Guid.TryParse(cfg.CajaId, out var cajaAsignadaId))
                    caja = App.DbContext.Cajas.FirstOrDefault(c => c.Id == cajaAsignadaId);

                caja ??= App.DbContext.Cajas.FirstOrDefault();

                if (caja == null)
                {
                    MessageBox.Show("No existe caja configurada");
                    return;
                }

                // 🔥 SERVICIO DE CAJA
                var cajaService = new CajaService(App.DbContext);

                // 🔥 VER SI YA HAY SESIÓN ABIERTA
                var sesion = cajaService.ObtenerSesionAbierta(caja.Id);

                if (sesion == null)
                {
                    // 🔥 ABRIR AUTOMÁTICAMENTE
                    sesion = cajaService.AbrirCaja(caja.Id, 0);
                }

                // 🔥 GUARDAR CAJA ACTUAL
                App.CajaActualId = caja.Id;

                // 🔥 ABRIR POS REAL
                var view = new CajaView();
                System.Windows.Application.Current.MainWindow = view;
                view.Show();

                // 🔥 cerrar login
                this.Close();
            }
            else
            {
                MessageBox.Show("Credenciales incorrectas");
            }
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            await IntentarLoginAsync();
        }

        private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key is not Key.Enter and not Key.Return)
                return;

            e.Handled = true;
            await IntentarLoginAsync();
        }
    }
}