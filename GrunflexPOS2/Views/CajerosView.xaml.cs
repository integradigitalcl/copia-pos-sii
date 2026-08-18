using System;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using Microsoft.EntityFrameworkCore;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Multicaja;
using Grunflex.Licensing;
using Grunflex.Licensing.Security;

namespace GrunflexPOS2.Views
{
    public partial class CajerosView : UserControl
    {
        private Usuario? _usuarioSeleccionado;
        private List<Usuario> _usuarios = new();

        public CajerosView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                AplicarPermisosUi();
                if (MulticajaRuntime.UseApiOnlyClient)
                    CargarUsuarios();
            };
            CargarUsuarios();
            LimpiarFormulario();
        }

        private void AplicarPermisosUi()
        {
            var permitido = UsuarioPermisos.PuedeAdministrarUsuarios();
            IsEnabled = permitido;
            if (!permitido)
            {
                System.Windows.MessageBox.Show(
                    "Solo un administrador puede administrar usuarios y cajeros.",
                    UsuarioPermisosGate.Titulo,
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }

        private async void CargarUsuarios()
        {
            if (App.DbContext == null)
                return;

            try
            {
                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    var remoto = await MulticajaOperacionesClient.ListarUsuariosAsync().ConfigureAwait(true);
                    if (remoto == null)
                    {
                        System.Windows.MessageBox.Show(
                            "No se pudo cargar la lista de cajeros desde el servidor. Compruebe red y API.",
                            "Grunflex POS",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    _usuarios = remoto.Select(d => new Usuario
                    {
                        Id = d.Id,
                        Username = d.Username,
                        Nombre = d.Nombre,
                        Rol = d.Rol
                    }).ToList();
                }
                else
                {
                    _usuarios = App.DbContext.Usuarios
                        .OrderBy(u => u.Username)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("CajerosView.CargarUsuarios", ex);
                System.Windows.MessageBox.Show("Error al cargar cajeros:\n" + ex.Message);
                return;
            }

            AplicarFiltro();
        }

        private void AplicarFiltro()
        {
            string q = (TxtBuscar.Text ?? string.Empty).Trim();
            var lista = string.IsNullOrWhiteSpace(q)
                ? _usuarios
                : _usuarios.Where(u =>
                        (u.Username ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (u.Nombre ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            ListaCajeros.ItemsSource = lista;
        }

        private void TxtBuscar_TextChanged(object sender, TextChangedEventArgs e) => AplicarFiltro();

        private void ListaCajeros_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ListaCajeros.SelectedItem is not Usuario u)
                return;

            _usuarioSeleccionado = u;
            TxtUsuario.Text = u.Username ?? string.Empty;
            TxtNombre.Text = u.Nombre ?? string.Empty;
            TxtPassword.Password = string.Empty;
            CargarPermisosDesdeRol(u.Rol ?? string.Empty);
        }

        private async void BtnGuardar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureUsuarios())
                return;

            Guid idGuardado = Guid.Empty;
            bool esNuevo = (_usuarioSeleccionado == null);

            try
            {
                if (App.DbContext == null)
                {
                    System.Windows.MessageBox.Show(
                        "El POS no tiene una base de datos abierta. Reinicia el POS.",
                        "Grunflex POS",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }

                string username = (TxtUsuario.Text ?? string.Empty).Trim();
                string nombre = (TxtNombre.Text ?? string.Empty).Trim();
                string password = (TxtPassword.Password ?? string.Empty).Trim();

                if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(nombre))
                {
                    System.Windows.MessageBox.Show("Debe ingresar usuario y nombre.");
                    return;
                }

                if (esNuevo && string.IsNullOrWhiteSpace(password))
                {
                    System.Windows.MessageBox.Show("Debe ingresar contraseña.");
                    return;
                }

                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    var req = new MulticajaUsuarioUpsertRequest
                    {
                        Username = username,
                        Nombre = nombre,
                        Password = password,
                        Rol = ConstruirRolDesdePermisos()
                    };

                    MulticajaUsuarioSyncDto? guardado;
                    if (esNuevo)
                        guardado = await MulticajaOperacionesClient.CrearUsuarioAsync(req).ConfigureAwait(true);
                    else
                        guardado = await MulticajaOperacionesClient
                            .ActualizarUsuarioAsync(_usuarioSeleccionado!.Id, req).ConfigureAwait(true);

                    if (guardado == null)
                    {
                        System.Windows.MessageBox.Show(
                            "No se pudo guardar el cajero en el servidor (revise duplicados o permisos).",
                            "Grunflex POS",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    await MulticajaShadowCatalogSync.PullUsuariosAsync().ConfigureAwait(true);
                    CargarUsuarios();
                    LimpiarFormulario();
                    System.Windows.MessageBox.Show(
                        $"Usuario '{username}' guardado en el servidor y copiado a esta terminal.",
                        "Grunflex POS",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                    return;
                }

                // OJO con el SQLite: la comparación por defecto es BINARY (case-sensitive).
                // Forzamos NOCASE para que "Pedro" y "pedro" se consideren duplicados — sino
                // el operador crea sin darse cuenta dos cajeros que después chocan en login.
                var existeMismoUsername = App.DbContext.Usuarios
                    .FirstOrDefault(x => EF.Functions.Like(x.Username, username));

                if (esNuevo)
                {
                    if (existeMismoUsername != null)
                    {
                        System.Windows.MessageBox.Show(
                            $"Ya existe un usuario con username '{existeMismoUsername.Username}'.\n" +
                            "Elija otro nombre.",
                            "Usuario duplicado",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    var nuevo = new Usuario
                    {
                        Id = Guid.NewGuid(),
                        Username = username,
                        Nombre = nombre,
                        Password = PasswordHasher.Hash(password),
                        Rol = ConstruirRolDesdePermisos()
                    };

                    App.DbContext.Usuarios.Add(nuevo);
                    idGuardado = nuevo.Id;
                }
                else
                {
                    var edit = App.DbContext.Usuarios.First(x => x.Id == _usuarioSeleccionado!.Id);
                    if (existeMismoUsername != null && existeMismoUsername.Id != edit.Id)
                    {
                        System.Windows.MessageBox.Show(
                            $"Ya existe un usuario con username '{existeMismoUsername.Username}'.",
                            "Usuario duplicado",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    edit.Username = username;
                    edit.Nombre = nombre;
                    if (!string.IsNullOrWhiteSpace(password))
                        edit.Password = PasswordHasher.Hash(password);
                    edit.Rol = ConstruirRolDesdePermisos();
                    idGuardado = edit.Id;
                }

                int filasAfectadas = App.DbContext.SaveChanges();
                PosDiagnostics.Log($"CajerosView: SaveChanges devolvió {filasAfectadas} fila(s) afectada(s) para '{username}' (Id={idGuardado}, esNuevo={esNuevo}).");

                // Verificación dura: re-leemos con un contexto FRESCO conectado a la misma
                // cadena. Si no aparece, es señal inequívoca de que el SaveChanges no llegó
                // al archivo físico (DB readonly, transacción rollback silencioso, lock SMB, etc).
                bool verificadoEnDisco = VerificarUsuarioEnDisco(idGuardado, out string rutaBd, out string motivoFallo);

                CargarUsuarios();
                LimpiarFormulario();

                if (verificadoEnDisco)
                {
                    System.Windows.MessageBox.Show(
                        $"Usuario '{username}' guardado correctamente.\n\nBD: {rutaBd}",
                        "Grunflex POS",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                else
                {
                    PosDiagnostics.Log(
                        $"CajerosView: ALERTA - SaveChanges no se reflejó en disco. " +
                        $"Username='{username}', Id={idGuardado}, BD='{rutaBd}', motivo='{motivoFallo}'");
                    System.Windows.MessageBox.Show(
                        "El POS dijo que guardó el usuario pero no aparece en la base al releerla.\n\n" +
                        $"BD: {rutaBd}\n" +
                        $"Detalle: {motivoFallo}\n\n" +
                        "Causa típica: la BD está abierta sólo-lectura (permisos NTFS), " +
                        "otro proceso la tiene en uso exclusivo, o la conexión apunta a un " +
                        "archivo distinto del que la API/multicaja usan.\n\n" +
                        "Ver detalles en %LocalAppData%\\GrunflexPOS\\logs\\pos-*.log",
                        "Grunflex POS - guardado no persistido",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("CajerosView: excepción guardando usuario", ex);

                var sb = new StringBuilder();
                sb.AppendLine("No se pudo guardar el usuario.");
                sb.AppendLine();
                sb.AppendLine("Detalle:");
                sb.AppendLine(ex.GetType().FullName + ": " + ex.Message);
                var inner = ex.InnerException;
                int nivel = 1;
                while (inner != null)
                {
                    sb.AppendLine($"  -> ({nivel}) " + inner.GetType().FullName + ": " + inner.Message);
                    inner = inner.InnerException;
                    nivel++;
                }
                sb.AppendLine();
                sb.AppendLine("BD activa: " + DescribirBdActiva());
                sb.AppendLine();
                sb.AppendLine("Ver pos-*.log en %LocalAppData%\\GrunflexPOS\\logs");

                System.Windows.MessageBox.Show(
                    sb.ToString(),
                    "Grunflex POS - error guardando",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Abre un DbContext fresco contra la misma cadena de conexión del POS y verifica
        /// que el usuario realmente exista en disco. Esto neutraliza falsos positivos del
        /// change tracker de EF Core (que devuelve la entidad de su caché en memoria
        /// aunque el INSERT no haya llegado al archivo).
        /// </summary>
        private bool VerificarUsuarioEnDisco(Guid id, out string rutaBd, out string motivo)
        {
            rutaBd = "(desconocida)";
            motivo = string.Empty;

            try
            {
                var cs = AppConfig.Cargar().ConnectionString;
                rutaBd = ExtraerDataSource(cs);

                using var verify = new GrunflexDbContext(
                    new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<GrunflexDbContext>()
                        .UseSqlite(cs)
                        .Options);

                return verify.Usuarios.AsNoTracking().Any(u => u.Id == id);
            }
            catch (Exception ex)
            {
                motivo = ex.GetType().Name + ": " + ex.Message;
                PosDiagnostics.Log("CajerosView.VerificarUsuarioEnDisco falló", ex);
                return false;
            }
        }

        private string DescribirBdActiva()
        {
            try
            {
                var cs = AppConfig.Cargar().ConnectionString;
                return ExtraerDataSource(cs);
            }
            catch
            {
                return "(no se pudo determinar)";
            }
        }

        private static string ExtraerDataSource(string cs)
        {
            if (string.IsNullOrWhiteSpace(cs)) return "(vacía)";
            foreach (var parte in cs.Split(';'))
            {
                var t = parte.Trim();
                if (t.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("Data Source=".Length).Trim();
                if (t.StartsWith("Filename=", StringComparison.OrdinalIgnoreCase))
                    return t.Substring("Filename=".Length).Trim();
            }
            return cs;
        }

        private void BtnCancelar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            LimpiarFormulario();
        }

        private async void BtnEliminar_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureUsuarios())
                return;

            try
            {
                if (_usuarioSeleccionado == null || App.DbContext == null)
                    return;

                var resp = System.Windows.MessageBox.Show(
                    $"¿Eliminar usuario '{_usuarioSeleccionado.Username}'?",
                    "Confirmar",
                    System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Question);

                if (resp != System.Windows.MessageBoxResult.Yes)
                    return;

                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    var err = await MulticajaOperacionesClient.EliminarUsuarioAsync(_usuarioSeleccionado.Id)
                        .ConfigureAwait(true);
                    if (err != null)
                    {
                        System.Windows.MessageBox.Show(err, "No se pudo eliminar", System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }

                    await MulticajaShadowCatalogSync.PullUsuariosAsync().ConfigureAwait(true);
                    CargarUsuarios();
                    LimpiarFormulario();
                    return;
                }

                var edit = App.DbContext.Usuarios.First(x => x.Id == _usuarioSeleccionado.Id);
                App.DbContext.Usuarios.Remove(edit);
                App.DbContext.SaveChanges();

                CargarUsuarios();
                LimpiarFormulario();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("No se pudo eliminar el usuario:\n" + ex.Message);
            }
        }

        private void LimpiarFormulario()
        {
            _usuarioSeleccionado = null;
            ListaCajeros.SelectedItem = null;
            TxtUsuario.Text = string.Empty;
            TxtNombre.Text = string.Empty;
            TxtPassword.Password = string.Empty;

            ChkVentas.IsChecked = false;
            ChkClientes.IsChecked = false;
            ChkProductos.IsChecked = false;
            ChkInventario.IsChecked = false;
            ChkCancelarTickets.IsChecked = false;
            ChkDescuentos.IsChecked = false;
            ChkVerHistorial.IsChecked = false;
            ChkCobrar.IsChecked = false;
            ChkFacturar.IsChecked = false;
        }

        private string ConstruirRolDesdePermisos()
        {
            var permisos = new List<string>();
            if (ChkVentas.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Ventas);
            if (ChkClientes.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Clientes);
            if (ChkProductos.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Productos);
            if (ChkInventario.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Inventario);
            if (ChkCancelarTickets.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.CancelarTickets);
            if (ChkDescuentos.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Descuentos);
            if (ChkVerHistorial.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.VerHistorial);
            if (ChkCobrar.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Cobrar);
            if (ChkFacturar.IsChecked == true) permisos.Add(UsuarioRolPermisos.Codigo.Facturar);
            return UsuarioRolPermisos.ConstruirRol(permisos);
        }

        private void CargarPermisosDesdeRol(string rol)
        {
            var permisos = UsuarioRolPermisos.Parse(rol);

            ChkVentas.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Ventas);
            ChkClientes.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Clientes);
            ChkProductos.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Productos);
            ChkInventario.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Inventario);
            ChkCancelarTickets.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.CancelarTickets);
            ChkDescuentos.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Descuentos);
            ChkVerHistorial.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.VerHistorial);
            ChkCobrar.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Cobrar);
            ChkFacturar.IsChecked = permisos.Contains(UsuarioRolPermisos.Codigo.Facturar);
        }
    }
}