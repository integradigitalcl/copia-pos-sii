using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Multicaja;
using GrunflexPOS2.Services.Multicaja.Sync;
using GrunflexPOS2.Services.Multicaja.UI;
using System.Threading;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GrunflexPOS2.Views
{
    public partial class InventarioView : UserControl
    {
        private enum InventarioModo
        {
            Agregar,
            Ajustes,
            ProductosBajos,
            Reporte,
            Movimientos,
            Kardex
        }

        private sealed class MovimientoInventarioView
        {
            public DateTime Fecha { get; init; }
            public string Producto { get; init; } = "";
            public string Tipo { get; init; } = "";
            public int Cantidad { get; init; }
            public string Cajero { get; init; } = "";
            public string Referencia { get; init; } = "";
        }

        private InventarioModo _modoActual = InventarioModo.Agregar;
        private Producto? productoActual;
        private Window? _ventanaHost;
        private bool _previewF10Registrado;
        private TextBox? _txtBuscarCodigoNombre;
        private bool _actualizandoSugerencias;
        private List<Producto> _productosCache = new();
        private List<Producto> _listaFiltradaActual = new();
        private int _pageSize = 20;
        private int _currentPage = 1;
        private bool _suspendPaginationUi;
        private ReactiveViewRefresh? _reactiveRefresh;

        private static readonly Brush PlaceholderNombreBrush = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        private static readonly Brush NombreSeleccionadoBrush = new SolidColorBrush(Color.FromRgb(17, 24, 39));

        private sealed class SugerenciaProducto
        {
            public required string Codigo { get; init; }
            public required string Nombre { get; init; }
            public string Texto => $"{Codigo} - {Nombre}";
        }

        public InventarioView()
        {
            try
            {
                InitializeComponent();

                // 🔥 CONTEXTO
                SoporteContexto.Modulo = "Inventario";
                SoporteContexto.Accion = "Inicio";

                // 🔥 VALIDACIÓN CRÍTICA
                if (App.DbContext == null)
                {
                    SoporteContexto.Error = "DbContext no inicializado";
                    MessageBox.Show("DbContext no está inicializado");
                }
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error al cargar Inventario:\n" + ex.Message);
            }
        }

        private void InventarioView_Loaded(object sender, RoutedEventArgs e)
        {
            var win = Window.GetWindow(this);
            if (win != null && !_previewF10Registrado)
            {
                _ventanaHost = win;
                _previewF10Registrado = true;
                win.PreviewKeyDown += VentanaHost_PreviewKeyDown_F10;
            }

            Unloaded -= InventarioView_Unloaded;
            Unloaded += InventarioView_Unloaded;

            if (MulticajaRuntime.UseApiOnlyClient && App.MulticajaRealtime != null)
            {
                _reactiveRefresh = ReactiveViewRefresh.ForInventory(
                    this,
                    RefrescarGridAsync,
                    App.MulticajaRealtime.Caches,
                    App.MulticajaRealtime.EventBus);
            }

            _suspendPaginationUi = true;
            try
            {
                ConfigurarAutoCompletarCodigoNombre();
                ActivarModo(InventarioModo.Agregar);
            }
            finally
            {
                _suspendPaginationUi = false;
            }

            if (MulticajaRuntime.UseApiOnlyClient)
                _ = SincronizarInventarioDesdeServidorAsync();

            AjustarAlturaGridInventario();
            AplicarPermisosInventario();
        }

        private void AplicarPermisosInventario()
        {
            var puedeAjustar = UsuarioPermisos.PuedeAjustarInventario();
            BtnGuardar.IsEnabled = puedeAjustar;
            if (FindName("BtnImportarExcel") is Button btnImportar)
                btnImportar.IsEnabled = puedeAjustar;
        }

        private void InventarioView_SizeChanged(object sender, SizeChangedEventArgs e) =>
            AjustarAlturaGridInventario();

        private void AjustarAlturaGridInventario()
        {
            if (GridInventario == null)
                return;
            PosGridHeightHelper.AjustarAlturaGrid(this, GridInventario, reservarSuperior: 320, minAltura: 180, maxFraccionViewport: 0.38);
        }

        private async Task SincronizarInventarioDesdeServidorAsync()
        {
            try
            {
                if (App.MulticajaRealtime != null)
                    await App.MulticajaRealtime.Coordinator.FlushPendingBatchesAsync().ConfigureAwait(true);

                await RefrescarGridAsync(CancellationToken.None).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("InventarioView.SincronizarInventarioDesdeServidorAsync", ex);
            }
        }

        private void InventarioView_Unloaded(object sender, RoutedEventArgs e)
        {
            _reactiveRefresh?.Dispose();
            _reactiveRefresh = null;

            if (_ventanaHost != null && _previewF10Registrado)
            {
                _ventanaHost.PreviewKeyDown -= VentanaHost_PreviewKeyDown_F10;
                _ventanaHost = null;
                _previewF10Registrado = false;
            }
        }

        /// <summary>F10 a veces llega como SystemKey; además bloquea el menú de la ventana.</summary>
        private void VentanaHost_PreviewKeyDown_F10(object sender, KeyEventArgs e)
        {
            if (!EsTeclaF10(e) || !IsKeyboardFocusWithin)
                return;
            e.Handled = true;
            Dispatcher.BeginInvoke(FocusCajaBusqueda, DispatcherPriority.Input);
        }

        private void InventarioView_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!EsTeclaF10(e))
                return;
            e.Handled = true;
            Dispatcher.BeginInvoke(FocusCajaBusqueda, DispatcherPriority.Input);
        }

        private static bool EsTeclaF10(KeyEventArgs e) =>
            e.Key == Key.F10 || (e.Key == Key.System && e.SystemKey == Key.F10);

        private void FocusCajaBusqueda()
        {
            TxtBuscarNombre.Focus();
            TxtBuscarNombre.SelectAll();
        }

        private void TxtBuscarNombre_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _currentPage = 1;
                RefrescarGrid();
            }
        }

        private void TxtBuscarNombre_TextChanged(object sender, TextChangedEventArgs e)
        {
            _currentPage = 1;
            RefrescarGrid();
        }

        private void RefrescarGrid() => _ = RefrescarGridAsync(CancellationToken.None);

        private async Task RefrescarGridAsync(CancellationToken ct)
        {
            if (App.DbContext == null)
                return;
            try
            {
                ct.ThrowIfCancellationRequested();
                _productosCache = await App.DbContext.Productos
                    .AsNoTracking()
                    .OrderBy(p => p.Id)
                    .ToListAsync(ct)
                    .ConfigureAwait(true);

                ct.ThrowIfCancellationRequested();
                var lista = _productosCache.AsEnumerable();
                if (_modoActual == InventarioModo.ProductosBajos)
                    lista = lista.Where(p => p.InvMinimo > 0 && p.Stock <= p.InvMinimo);
                if (_modoActual == InventarioModo.Reporte && CmbDepartamentoReporte != null &&
                    CmbDepartamentoReporte.SelectedItem != null &&
                    !string.Equals(CmbDepartamentoReporte.SelectedItem.ToString(), "Todos", StringComparison.OrdinalIgnoreCase))
                {
                    string dep = CmbDepartamentoReporte.SelectedItem.ToString() ?? "";
                    lista = lista.Where(p => string.Equals(p.Departamento ?? "", dep, StringComparison.OrdinalIgnoreCase));
                }

                string? filtro = TxtBuscarNombre?.Text?.Trim();
                if (!string.IsNullOrEmpty(filtro))
                    lista = lista.Where(p => CoincideBusquedaListado(p, filtro));

                _listaFiltradaActual = lista.ToList();

                int total = _listaFiltradaActual.Count;
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
                if (_currentPage > totalPages)
                    _currentPage = totalPages;
                if (_currentPage < 1)
                    _currentPage = 1;

                int skip = (_currentPage - 1) * _pageSize;
                var pagina = _listaFiltradaActual.Skip(skip).Take(_pageSize).ToList();

                GridInventario.ItemsSource = pagina;
                GridInventario.UpdateLayout();

                if (LblCostoInventario != null)
                    LblCostoInventario.Text = _listaFiltradaActual.Sum(x => x.Costo * x.Stock).ToString("N0");
                if (LblCantidadProductosInv != null)
                    LblCantidadProductosInv.Text = _listaFiltradaActual.Count.ToString("N0");

                ActualizarPiePaginacion(total, totalPages);
            }
            catch (OperationCanceledException)
            {
                PosDiagnostics.Log("InventarioView.RefrescarGridAsync canceled");
            }
            catch
            {
                _listaFiltradaActual = new List<Producto>();
                GridInventario.ItemsSource = null;
                ActualizarPiePaginacion(0, 1);
            }
        }

        /// <summary>Recarga el listado desde la BD local (tras sincronizar catálogo desde el servidor).</summary>
        public void RefrescarDesdeBase()
        {
            RefrescarGrid();
        }

        private void ActualizarPiePaginacion(int total, int totalPages)
        {
            if (TxtRangoMostrado == null)
                return;

            if (total == 0)
            {
                TxtRangoMostrado.Text = "Mostrando 0 productos";
                ReconstruirBotonesPagina(1, 0);
                return;
            }

            int start = (_currentPage - 1) * _pageSize + 1;
            int end = Math.Min(_currentPage * _pageSize, total);
            TxtRangoMostrado.Text = $"Mostrando {start} a {end} de {total} productos";
            ReconstruirBotonesPagina(totalPages, total);
        }

        private void ReconstruirBotonesPagina(int totalPages, int totalItems)
        {
            if (PanelNumerosPagina == null)
                return;

            PanelNumerosPagina.Children.Clear();
            if (totalItems == 0 || totalPages <= 1)
                return;

            void AgregarBoton(int numeroPagina)
            {
                var btn = new Button
                {
                    Content = numeroPagina.ToString(CultureInfo.InvariantCulture),
                    Width = 34,
                    Height = 34,
                    Margin = new Thickness(3, 0, 3, 0),
                    Padding = new Thickness(0),
                    Cursor = Cursors.Hand,
                    Tag = numeroPagina
                };
                btn.Click += (_, _) =>
                {
                    _currentPage = numeroPagina;
                    RefrescarGrid();
                };

                if (numeroPagina == _currentPage)
                {
                    btn.Background = new SolidColorBrush(Color.FromRgb(29, 78, 216));
                    btn.Foreground = Brushes.White;
                    btn.BorderThickness = new Thickness(0);
                }
                else
                {
                    btn.Background = Brushes.White;
                    btn.Foreground = new SolidColorBrush(Color.FromRgb(17, 24, 39));
                    btn.BorderBrush = new SolidColorBrush(Color.FromRgb(229, 231, 235));
                    btn.BorderThickness = new Thickness(1);
                }

                PanelNumerosPagina.Children.Add(btn);
            }

            const int maxVentana = 7;
            if (totalPages <= maxVentana)
            {
                for (int p = 1; p <= totalPages; p++)
                    AgregarBoton(p);
                return;
            }

            int ventana = maxVentana - 2;
            int mitad = ventana / 2;
            int inicio = Math.Max(2, _currentPage - mitad);
            int fin = Math.Min(totalPages - 1, inicio + ventana - 1);
            if (fin - inicio < ventana - 1)
                inicio = Math.Max(2, fin - ventana + 1);

            AgregarBoton(1);
            if (inicio > 2)
                PanelNumerosPagina.Children.Add(new TextBlock { Text = "…", Margin = new Thickness(4, 8, 4, 0), Foreground = PlaceholderNombreBrush });

            for (int p = inicio; p <= fin; p++)
                AgregarBoton(p);

            if (fin < totalPages - 1)
                PanelNumerosPagina.Children.Add(new TextBlock { Text = "…", Margin = new Thickness(4, 8, 4, 0), Foreground = PlaceholderNombreBrush });

            AgregarBoton(totalPages);
        }

        private void BtnPagAnterior_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPage <= 1)
                return;
            _currentPage--;
            RefrescarGrid();
        }

        private void BtnPagSiguiente_Click(object sender, RoutedEventArgs e)
        {
            int total = _listaFiltradaActual.Count;
            int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)_pageSize));
            if (_currentPage >= totalPages)
                return;
            _currentPage++;
            RefrescarGrid();
        }

        private void CmbItemsPorPagina_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suspendPaginationUi || CmbItemsPorPagina?.SelectedItem is not ComboBoxItem item)
                return;

            if (item.Tag is string s && int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sz) && sz > 0)
            {
                _pageSize = sz;
                _currentPage = 1;
                RefrescarGrid();
            }
        }

        private void BtnFiltrosListado_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Los filtros avanzados estarán disponibles en una próxima versión.\nPor ahora use la búsqueda por nombre o código.",
                "Filtros",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnAccionesInventario_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Producto p })
                return;
            MessageBox.Show(
                $"Producto: {p.Nombre}\n\nLas acciones por fila (editar, historial, etc.) se integrarán después.",
                "Acciones",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private void BtnCantidadMenos_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCantidad.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                v = 0;
            TxtCantidad.Text = (v - 1).ToString(CultureInfo.InvariantCulture);
        }

        private void BtnCantidadMas_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(TxtCantidad.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                v = 0;
            TxtCantidad.Text = (v + 1).ToString(CultureInfo.InvariantCulture);
        }

        private void TxtCantidad_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is not TextBox tb)
                return;
            int start = tb.SelectionStart;
            int len = tb.SelectionLength;
            string proposed = tb.Text[..start] + e.Text + tb.Text[(start + len)..];
            if (string.IsNullOrEmpty(proposed) || proposed == "-")
                return;
            e.Handled = !Regex.IsMatch(proposed, @"^-?\d+$");
        }

        private void TxtCantidad_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Sin validación estricta: permite borrar y volver a escribir.
        }

        private void BtnExportarReporte_Click(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Accion = "Exportar PDF reporte inventario";
            ExportarProductosPdf(ObtenerProductosListadoActual(), "reporte_inventario");
        }

        private void TxtKardexProducto_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                BtnBuscarKardex_Click(sender, e);
        }

        /// <summary>Coincide si el texto aparece en el nombre o en el código (parcial, sin distinguir mayúsculas).</summary>
        private static bool CoincideBusquedaListado(Producto p, string filtro)
        {
            if (!string.IsNullOrEmpty(p.Nombre) &&
                p.Nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase))
                return true;
            var cod = (p.CodigoBarras ?? string.Empty).Trim();
            return cod.Length > 0 &&
                   cod.Contains(filtro, StringComparison.OrdinalIgnoreCase);
        }

        private void ConfigurarAutoCompletarCodigoNombre()
        {
            if (_txtBuscarCodigoNombre != null)
                return;

            if (CmbBuscarProducto.Template.FindName("PART_EditableTextBox", CmbBuscarProducto) is not TextBox txt)
                return;

            _txtBuscarCodigoNombre = txt;
            txt.TextChanged += TxtBuscarCodigoNombre_TextChanged;
        }

        private void TxtBuscarCodigoNombre_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_actualizandoSugerencias)
                return;

            string termino = (_txtBuscarCodigoNombre?.Text ?? string.Empty).Trim();
            if (termino.Length == 0)
            {
                CmbBuscarProducto.ItemsSource = null;
                CmbBuscarProducto.IsDropDownOpen = false;
                return;
            }

            var sugerencias = _productosCache
                .Where(p => CoincideBusquedaListado(p, termino))
                .Take(12)
                .Select(p => new SugerenciaProducto
                {
                    Codigo = (p.CodigoBarras ?? string.Empty).Trim(),
                    Nombre = p.Nombre ?? string.Empty
                })
                .ToList();

            CmbBuscarProducto.DisplayMemberPath = nameof(SugerenciaProducto.Texto);
            CmbBuscarProducto.SelectedValuePath = nameof(SugerenciaProducto.Codigo);
            CmbBuscarProducto.ItemsSource = sugerencias;
            CmbBuscarProducto.IsDropDownOpen = sugerencias.Count > 0;
        }

        private void CmbBuscarProducto_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SoporteContexto.Accion = "Buscar producto";
                BuscarProducto();
            }
        }

        private void BuscarProducto()
        {
            try
            {
                string codigo = (CmbBuscarProducto.Text ?? string.Empty).Trim();

                if (string.IsNullOrEmpty(codigo))
                {
                    SoporteContexto.Error = "Código vacío";
                    MessageBox.Show("Ingrese código o nombre");
                    return;
                }

                if (App.DbContext == null)
                {
                    SoporteContexto.Error = "DbContext no disponible";
                    MessageBox.Show("Error: DbContext no disponible");
                    return;
                }

                if (_productosCache.Count == 0)
                {
                    _productosCache = App.DbContext.Productos
                        .AsNoTracking()
                        .OrderBy(p => p.Id)
                        .ToList();
                }

                productoActual = _productosCache
                    .FirstOrDefault(p => string.Equals(
                        (p.CodigoBarras ?? string.Empty).Trim(),
                        codigo,
                        StringComparison.OrdinalIgnoreCase));

                productoActual ??= _productosCache
                    .FirstOrDefault(p => string.Equals(
                        p.Nombre ?? string.Empty,
                        codigo,
                        StringComparison.OrdinalIgnoreCase));

                productoActual ??= _productosCache
                    .FirstOrDefault(p => CoincideBusquedaListado(p, codigo));

                if (productoActual == null)
                {
                    SoporteContexto.Error = "Producto no encontrado";
                    MessageBox.Show("Producto no encontrado");
                    return;
                }

                // 🔥 CONTEXTO
                SoporteContexto.Producto = productoActual.Nombre;

                LblNombre.Text = productoActual.Nombre;
                LblNombre.Foreground = NombreSeleccionadoBrush;
                LblStock.Text = productoActual.Stock.ToString(CultureInfo.InvariantCulture);
                _actualizandoSugerencias = true;
                CmbBuscarProducto.Text = string.IsNullOrWhiteSpace(productoActual.CodigoBarras)
                    ? productoActual.Nombre
                    : productoActual.CodigoBarras;
                _actualizandoSugerencias = false;
                CmbBuscarProducto.IsDropDownOpen = false;
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error al buscar:\n" + ex.Message);
            }
            finally
            {
                _actualizandoSugerencias = false;
            }
        }

        private void CmbBuscarProducto_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CmbBuscarProducto.SelectedItem is not SugerenciaProducto s)
                return;

            _actualizandoSugerencias = true;
            CmbBuscarProducto.Text = !string.IsNullOrWhiteSpace(s.Codigo) ? s.Codigo : s.Nombre;
            _actualizandoSugerencias = false;
            BuscarProducto();
        }

        private void BtnImportarExcel_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureInventario())
                return;
            try
            {
                SoporteContexto.Accion = "Importar Excel inventario";

                if (App.DbContext == null)
                {
                    SoporteContexto.Error = "DbContext no disponible";
                    MessageBox.Show("Error: base de datos no disponible.", "Importar Excel");
                    return;
                }

                var dlg = new OpenFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    Title = "Lista de inventario (código + cantidad)"
                };

                if (dlg.ShowDialog() != true)
                    return;

                var res = InventarioExcelImportService.ImportarAjustesDesdeExcel(App.DbContext, dlg.FileName);

                string msg =
                    $"Filas con código leídas: {res.FilasProcesadas}\n" +
                    $"Productos actualizados: {res.ProductosActualizados}";

                if (res.Errores.Count > 0)
                {
                    int max = Math.Min(res.Errores.Count, 35);
                    msg += "\n\nAvisos / errores:\n" + string.Join("\n", res.Errores.Take(max));
                    if (res.Errores.Count > max)
                        msg += $"\n… y {res.Errores.Count - max} más.";
                }

                MessageBox.Show(
                    msg,
                    "Importar Excel",
                    MessageBoxButton.OK,
                    res.ProductosActualizados == 0 && res.Errores.Count > 0
                        ? MessageBoxImage.Warning
                        : MessageBoxImage.Information);

                if (productoActual != null)
                {
                    App.DbContext.Entry(productoActual).Reload();
                    LblStock.Text = productoActual.Stock.ToString();
                }

                _currentPage = 1;
                RefrescarGrid();
                GridInventario?.UpdateLayout();
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show(
                    "No se pudo leer o aplicar el archivo Excel:\n" + ex.Message,
                    "Importar Excel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureInventario())
                return;

            try
            {
                SoporteContexto.Accion = "Modificar stock";

                if (productoActual == null)
                {
                    SoporteContexto.Error = "Producto no seleccionado";
                    MessageBox.Show("Debe buscar un producto primero");
                    return;
                }

                if (!int.TryParse(TxtCantidad.Text, out int cantidad))
                {
                    SoporteContexto.Error = "Cantidad inválida";
                    MessageBox.Show("Cantidad inválida");
                    return;
                }

                if (cantidad == 0)
                {
                    MessageBox.Show("Ingrese una cantidad distinta de cero.");
                    return;
                }

                var cfg = AppConfig.Cargar();
                if (MulticajaInventoryWriter.ShouldUseCentralApi(cfg))
                {
                    if (App.UsuarioActual == null)
                    {
                        MessageBox.Show("Debe iniciar sesión para ajustar inventario.");
                        return;
                    }

                    if (App.CajaActualId == Guid.Empty && Guid.TryParse(cfg.CajaId, out var cfgCaja))
                        App.CajaActualId = cfgCaja;

                    if (App.CajaActualId == Guid.Empty)
                    {
                        MessageBox.Show("No hay caja asignada a este equipo.");
                        return;
                    }

                    var sesion = App.MulticajaSesionEnServidor
                                     ?? App.DbContext?.CajaSesiones.FirstOrDefault(c => c.Abierta);

                    var body = new MulticajaInventarioAjusteRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        CajaId = App.CajaActualId,
                        CajaSesionId = sesion?.Id ?? Guid.Empty,
                        UsuarioId = App.UsuarioActual.Id,
                        ProductoId = productoActual.Id,
                        CantidadDelta = cantidad,
                        Motivo = "Ajuste manual inventario"
                    };

                    var resp = await MulticajaOperacionesClient.AjustarInventarioAsync(body).ConfigureAwait(true);
                    if (resp == null || !resp.Ok)
                    {
                        MessageBox.Show(resp?.Error ?? "No se pudo ajustar inventario en el servidor.");
                        return;
                    }

                    if (MulticajaRuntime.UseApiOnlyClient)
                    {
                        await MulticajaShadowCatalogSync.PullProductsByIdsAsync([productoActual.Id])
                            .ConfigureAwait(true);
                    }
                    else
                    {
                        await App.DbContext!.Entry(productoActual).ReloadAsync().ConfigureAwait(true);
                    }

                    productoActual.Stock = resp.StockNuevo;
                    MessageBox.Show($"Stock actualizado en servidor: {resp.StockAnterior} → {resp.StockNuevo}");
                }
                else
                {
                    productoActual.Stock += cantidad;
                    App.DbContext!.SaveChanges();
                    MessageBox.Show("Stock actualizado");
                }

                LblStock.Text = productoActual.Stock.ToString(CultureInfo.InvariantCulture);
                TxtCantidad.Text = "0";
                CmbBuscarProducto.Focus();
                RefrescarGrid();
                CargarMovimientos();
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show("Error al guardar:\n" + ex.Message);
            }
        }

        private void BtnExportarLista_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                SoporteContexto.Accion = "Exportar lista de inventario";
                var lista = ObtenerProductosListadoActual();

                if (lista.Count == 0)
                {
                    MessageBox.Show("No hay datos para exportar.", "Exportar lista");
                    return;
                }

                var dlg = new SaveFileDialog
                {
                    Filter = "Excel (*.xlsx)|*.xlsx",
                    FileName = $"inventario_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                    Title = "Exportar lista de inventario"
                };

                if (dlg.ShowDialog() != true)
                    return;

                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add("Inventario");
                string[] headers =
                {
                    "ID", "Código", "Producto", "P. Costo", "P. Venta", "P. Mayoreo",
                    "Departamento", "Existencia", "Inv. Mínimo", "Inv. Máximo", "Tipo de venta"
                };

                for (int c = 0; c < headers.Length; c++)
                    ws.Cell(1, c + 1).Value = headers[c];

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

                MessageBox.Show(
                    $"Inventario exportado correctamente.\nArchivo: {Path.GetFileName(dlg.FileName)}",
                    "Exportar lista",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                SoporteContexto.Error = ex.Message;
                MessageBox.Show(
                    "No se pudo exportar la lista de inventario:\n" + ex.Message,
                    "Exportar lista",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void ActivarModo(InventarioModo modo)
        {
            _modoActual = modo;
            _currentPage = 1;
            PanelAgregarAjuste.Visibility = (modo == InventarioModo.Agregar || modo == InventarioModo.Ajustes) ? Visibility.Visible : Visibility.Collapsed;
            PanelReporteInventario.Visibility = modo == InventarioModo.Reporte ? Visibility.Visible : Visibility.Collapsed;
            PanelMovimientos.Visibility = modo == InventarioModo.Movimientos ? Visibility.Visible : Visibility.Collapsed;
            PanelKardex.Visibility = modo == InventarioModo.Kardex ? Visibility.Visible : Visibility.Collapsed;
            PanelListado.Visibility = modo != InventarioModo.Movimientos && modo != InventarioModo.Kardex ? Visibility.Visible : Visibility.Collapsed;

            TxtTituloModo.Text = modo == InventarioModo.Ajustes ? "AJUSTAR INVENTARIO" : "AGREGAR INVENTARIO";
            switch (modo)
            {
                case InventarioModo.ProductosBajos:
                    SoporteContexto.Accion = "Productos bajos en inventario";
                    break;
                case InventarioModo.Reporte:
                    SoporteContexto.Accion = "Reporte de inventario";
                    CargarDepartamentos();
                    break;
                case InventarioModo.Movimientos:
                    SoporteContexto.Accion = "Reporte de movimientos";
                    CargarMovimientos();
                    break;
                case InventarioModo.Kardex:
                    SoporteContexto.Accion = "Kardex";
                    break;
                case InventarioModo.Ajustes:
                    SoporteContexto.Accion = "Ajustes inventario";
                    break;
                default:
                    SoporteContexto.Accion = "Agregar inventario";
                    break;
            }

            RefrescarGrid();
        }

        private void CargarDepartamentos()
        {
            if (CmbDepartamentoReporte == null)
                return;
            var deps = _productosCache
                .Select(p => (p.Departamento ?? "").Trim())
                .Where(d => !string.IsNullOrWhiteSpace(d))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d)
                .ToList();
            deps.Insert(0, "Todos");
            CmbDepartamentoReporte.ItemsSource = deps;
            if (CmbDepartamentoReporte.SelectedIndex < 0)
                CmbDepartamentoReporte.SelectedIndex = 0;
        }

        private void CargarMovimientos()
        {
            if (App.DbContext == null)
                return;
            var movimientos = new List<MovimientoInventarioView>();

            try
            {
                var cajeroActual = App.UsuarioActual != null ? App.UsuarioActual.Username : "";
                var ajustes = App.DbContext.MovimientosCaja
                    .AsNoTracking()
                    .Where(m => m.Tipo == "INGRESO" || m.Tipo == "RETIRO")
                    .OrderByDescending(m => m.Fecha)
                    .Take(100)
                    .Select(m => new MovimientoInventarioView
                    {
                        Fecha = m.Fecha,
                        Producto = m.Descripcion ?? "",
                        Tipo = m.Tipo,
                        Cantidad = 0,
                        Cajero = cajeroActual,
                        Referencia = "Movimiento de caja"
                    }).ToList();
                movimientos.AddRange(ajustes);
            }
            catch { }

            try
            {
                var ventas = App.DbContext.Ventas
                    .AsNoTracking()
                    .OrderByDescending(v => v.Fecha)
                    .Take(100)
                    .ToList();
                var ventaIds = ventas.Select(v => v.Id).ToList();
                var detalles = App.DbContext.DetalleVentas
                    .AsNoTracking()
                    .Where(d => ventaIds.Contains(d.VentaId))
                    .ToList();
                foreach (var d in detalles)
                {
                    var v = ventas.FirstOrDefault(x => x.Id == d.VentaId);
                    if (v == null) continue;
                    movimientos.Add(new MovimientoInventarioView
                    {
                        Fecha = v.Fecha,
                        Producto = d.Producto,
                        Tipo = v.EsConsumoPersonal ? "CONSUMO_PERSONAL" : "VENTA",
                        Cantidad = -d.Cantidad,
                        Cajero = v.Cajero ?? "",
                        Referencia = v.EsConsumoPersonal
                            ? $"Consumo personal #{v.NumeroTicket}"
                            : $"Ticket #{v.NumeroTicket}"
                    });
                }
            }
            catch { }

            GridMovimientos.ItemsSource = movimientos
                .OrderByDescending(m => m.Fecha)
                .Take(300)
                .ToList();
        }

        private List<MovimientoInventarioView> ObtenerMovimientosKardex()
        {
            var actual = GridMovimientos.ItemsSource as IEnumerable<MovimientoInventarioView>;
            return actual?.ToList() ?? new List<MovimientoInventarioView>();
        }

        private List<Producto> ObtenerProductosListadoActual() =>
            _listaFiltradaActual.Count > 0 ? _listaFiltradaActual.ToList() : new List<Producto>();

        private void BtnModoAgregar_Click(object sender, RoutedEventArgs e) => ActivarModo(InventarioModo.Agregar);
        private void BtnModoAjustes_Click(object sender, RoutedEventArgs e) => ActivarModo(InventarioModo.Ajustes);
        private void BtnModoBajos_Click(object sender, RoutedEventArgs e) => ActivarModo(InventarioModo.ProductosBajos);
        private void BtnModoReporte_Click(object sender, RoutedEventArgs e) => ActivarModo(InventarioModo.Reporte);
        private void BtnModoMovimientos_Click(object sender, RoutedEventArgs e) => ActivarModo(InventarioModo.Movimientos);
        private void BtnModoKardex_Click(object sender, RoutedEventArgs e)
        {
            CargarMovimientos();
            ActivarModo(InventarioModo.Kardex);
        }
        private void CmbDepartamentoReporte_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_modoActual == InventarioModo.Reporte)
            {
                _currentPage = 1;
                RefrescarGrid();
            }
        }

        private void BtnBuscarKardex_Click(object sender, RoutedEventArgs e)
        {
            string q = (TxtKardexProducto.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                GridKardex.ItemsSource = null;
                return;
            }
            var movs = ObtenerMovimientosKardex()
                .Where(m => (m.Producto ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(m => m.Fecha)
                .ToList();
            GridKardex.ItemsSource = movs;
        }

        private void BtnReporteExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            ExportarProductosExcel(ObtenerProductosListadoActual(), "reporte_inventario");
        }

        private void BtnReporteExportarPdf_Click(object sender, RoutedEventArgs e)
        {
            ExportarProductosPdf(ObtenerProductosListadoActual(), "reporte_inventario");
        }

        private void BtnReporteImprimir_Click(object sender, RoutedEventArgs e)
        {
            BtnReporteExportarPdf_Click(sender, e);
        }

        private void BtnKardexExportarExcel_Click(object sender, RoutedEventArgs e)
        {
            var kardex = (GridKardex.ItemsSource as IEnumerable<MovimientoInventarioView>)?.ToList()
                         ?? ObtenerMovimientosKardex();
            ExportarKardexExcel(kardex, "kardex_inventario");
        }

        private void BtnKardexExportarPdf_Click(object sender, RoutedEventArgs e)
        {
            var kardex = (GridKardex.ItemsSource as IEnumerable<MovimientoInventarioView>)?.ToList()
                         ?? ObtenerMovimientosKardex();
            ExportarKardexPdf(kardex, "kardex_inventario");
        }

        private void BtnKardexImprimir_Click(object sender, RoutedEventArgs e)
        {
            BtnKardexExportarPdf_Click(sender, e);
        }

        private static void ExportarProductosExcel(List<Producto> lista, string prefijo)
        {
            if (lista.Count == 0)
            {
                MessageBox.Show("No hay datos para exportar.", "Exportar Excel");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"{prefijo}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                Title = "Exportar Excel"
            };
            if (dlg.ShowDialog() != true)
                return;

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Reporte");
            var headers = new[]
            {
                "Código", "Producto", "Costo", "Precio Venta", "Precio Mayoreo",
                "Departamento", "Existencia", "Inv. Mínimo", "Inv. Máximo", "Tipo venta"
            };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];

            for (int i = 0; i < lista.Count; i++)
            {
                var p = lista[i];
                int r = i + 2;
                ws.Cell(r, 1).Value = p.CodigoBarras ?? "";
                ws.Cell(r, 2).Value = p.Nombre ?? "";
                ws.Cell(r, 3).Value = p.Costo;
                ws.Cell(r, 4).Value = p.Precio;
                ws.Cell(r, 5).Value = p.PrecioMayoreo;
                ws.Cell(r, 6).Value = p.Departamento ?? "";
                ws.Cell(r, 7).Value = p.Stock;
                ws.Cell(r, 8).Value = p.InvMinimo;
                ws.Cell(r, 9).Value = p.InvMaximo;
                ws.Cell(r, 10).Value = p.TipoVenta ?? "";
            }
            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);
            MessageBox.Show("Reporte exportado correctamente.", "Exportar Excel", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ExportarKardexExcel(List<MovimientoInventarioView> lista, string prefijo)
        {
            if (lista.Count == 0)
            {
                MessageBox.Show("No hay movimientos para exportar.", "Exportar Excel");
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Excel (*.xlsx)|*.xlsx",
                FileName = $"{prefijo}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx",
                Title = "Exportar Excel"
            };
            if (dlg.ShowDialog() != true)
                return;

            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Kardex");
            var headers = new[] { "Fecha", "Tipo", "Cantidad", "Producto", "Referencia", "Cajero" };
            for (int c = 0; c < headers.Length; c++)
                ws.Cell(1, c + 1).Value = headers[c];
            for (int i = 0; i < lista.Count; i++)
            {
                var m = lista[i];
                int r = i + 2;
                ws.Cell(r, 1).Value = m.Fecha;
                ws.Cell(r, 2).Value = m.Tipo;
                ws.Cell(r, 3).Value = m.Cantidad;
                ws.Cell(r, 4).Value = m.Producto;
                ws.Cell(r, 5).Value = m.Referencia;
                ws.Cell(r, 6).Value = m.Cajero;
            }
            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);
            MessageBox.Show("Kardex exportado correctamente.", "Exportar Excel", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ExportarProductosPdf(List<Producto> lista, string prefijo)
        {
            if (lista.Count == 0)
            {
                MessageBox.Show("No hay datos para exportar.", "Exportar PDF");
                return;
            }
            var dlg = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = $"{prefijo}_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
                Title = "Exportar PDF"
            };
            if (dlg.ShowDialog() != true)
                return;

            var doc = new PdfDocument();
            doc.Info.Title = "Reporte de inventario";
            var page = doc.AddPage();
            var g = XGraphics.FromPdfPage(page);
            var fontTitle = new XFont("Arial", 12, XFontStyleEx.Bold);
            var font = new XFont("Arial", 8, XFontStyleEx.Regular);
            double y = 28;
            g.DrawString("Reporte de inventario", fontTitle, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 20), XStringFormats.TopLeft);
            y += 24;
            g.DrawString("Codigo | Producto | Costo | Precio | Stock", fontTitle, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 20), XStringFormats.TopLeft);
            y += 20;
            foreach (var p in lista.Take(120))
            {
                string row = $"{(p.CodigoBarras ?? "").PadRight(8)} | {(p.Nombre ?? "").PadRight(28)} | {p.Costo:N0} | {p.Precio:N0} | {p.Stock}";
                g.DrawString(row, font, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 14), XStringFormats.TopLeft);
                y += 12;
                if (y > page.Height.Point - 24) break;
            }
            doc.Save(dlg.FileName);
            MessageBox.Show("PDF generado correctamente.", "Exportar PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private static void ExportarKardexPdf(List<MovimientoInventarioView> lista, string prefijo)
        {
            if (lista.Count == 0)
            {
                MessageBox.Show("No hay movimientos para exportar.", "Exportar PDF");
                return;
            }
            var dlg = new SaveFileDialog
            {
                Filter = "PDF (*.pdf)|*.pdf",
                FileName = $"{prefijo}_{DateTime.Now:yyyyMMdd_HHmm}.pdf",
                Title = "Exportar PDF"
            };
            if (dlg.ShowDialog() != true)
                return;

            var doc = new PdfDocument();
            doc.Info.Title = "Kardex de inventario";
            var page = doc.AddPage();
            var g = XGraphics.FromPdfPage(page);
            var fontTitle = new XFont("Arial", 12, XFontStyleEx.Bold);
            var font = new XFont("Arial", 8, XFontStyleEx.Regular);
            double y = 28;
            g.DrawString("Kardex inventario", fontTitle, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 20), XStringFormats.TopLeft);
            y += 24;
            g.DrawString("Fecha | Tipo | Cantidad | Producto | Referencia", fontTitle, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 20), XStringFormats.TopLeft);
            y += 20;
            foreach (var m in lista.Take(140))
            {
                string row = $"{m.Fecha:dd/MM HH:mm} | {m.Tipo} | {m.Cantidad} | {m.Producto} | {m.Referencia}";
                g.DrawString(row, font, XBrushes.Black, new XRect(20, y, page.Width.Point - 40, 14), XStringFormats.TopLeft);
                y += 12;
                if (y > page.Height.Point - 24) break;
            }
            doc.Save(dlg.FileName);
            MessageBox.Show("PDF generado correctamente.", "Exportar PDF", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}