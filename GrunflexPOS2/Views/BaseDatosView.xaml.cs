using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
using Grunflex.Licensing.Security;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services.Backups;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;

namespace GrunflexPOS2.Views
{
    public partial class BaseDatosView : UserControl
    {
        public BaseDatosView()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                if (!UsuarioPermisos.PuedeAdministrarBaseDatos())
                {
                    IsEnabled = false;
                    MessageBox.Show(
                        "Solo un administrador puede acceder a herramientas de base de datos.",
                        UsuarioPermisosGate.Titulo,
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                AplicarPermisosRestauracion();
                RefrescarSnapshots();
            };
        }

        private void AplicarPermisosRestauracion()
        {
            var puedeRestaurar = UsuarioPermisos.PuedeRestaurarRespaldos();
            BtnRestaurarSnapshot.IsEnabled = puedeRestaurar;
            BtnRestaurarBaseDatos.IsEnabled = puedeRestaurar;
            BtnReinicializarBaseDatos.IsEnabled = puedeRestaurar;
        }

        private void RefrescarSnapshots()
        {
            try
            {
                ListaSnapshots.ItemsSource = App.Backups?.ListBackups() ?? new System.Collections.Generic.List<BackupInfo>();
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo listar snapshots: " + ex.Message);
            }
        }

        private void BtnCrearSnapshot_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var info = App.Backups?.CreateManualBackup();
                if (info != null)
                    MessageBox.Show($"Snapshot creado:\n\n{info.FileName}\nTamaño: {info.SizeDisplay}\nSHA-256: {info.Sha256Hex}",
                        "Snapshot OK", MessageBoxButton.OK, MessageBoxImage.Information);
                RefrescarSnapshots();
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo crear el snapshot:\n" + ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BtnRefrescarSnapshots_Click(object sender, RoutedEventArgs e) => RefrescarSnapshots();

        private void BtnAbrirCarpetaBackups_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var dir = BackupService.BackupsDirectory;
                Directory.CreateDirectory(dir);
                Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
            }
            catch (Exception ex) { MessageBox.Show("No se pudo abrir carpeta: " + ex.Message); }
        }

        private void BtnVerificarSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (ListaSnapshots.SelectedItem is not BackupInfo b) { MessageBox.Show("Seleccione un snapshot."); return; }
            var r = App.Backups?.VerifyBackup(b.FilePath) ?? (false, "Servicio no disponible.");
            MessageBox.Show(r.ok ? "OK — " + r.detail : "FALLO — " + r.detail,
                "Verificación de integridad",
                MessageBoxButton.OK,
                r.ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void BtnRestaurarSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureRestaurarRespaldos())
                return;

            if (ListaSnapshots.SelectedItem is not BackupInfo b) { MessageBox.Show("Seleccione un snapshot."); return; }
            var confirm = MessageBox.Show(
                $"Se reemplazará la base de datos actual con:\n\n{b.FileName}\n({b.CreatedDisplay}, {b.SizeDisplay})\n\n" +
                "Se creará un respaldo previo automáticamente antes de restaurar.\n" +
                "Tras la restauración, deberá REINICIAR el POS.\n\n¿Continuar?",
                "Confirmar restauración",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            var r = App.Backups?.RestoreBackup(b.FilePath) ?? (false, "Servicio no disponible.");
            MessageBox.Show(r.ok ? r.detail : "Error: " + r.detail,
                "Restauración",
                MessageBoxButton.OK,
                r.ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            if (r.ok)
            {
                System.Windows.Application.Current.Shutdown();
            }
        }

        private void BtnEliminarSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (ListaSnapshots.SelectedItem is not BackupInfo b) { MessageBox.Show("Seleccione un snapshot."); return; }
            if (MessageBox.Show($"¿Eliminar el snapshot {b.FileName}?", "Confirmar",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { File.Delete(b.FilePath); RefrescarSnapshots(); }
            catch (Exception ex) { MessageBox.Show("No se pudo borrar: " + ex.Message); }
        }

        private void BtnExportarBaseDatos_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.DbContext == null)
                    return;

                var dlg = new SaveFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    FileName = $"inventario_export_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                    Title = "Exportar inventario"
                };
                if (dlg.ShowDialog() != true)
                    return;

                var lista = App.DbContext.Productos
                    .AsNoTracking()
                    .OrderBy(p => p.Id)
                    .ToList();

                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("Inventario");
                string[] headers =
                {
                    "ID", "Código", "Producto", "P. Costo", "P. Venta", "P. Mayoreo",
                    "Departamento", "Existencia", "Inv. Mínimo", "Inv. Máximo", "Tipo de venta"
                };
                for (int i = 0; i < headers.Length; i++)
                    ws.Cell(1, i + 1).Value = headers[i];

                for (int i = 0; i < lista.Count; i++)
                {
                    var p = lista[i];
                    int r = i + 2;
                    ws.Cell(r, 1).Value = p.Id;
                    ws.Cell(r, 2).Value = p.CodigoBarras ?? string.Empty;
                    ws.Cell(r, 3).Value = p.Nombre ?? string.Empty;
                    ws.Cell(r, 4).Value = p.Costo;
                    ws.Cell(r, 5).Value = p.Precio;
                    ws.Cell(r, 6).Value = p.PrecioMayoreo;
                    ws.Cell(r, 7).Value = p.Departamento ?? string.Empty;
                    ws.Cell(r, 8).Value = p.Stock;
                    ws.Cell(r, 9).Value = p.InvMinimo;
                    ws.Cell(r, 10).Value = p.InvMaximo;
                    ws.Cell(r, 11).Value = p.TipoVenta ?? string.Empty;
                }

                ws.Columns().AdjustToContents();
                wb.SaveAs(dlg.FileName);

                MessageBox.Show("Inventario exportado correctamente.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo exportar inventario:\n" + ex.Message);
            }
        }

        private void BtnVerificarBaseDatos_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.DbContext == null)
                    return;

                bool ok = App.DbContext.Database.CanConnect();
                if (!ok)
                {
                    MessageBox.Show("No se pudo conectar a la base de datos.");
                    return;
                }

                int usuarios = App.DbContext.Usuarios.Count();
                int productos = App.DbContext.Productos.Count();
                int ventas = App.DbContext.Ventas.Count();
                int movs = App.DbContext.MovimientosCaja.Count();

                MessageBox.Show(
                    $"Conexión OK\n\nUsuarios: {usuarios}\nProductos: {productos}\nVentas: {ventas}\nMovimientos: {movs}",
                    "Verificación",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error al verificar base de datos:\n" + ex.Message);
            }
        }

        private void BtnRespaldarBaseDatos_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (App.DbContext == null)
                    return;

                var dlg = new SaveFileDialog
                {
                    Filter = "Respaldo JSON (*.json)|*.json",
                    FileName = $"respaldo_bd_{DateTime.Now:yyyyMMdd_HHmm}.json",
                    Title = "Respaldar base de datos"
                };
                if (dlg.ShowDialog() != true)
                    return;

                var payload = new RespaldoPayload
                {
                    Fecha = DateTime.UtcNow,
                    Usuarios = App.DbContext.Usuarios.AsNoTracking().ToList(),
                    Categorias = App.DbContext.Categorias.AsNoTracking().ToList(),
                    Productos = App.DbContext.Productos.AsNoTracking().ToList(),
                    Configuraciones = App.DbContext.Configuraciones.AsNoTracking().ToList()
                };

                var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                File.WriteAllText(dlg.FileName, json);
                MessageBox.Show("Respaldo generado correctamente.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo generar respaldo:\n" + ex.Message);
            }
        }

        private void BtnRestaurarBaseDatos_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureRestaurarRespaldos())
                return;

            try
            {
                if (App.DbContext == null)
                    return;

                var dlg = new OpenFileDialog
                {
                    Filter = "Respaldo JSON (*.json)|*.json",
                    Title = "Restaurar base de datos"
                };
                if (dlg.ShowDialog() != true)
                    return;

                var confirm = MessageBox.Show(
                    "Se reemplazarán usuarios, categorías, productos y configuraciones.\n\n¿Desea continuar?",
                    "Confirmar restauración",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;

                var json = File.ReadAllText(dlg.FileName);
                var payload = JsonSerializer.Deserialize<RespaldoPayload>(json);
                if (payload == null)
                    throw new Exception("Archivo de respaldo inválido.");

                App.DbContext.Usuarios.RemoveRange(App.DbContext.Usuarios);
                App.DbContext.Productos.RemoveRange(App.DbContext.Productos);
                App.DbContext.Categorias.RemoveRange(App.DbContext.Categorias);
                App.DbContext.Configuraciones.RemoveRange(App.DbContext.Configuraciones);
                App.DbContext.SaveChanges();

                if (payload.Usuarios != null && payload.Usuarios.Count > 0)
                    App.DbContext.Usuarios.AddRange(payload.Usuarios);
                if (payload.Categorias != null && payload.Categorias.Count > 0)
                    App.DbContext.Categorias.AddRange(payload.Categorias);
                if (payload.Productos != null && payload.Productos.Count > 0)
                    App.DbContext.Productos.AddRange(payload.Productos);
                if (payload.Configuraciones != null && payload.Configuraciones.Count > 0)
                    App.DbContext.Configuraciones.AddRange(payload.Configuraciones);

                App.DbContext.SaveChanges();
                MessageBox.Show("Restauración completada.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo restaurar:\n" + ex.Message);
            }
        }

        private void BtnReinicializarBaseDatos_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureRestaurarRespaldos())
                return;

            try
            {
                if (App.DbContext == null)
                    return;

                var confirm = MessageBox.Show(
                    "Esto eliminará productos, categorías, usuarios (excepto admin) y configuraciones.\n¿Continuar?",
                    "Reinicializar base de datos",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);

                if (confirm != MessageBoxResult.Yes)
                    return;

                var admin = App.DbContext.Usuarios
                    .AsNoTracking()
                    .FirstOrDefault(u => u.Username == "admin");

                App.DbContext.Productos.RemoveRange(App.DbContext.Productos);
                App.DbContext.Categorias.RemoveRange(App.DbContext.Categorias);
                App.DbContext.Configuraciones.RemoveRange(App.DbContext.Configuraciones);
                App.DbContext.Usuarios.RemoveRange(App.DbContext.Usuarios.Where(u => u.Username != "admin"));
                App.DbContext.SaveChanges();

                if (admin == null)
                {
                    App.DbContext.Usuarios.Add(new Usuario
                    {
                        Id = Guid.NewGuid(),
                        Username = "admin",
                        Password = PasswordHasher.Hash("1234"),
                        Nombre = "Administrador",
                        Rol = "Admin"
                    });
                    App.DbContext.SaveChanges();
                }

                MessageBox.Show("Base de datos reinicializada.");
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo reinicializar:\n" + ex.Message);
            }
        }

        private sealed class RespaldoPayload
        {
            public DateTime Fecha { get; set; }
            public System.Collections.Generic.List<Usuario> Usuarios { get; set; } = new();
            public System.Collections.Generic.List<Categoria> Categorias { get; set; } = new();
            public System.Collections.Generic.List<Producto> Productos { get; set; } = new();
            public System.Collections.Generic.List<Configuracion> Configuraciones { get; set; } = new();
        }
    }
}