using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using GrunflexPOS2.Data;
using GrunflexPOS2.Models;
using GrunflexPOS2.Services.Multicaja;
using GrunflexPOS2.Services.Multicaja.Sync;
using GrunflexPOS2.Services.Multicaja.UI;
using System.Threading;
using GrunflexPOS2.Services.Connectivity;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using GrunflexPOS2.ViewModels;
using GrunflexPOS2.UI;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views
{
    public partial class VentasView : UserControl
    {
        private readonly VentasViewModel _vm;
        private VentaItem? _ultimoProductoAgregado;
        private readonly List<Producto> _productosAuto = new();
        private readonly HashSet<string> _codigosBarrasExactos = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Producto> _productosPorCodigo = new(StringComparer.OrdinalIgnoreCase);
        private TextBox? _txtCodigoNombre;
        private bool _actualizandoSugerencias;
        private bool _navegandoSugerencias;
        private bool _ignorarTextChangedNavegacion;
        private int _indiceSugerencia = -1;
        private string _textoBusqueda = string.Empty;
        private int _intentosEnlazarCajaCodigo;
        private readonly DispatcherTimer _autoScanTimer;
        private bool _autoInsertInProgress;
        private string? _scanCodigoPendiente;
        private long _ultimoCambioTextoMs;
        private bool _suprimirAutocompleteScan;
        private readonly StringBuilder _globalScanBuffer = new();
        private readonly DispatcherTimer _globalScanTimer;
        private Window? _hostWindow;
        private string _ultimoCodigoProcesado = string.Empty;
        private long _ultimoCodigoProcesadoMs;
        private ReactiveViewRefresh? _reactiveRefresh;

        private sealed class SugerenciaProducto
        {
            public required string Codigo { get; init; }
            public required string Nombre { get; init; }
            public string Etiqueta => string.IsNullOrWhiteSpace(Codigo) ? Nombre : $"{Codigo} - {Nombre}";
        }

        public event Action<decimal, decimal, decimal, int>? ResumenActualizado;

        public VentasView()
        {
            InitializeComponent();

            _vm = new VentasViewModel(App.ProductoLookup);
            DataContext = _vm;

            _vm.ResumenActualizado += (bruto, desc, neto, art) =>
                ResumenActualizado?.Invoke(bruto, desc, neto, art);
            _vm.ProductoAgregado += item =>
            {
                _ultimoProductoAgregado = item;
                Dispatcher.BeginInvoke(() =>
                {
                    VentasGrid.SelectedItem = item;
                    VentasGrid.ScrollIntoView(item);
                }, DispatcherPriority.Input);
            };

            VentasGrid.ItemsSource = _vm.Items;
            _vm.Items.CollectionChanged += Items_CollectionChanged;

            Loaded += (_, _) =>
            {
                LectorCodigoService.CodigoRecibido += LectorCodigoService_CodigoRecibido;
                BtnToolbarDescuento.Visibility = UsuarioPermisos.PuedeAplicarDescuentos()
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            };
            Unloaded += VentasView_Unloaded;
            LayoutUpdated += VentasView_LayoutUpdated;

            // Para lector USB "modo teclado": agrega al grid al detectar código exacto en ráfaga.
            _autoScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(8) };
            _autoScanTimer.Tick += async (_, _) =>
            {
                _autoScanTimer.Stop();
                if (_autoInsertInProgress || !IsVisible)
                    return;

                try
                {
                    _autoInsertInProgress = true;
                    if (!string.IsNullOrWhiteSpace(_scanCodigoPendiente))
                    {
                        var codigo = _scanCodigoPendiente;
                        _scanCodigoPendiente = null;
                        await AgregarEscaneoDirectoAsync(codigo);
                    }
                }
                finally
                {
                    _autoInsertInProgress = false;
                }
            };

            // Captura escaneo cuando el foco está fuera del cuadro de código.
            _globalScanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
            _globalScanTimer.Tick += async (_, _) =>
            {
                _globalScanTimer.Stop();
                await FlushGlobalScanBufferAsync();
            };
        }

        private static TextBox? BuscarPartEditableTextBox(DependencyObject raiz)
        {
            var n = VisualTreeHelper.GetChildrenCount(raiz);
            for (var i = 0; i < n; i++)
            {
                var hijo = VisualTreeHelper.GetChild(raiz, i);
                if (hijo is TextBox tb && tb.Name == "PART_EditableTextBox")
                    return tb;
                var profundo = BuscarPartEditableTextBox(hijo);
                if (profundo != null)
                    return profundo;
            }

            return null;
        }

        private void VentasView_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_txtCodigoNombre != null)
            {
                LayoutUpdated -= VentasView_LayoutUpdated;
                return;
            }

            if (_intentosEnlazarCajaCodigo >= 12)
            {
                LayoutUpdated -= VentasView_LayoutUpdated;
                return;
            }

            _intentosEnlazarCajaCodigo++;
            CodigoComboBox.ApplyTemplate();
            ConfigurarCajaEditable();
        }

        private void VentasGrid_MouseLeave(object sender, MouseEventArgs e)
        {
            // Pistola en modo teclado: al salir del grid, volver al campo de código.
            if (IsVisible)
                Dispatcher.BeginInvoke(new Action(EnfocarCodigo), DispatcherPriority.Input);
        }

        private void LectorCodigoService_CodigoRecibido(string codigo)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (!IsVisible || string.IsNullOrWhiteSpace(codigo))
                    return;

                // Evita doble alta cuando un mismo escaneo entra por más de un canal.
                await AgregarEscaneoDirectoAsync(codigo.Trim());
            }), DispatcherPriority.Input);
        }

        private void Items_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.BeginInvoke(new Action(ActualizarEstadoVacioCarrito));
            Dispatcher.BeginInvoke(AjustarAlturaGridVentas);
        }

        private void ActualizarEstadoVacioCarrito()
        {
            var vacio = _vm.Items.Count == 0;
            PanelVacioCarrito.Visibility = vacio ? Visibility.Visible : Visibility.Collapsed;
            GridEstadoVacio.Visibility = vacio ? Visibility.Visible : Visibility.Collapsed;
        }

        private void VentasView_OnLoaded(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Modulo = "Ventas";
            SoporteContexto.Accion = "Inicio";
            ActualizarEstadoVacioCarrito();
            CargarProductosAutocomplete();

            if (MulticajaRuntime.UseApiOnlyClient && App.MulticajaRealtime != null)
            {
                _reactiveRefresh = new ReactiveViewRefresh(
                    this,
                    async ct =>
                    {
                        await CargarProductosAutocompleteAsync(ct).ConfigureAwait(true);
                        RefrescarCatalogoTrasSyncServidor();
                        return;
                    },
                    App.MulticajaRealtime.Caches.Catalog,
                    App.MulticajaRealtime.Caches.Inventory);
            }

            CodigoComboBox.ApplyTemplate();
            ConfigurarCajaEditable();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                CodigoComboBox.ApplyTemplate();
                ConfigurarCajaEditable();
                EnfocarCodigo();
            }), DispatcherPriority.Loaded);

            HookGlobalScannerCapture();
            AjustarAlturaGridVentas();
        }

        private void VentasView_SizeChanged(object sender, SizeChangedEventArgs e) =>
            AjustarAlturaGridVentas();

        private void AjustarAlturaGridVentas()
        {
            const double rowHeight = 36;
            const double headerHeight = 36;
            const double chrome = 8;
            var filas = Math.Max(_vm.Items.Count, 1);
            var alturaGrid = headerHeight + filas * rowHeight + chrome;

            PosGridHeightHelper.AjustarAlturaGrid(
                this,
                VentasGrid,
                reservarSuperior: 260,
                minAltura: Math.Max(alturaGrid, 72),
                maxFraccionViewport: 0.45);

            VentasGrid.MaxHeight = Math.Max(VentasGrid.MaxHeight, alturaGrid);
        }

        private void VentasView_Unloaded(object sender, RoutedEventArgs e)
        {
            _reactiveRefresh?.Dispose();
            _reactiveRefresh = null;
            LectorCodigoService.CodigoRecibido -= LectorCodigoService_CodigoRecibido;
            UnhookGlobalScannerCapture();
        }

        private void RefrescarCatalogoTrasSyncServidor()
        {
            CargarProductosAutocomplete();

            var texto = (_txtCodigoNombre?.Text ?? _textoBusqueda ?? string.Empty).Trim();
            if (texto.Length == 0 || _suprimirAutocompleteScan || _txtCodigoNombre == null)
                return;

            var opciones = _productosAuto
                .Where(p =>
                    (!string.IsNullOrWhiteSpace(p.CodigoBarras) &&
                     p.CodigoBarras.Contains(texto, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(p.Nombre) &&
                     p.Nombre.Contains(texto, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Nombre)
                .Take(25)
                .Select(p => new SugerenciaProducto
                {
                    Codigo = (p.CodigoBarras ?? string.Empty).Trim(),
                    Nombre = p.Nombre ?? string.Empty
                })
                .ToList();

            _actualizandoSugerencias = true;
            try
            {
                CodigoComboBox.DisplayMemberPath = nameof(SugerenciaProducto.Etiqueta);
                CodigoComboBox.SelectedValuePath = nameof(SugerenciaProducto.Codigo);
                CodigoComboBox.ItemsSource = opciones;
                CodigoComboBox.SelectedItem = null;
                _indiceSugerencia = -1;
                if (opciones.Count > 0 && IsVisible)
                    CodigoComboBox.IsDropDownOpen = true;
            }
            finally
            {
                _actualizandoSugerencias = false;
            }
        }

        private void VentasView_OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                Dispatcher.BeginInvoke(new Action(EnfocarCodigo), System.Windows.Threading.DispatcherPriority.Input);
                HookGlobalScannerCapture();
            }
            else
            {
                UnhookGlobalScannerCapture();
            }
        }

        private void HookGlobalScannerCapture()
        {
            if (_hostWindow != null)
                return;

            _hostWindow = Window.GetWindow(this);
            if (_hostWindow == null)
                return;

            _hostWindow.AddHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(HostWindow_PreviewTextInput), true);
            _hostWindow.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(HostWindow_PreviewKeyDown), true);
        }

        private void UnhookGlobalScannerCapture()
        {
            if (_hostWindow == null)
                return;

            _hostWindow.RemoveHandler(UIElement.PreviewTextInputEvent, new TextCompositionEventHandler(HostWindow_PreviewTextInput));
            _hostWindow.RemoveHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(HostWindow_PreviewKeyDown));
            _hostWindow = null;
            _globalScanBuffer.Clear();
            _globalScanTimer.Stop();
        }

        private bool IsFocusOnCodigoBox()
        {
            if (_txtCodigoNombre == null)
                return false;
            return ReferenceEquals(Keyboard.FocusedElement, _txtCodigoNombre) ||
                   ReferenceEquals(Keyboard.FocusedElement, CodigoComboBox);
        }

        private bool ShouldCaptureGlobalScan()
        {
            if (!IsVisible)
                return false;

            if (IsFocusOnCodigoBox())
                return false;

            // Si el foco está en otro campo de texto real, no interceptar.
            if (Keyboard.FocusedElement is TextBox or PasswordBox or RichTextBox)
                return false;

            return true;
        }

        private void HostWindow_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (!ShouldCaptureGlobalScan())
                return;

            var t = e.Text ?? string.Empty;
            if (string.IsNullOrEmpty(t) || char.IsControl(t[0]))
                return;

            _globalScanBuffer.Append(t);
            _globalScanTimer.Stop();
            _globalScanTimer.Start();
            e.Handled = true;
        }

        private async void HostWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!ShouldCaptureGlobalScan())
                return;

            if (e.Key is Key.Enter or Key.Return)
            {
                if (_globalScanBuffer.Length > 0)
                {
                    _globalScanTimer.Stop();
                    await FlushGlobalScanBufferAsync();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Back && _globalScanBuffer.Length > 0)
            {
                _globalScanBuffer.Length -= 1;
                e.Handled = true;
            }
        }

        private async Task FlushGlobalScanBufferAsync()
        {
            if (_globalScanBuffer.Length == 0 || _autoInsertInProgress)
                return;

            var code = _globalScanBuffer.ToString().Trim();
            _globalScanBuffer.Clear();
            if (string.IsNullOrWhiteSpace(code))
                return;

            await AgregarEscaneoDirectoAsync(code);
        }

        private void EnfocarCodigo()
        {
            CodigoComboBox.Focus();
            Keyboard.Focus(CodigoComboBox);
        }

        private void CargarProductosAutocomplete() => _ = CargarProductosAutocompleteAsync(CancellationToken.None);

        private async Task CargarProductosAutocompleteAsync(CancellationToken ct)
        {
            _productosAuto.Clear();
            _codigosBarrasExactos.Clear();
            _productosPorCodigo.Clear();
            if (App.DbContext == null)
                return;

            ct.ThrowIfCancellationRequested();
            var lista = await App.DbContext.Productos
                .AsNoTracking()
                .OrderBy(p => p.Nombre)
                .ToListAsync(ct)
                .ConfigureAwait(true);
            _productosAuto.AddRange(lista);
            foreach (var p in lista)
            {
                var codigo = (p.CodigoBarras ?? string.Empty).Trim();
                if (!string.IsNullOrWhiteSpace(codigo))
                {
                    _codigosBarrasExactos.Add(codigo);
                    if (!_productosPorCodigo.ContainsKey(codigo))
                        _productosPorCodigo[codigo] = p;
                }
            }
        }

        public void RefrescarAutocompletadoProductos() => CargarProductosAutocomplete();

        private void ConfigurarCajaEditable()
        {
            if (_txtCodigoNombre != null)
                return;

            CodigoComboBox.ApplyTemplate();
            var txt = CodigoComboBox.Template?.FindName("PART_EditableTextBox", CodigoComboBox) as TextBox
                      ?? BuscarPartEditableTextBox(CodigoComboBox);
            if (txt == null)
                return;

            _txtCodigoNombre = txt;
            txt.TextChanged -= CodigoComboBox_TextChanged;
            txt.TextChanged += CodigoComboBox_TextChanged;
            txt.PreviewKeyDown -= CodigoComboBox_PreviewKeyDown;
            txt.PreviewKeyDown += CodigoComboBox_PreviewKeyDown;
        }

        private void CodigoComboBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_actualizandoSugerencias || _navegandoSugerencias || _ignorarTextChangedNavegacion)
                return;

            if (_txtCodigoNombre == null)
                return;

            var textoOriginal = _txtCodigoNombre.Text ?? string.Empty;
            var cursor = _txtCodigoNombre.SelectionStart;

            string texto = (_txtCodigoNombre?.Text ?? string.Empty).Trim();
            if (texto.Length == 0)
            {
                CodigoComboBox.ItemsSource = null;
                CodigoComboBox.IsDropDownOpen = false;
                CodigoComboBox.SelectedItem = null;
                _indiceSugerencia = -1;
                _textoBusqueda = string.Empty;
                _suprimirAutocompleteScan = false;
                return;
            }

            var ahora = Environment.TickCount64;
            var delta = _ultimoCambioTextoMs == 0 ? long.MaxValue : (ahora - _ultimoCambioTextoMs);
            _ultimoCambioTextoMs = ahora;

            // Si los cambios llegan en ráfaga, asumimos escaneo y ocultamos autocompletado visual.
            if (delta <= 35 && texto.Length >= 2)
                _suprimirAutocompleteScan = true;
            else if (delta >= 250)
                _suprimirAutocompleteScan = false;

            if (_suprimirAutocompleteScan)
            {
                CodigoComboBox.ItemsSource = null;
                CodigoComboBox.IsDropDownOpen = false;
                CodigoComboBox.SelectedItem = null;
                _indiceSugerencia = -1;
                _textoBusqueda = string.Empty;
                ProgramarAutoAgregarPorEscaner(texto);
                return;
            }

            var opciones = _productosAuto
                .Where(p =>
                    (!string.IsNullOrWhiteSpace(p.CodigoBarras) &&
                     p.CodigoBarras.Contains(texto, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrWhiteSpace(p.Nombre) &&
                     p.Nombre.Contains(texto, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(p => p.Nombre)
                .Take(25)
                .Select(p => new SugerenciaProducto
                {
                    Codigo = (p.CodigoBarras ?? string.Empty).Trim(),
                    Nombre = p.Nombre ?? string.Empty
                })
                .ToList();

            _actualizandoSugerencias = true;
            CodigoComboBox.DisplayMemberPath = nameof(SugerenciaProducto.Etiqueta);
            CodigoComboBox.SelectedValuePath = nameof(SugerenciaProducto.Codigo);
            CodigoComboBox.ItemsSource = opciones;
            CodigoComboBox.SelectedItem = null;
            _indiceSugerencia = -1;
            _textoBusqueda = textoOriginal;
            CodigoComboBox.IsDropDownOpen = opciones.Count > 0;
            _txtCodigoNombre.Text = textoOriginal;
            _txtCodigoNombre.SelectionStart = Math.Min(cursor, _txtCodigoNombre.Text.Length);
            _txtCodigoNombre.SelectionLength = 0;
            _actualizandoSugerencias = false;

            ProgramarAutoAgregarPorEscaner(texto);
        }

        private void ProgramarAutoAgregarPorEscaner(string texto)
        {
            if (_autoInsertInProgress || _txtCodigoNombre == null)
                return;

            var t = (texto ?? string.Empty).Trim();
            if (t.Length < 4)
            {
                _autoScanTimer.Stop();
                return;
            }

            // Solo auto-agrega cuando hay coincidencia exacta por código de barras.
            var exacto = _codigosBarrasExactos.Contains(t);
            if (!exacto)
            {
                _autoScanTimer.Stop();
                return;
            }

            _scanCodigoPendiente = t;
            // Escaneo rápido: no mostrar autocompletado ni reemplazar por "codigo - nombre".
            _actualizandoSugerencias = true;
            CodigoComboBox.ItemsSource = null;
            CodigoComboBox.SelectedItem = null;
            CodigoComboBox.IsDropDownOpen = false;
            _indiceSugerencia = -1;
            _textoBusqueda = string.Empty;
            _actualizandoSugerencias = false;

            _autoScanTimer.Stop();
            _autoScanTimer.Start();
        }

        private async Task AgregarEscaneoDirectoAsync(string codigo)
        {
            if (string.IsNullOrWhiteSpace(codigo))
                return;

            var c = codigo.Trim();
            var ahora = Environment.TickCount64;
            if (string.Equals(_ultimoCodigoProcesado, c, StringComparison.OrdinalIgnoreCase) &&
                (ahora - _ultimoCodigoProcesadoMs) <= 200)
            {
                return;
            }

            _ultimoCodigoProcesado = c;
            _ultimoCodigoProcesadoMs = ahora;

            if (_productosAuto.Count == 0)
                CargarProductosAutocomplete();

            if (_productosPorCodigo.TryGetValue(c, out var productoLocal))
                _vm.AgregarDesdeBusquedaLocal(productoLocal);
            else
                await _vm.AgregarPorCodigoBarrasAsync(c);

            // Limpiar cuadro sin mostrar valor escaneado.
            _ignorarTextChangedNavegacion = true;
            CodigoComboBox.Text = string.Empty;
            if (_txtCodigoNombre != null)
            {
                _txtCodigoNombre.Text = string.Empty;
                _txtCodigoNombre.SelectionStart = 0;
                _txtCodigoNombre.SelectionLength = 0;
            }
            _ignorarTextChangedNavegacion = false;
            CodigoComboBox.ItemsSource = null;
            CodigoComboBox.IsDropDownOpen = false;
            _indiceSugerencia = -1;
            _textoBusqueda = string.Empty;
            _suprimirAutocompleteScan = false;
            _ultimoCambioTextoMs = 0;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                // Para edición rápida por teclado: dejar seleccionado el último producto escaneado.
                var ultimo = _vm.Items.LastOrDefault();
                if (ultimo == null)
                    return;

                VentasGrid.SelectedItem = ultimo;
                VentasGrid.ScrollIntoView(ultimo);
                VentasGrid.CurrentCell = new DataGridCellInfo(ultimo, VentasGrid.Columns.FirstOrDefault());
                VentasGrid.Focus();
                Keyboard.Focus(VentasGrid);
            }), DispatcherPriority.Input);
        }

        private Producto? BuscarProductoLocal(string criterio)
        {
            if (string.IsNullOrWhiteSpace(criterio))
                return null;

            var q = criterio.Trim();
            return _productosAuto.FirstOrDefault(p =>
                       string.Equals(p.CodigoBarras?.Trim(), q, StringComparison.OrdinalIgnoreCase))
                   ?? _productosAuto.FirstOrDefault(p =>
                       string.Equals(p.Nombre?.Trim(), q, StringComparison.OrdinalIgnoreCase))
                   ?? _productosAuto.FirstOrDefault(p =>
                       (!string.IsNullOrWhiteSpace(p.CodigoBarras) &&
                        p.CodigoBarras.StartsWith(q, StringComparison.OrdinalIgnoreCase)) ||
                       (!string.IsNullOrWhiteSpace(p.Nombre) &&
                        p.Nombre.StartsWith(q, StringComparison.OrdinalIgnoreCase)))
                   ?? _productosAuto.FirstOrDefault(p =>
                       (!string.IsNullOrWhiteSpace(p.CodigoBarras) &&
                        p.CodigoBarras.Contains(q, StringComparison.OrdinalIgnoreCase)) ||
                       (!string.IsNullOrWhiteSpace(p.Nombre) &&
                        p.Nombre.Contains(q, StringComparison.OrdinalIgnoreCase)));
        }

        private async void CodigoComboBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                MoverSeleccionSugerencia(e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;
            }

            if (e.Key != Key.Enter)
                return;

            e.Handled = true;
            await AgregarDesdeEntradaAsync();
        }

        private void MoverSeleccionSugerencia(int delta)
        {
            if (CodigoComboBox.ItemsSource is not IEnumerable<SugerenciaProducto> src)
                return;

            var lista = src.ToList();
            if (lista.Count == 0)
                return;

            int idx = CodigoComboBox.SelectedIndex;
            if (_indiceSugerencia >= 0 && _indiceSugerencia < lista.Count)
                idx = _indiceSugerencia;
            else if (idx < 0)
                idx = delta > 0 ? -1 : 0;

            idx += delta;
            if (idx < 0) idx = 0;
            if (idx >= lista.Count) idx = lista.Count - 1;

            try
            {
                _navegandoSugerencias = true;
                _ignorarTextChangedNavegacion = true;
                CodigoComboBox.IsDropDownOpen = true;
                _indiceSugerencia = idx;
                CodigoComboBox.SelectedIndex = idx;
                if (_txtCodigoNombre != null)
                {
                    _txtCodigoNombre.Text = _textoBusqueda;
                    _txtCodigoNombre.SelectionStart = _txtCodigoNombre.Text.Length;
                    _txtCodigoNombre.SelectionLength = 0;
                }
            }
            finally
            {
                _navegandoSugerencias = false;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ignorarTextChangedNavegacion = false;
                    if (CodigoComboBox.ItemsSource is IEnumerable<SugerenciaProducto> items && items.Any())
                        CodigoComboBox.IsDropDownOpen = true;
                }), DispatcherPriority.Input);
            }
        }

        private async Task AgregarDesdeEntradaAsync()
        {
            _autoScanTimer.Stop();
            _scanCodigoPendiente = null;

            if (_productosAuto.Count == 0)
                CargarProductosAutocomplete();

            if (CodigoComboBox.SelectedItem == null &&
                _indiceSugerencia >= 0 &&
                CodigoComboBox.ItemsSource is IEnumerable<SugerenciaProducto> listaConIndice)
            {
                var arr = listaConIndice.ToList();
                if (_indiceSugerencia < arr.Count)
                    CodigoComboBox.SelectedItem = arr[_indiceSugerencia];
            }

            if (CodigoComboBox.SelectedItem == null &&
                CodigoComboBox.ItemsSource is IEnumerable<SugerenciaProducto> listaSug)
            {
                var primera = listaSug.FirstOrDefault();
                if (primera != null)
                    CodigoComboBox.SelectedItem = primera;
            }

            var textoIngresado = (_txtCodigoNombre?.Text ?? CodigoComboBox.Text ?? string.Empty).Trim();
            var codigo = CodigoComboBox.SelectedItem is SugerenciaProducto s
                ? (!string.IsNullOrWhiteSpace(s.Codigo) ? s.Codigo : s.Nombre)
                : textoIngresado;

            if (string.IsNullOrWhiteSpace(codigo) && !string.IsNullOrWhiteSpace(textoIngresado))
                codigo = textoIngresado;

            // Soporta texto mostrado en la lista: "codigo - nombre".
            if (codigo.Contains(" - ", StringComparison.Ordinal))
            {
                var partes = codigo.Split(" - ", 2, StringSplitOptions.TrimEntries);
                if (partes.Length > 0 && !string.IsNullOrWhiteSpace(partes[0]))
                    codigo = partes[0];
            }

            var productoLocal = BuscarProductoLocal(codigo);
            if (productoLocal == null && !string.IsNullOrWhiteSpace(textoIngresado))
                productoLocal = BuscarProductoLocal(textoIngresado);

            CodigoComboBox.Text = string.Empty;
            CodigoComboBox.ItemsSource = null;
            CodigoComboBox.IsDropDownOpen = false;
            _indiceSugerencia = -1;
            _textoBusqueda = string.Empty;

            if (string.IsNullOrEmpty(codigo))
            {
                EnfocarCodigo();
                return;
            }

            if (productoLocal != null)
                _vm.AgregarDesdeBusquedaLocal(productoLocal);
            else
                await _vm.AgregarPorCodigoBarrasAsync(codigo);

            Dispatcher.BeginInvoke(new Action(EnfocarCodigo), DispatcherPriority.Input);
        }

        private async void AgregarActual_Click(object sender, RoutedEventArgs e) =>
            await AgregarDesdeEntradaAsync();

        private void ToolbarEditarCompra_Click(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Accion = "Editar líneas de compra";
            if (_vm.Items.Count == 0)
            {
                GrunflexDialogs.Show("No hay productos en la venta.");
                EnfocarCodigo();
                return;
            }

            VentasGrid.Focus();
            Keyboard.Focus(VentasGrid);
            if (VentasGrid.SelectedItem == null)
                VentasGrid.SelectedItem = _vm.Items[0];
        }

        private void ToolbarAplicarDescuento_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureDescuentos())
                return;

            SoporteContexto.Accion = "Aplicar descuento global";
            string raw = GrunflexDialogs.Input(
                "Ingrese descuento global (% entre 0 y 100):",
                "Aplicar descuento",
                "0");

            if (string.IsNullOrWhiteSpace(raw))
                return;

            raw = raw.Trim().Replace("%", "").Replace(",", ".");
            if (!decimal.TryParse(raw, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var porcentaje))
            {
                GrunflexDialogs.Show("Descuento inválido.");
                return;
            }

            if (porcentaje < 0 || porcentaje > 100)
            {
                GrunflexDialogs.Show("El descuento debe estar entre 0 y 100.");
                return;
            }

            _vm.AplicarDescuentoGlobal(porcentaje);
        }

        private void Buscar_Click(object sender, RoutedEventArgs e)
        {
            SoporteContexto.Accion = "Buscar producto manual";
            AbrirBusqueda();
        }

        private void Mayoreo_Click(object sender, RoutedEventArgs e)
        {
            // Pendiente de regla de precios mayoreo.
            GrunflexDialogs.Show("Modo mayoreo próximamente.");
        }

        private async void Entradas_Click(object sender, RoutedEventArgs e)
        {
            await RegistrarMovimientoCajaAsync("INGRESO", "Entrada de efectivo").ConfigureAwait(true);
        }

        private async void Salidas_Click(object sender, RoutedEventArgs e)
        {
            await RegistrarMovimientoCajaAsync("RETIRO", "Salida de efectivo").ConfigureAwait(true);
        }

        private async Task RegistrarMovimientoCajaAsync(string tipo, string titulo)
        {
            try
            {
                if (App.DbContext == null)
                {
                    GrunflexDialogs.Show("Base de datos no disponible.");
                    return;
                }

                var sesion = App.ObtenerSesionCajaAbiertaVisual();
                if (sesion == null)
                {
                    GrunflexDialogs.Show("No hay caja abierta.");
                    return;
                }

                string montoRaw = GrunflexDialogs.Input(
                    "Cantidad:",
                    titulo,
                    "0");

                if (string.IsNullOrWhiteSpace(montoRaw))
                    return;

                montoRaw = montoRaw.Trim().Replace("$", "").Replace(".", "").Replace(",", ".");
                if (!decimal.TryParse(montoRaw, System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var monto) || monto <= 0)
                {
                    GrunflexDialogs.Show("Monto inválido.");
                    return;
                }

                string descripcion = GrunflexDialogs.Input(
                    tipo == "INGRESO" ? "Comentarios:" : "Razón o proveedor:",
                    titulo,
                    tipo == "INGRESO" ? "Entrada de dinero" : "");

                if (MulticajaRuntime.UseApiOnlyClient)
                {
                    if (MulticajaRuntime.BlockMonetaryForInvalidConfig)
                    {
                        GrunflexDialogs.Show(
                            "Operación bloqueada: la configuración multicaja API-only no es válida o el bloqueo crítico offline está activo sin API.\n" +
                            "Revise appsettings / ProgramData y los logs (multicaja.validation).",
                            titulo,
                            MessageBoxButton.OK,
                            MessageBoxImage.Stop);
                        return;
                    }

                    if (App.UsuarioActual == null)
                    {
                        GrunflexDialogs.Show("No hay usuario logueado.");
                        return;
                    }

                    var cfg = AppConfig.Cargar();
                    if (cfg.MulticajaBlockCriticalWhenOffline &&
                        App.Connectivity?.State == ConnectivityState.Degraded)
                    {
                        GrunflexDialogs.Show(
                            "Conectividad degradada y Multicaja:BlockCriticalWhenOffline está activo; no se registra el movimiento.",
                            titulo,
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    var body = new MulticajaMovimientoCajaRequest
                    {
                        RequestId = Guid.NewGuid().ToString("N"),
                        CajaId = App.CajaActualId,
                        CajaSesionId = sesion.Id,
                        UsuarioId = App.UsuarioActual.Id,
                        Tipo = tipo,
                        Monto = monto,
                        Descripcion = descripcion ?? string.Empty
                    };

                    if (App.Connectivity?.State == ConnectivityState.Offline)
                    {
                        if (MulticajaOfflineEnqueue.TryEnqueue("multicaja-movimiento-caja", body, body.RequestId))
                        {
                            GrunflexDialogs.Show(
                                "Sin conexión con la API. Si la cola offline está habilitada, el movimiento quedó pendiente de envío.",
                                titulo,
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }
                        else
                        {
                            GrunflexDialogs.Show(
                                "Sin conexión con la API y la cola offline no está disponible o está deshabilitada.",
                                titulo,
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning);
                        }

                        return;
                    }

                    try
                    {
                        var resp = await MulticajaOperacionesClient.RegistrarMovimientoCajaAsync(body)
                            .ConfigureAwait(true);
                        if (resp == null || !resp.Ok)
                        {
                            GrunflexDialogs.Show(resp?.Error ?? "No se pudo registrar el movimiento en el servidor.");
                            return;
                        }

                        if (App.MulticajaSesionEnServidor != null && App.MulticajaSesionEnServidor.Id == sesion.Id)
                        {
                            App.MulticajaSesionEnServidor.TotalIngresos = resp.TotalIngresos;
                            App.MulticajaSesionEnServidor.TotalRetiros = resp.TotalRetiros;
                            App.MulticajaSesionEnServidor.TotalVentas = resp.TotalVentas;
                        }

                        GrunflexDialogs.Show($"{titulo} registrada correctamente.");
                    }
                    catch (HttpRequestException)
                    {
                        if (MulticajaOfflineEnqueue.TryEnqueue("multicaja-movimiento-caja", body, body.RequestId))
                            GrunflexDialogs.Show(
                                "Error de red. Si la cola offline está habilitada, el movimiento quedó pendiente de envío.");
                        else
                            GrunflexDialogs.Show("Error de red al registrar el movimiento.");
                    }
                    catch (TaskCanceledException)
                    {
                        if (MulticajaOfflineEnqueue.TryEnqueue("multicaja-movimiento-caja", body, body.RequestId))
                            GrunflexDialogs.Show(
                                "Timeout. Si la cola offline está habilitada, el movimiento quedó pendiente de envío.");
                        else
                            GrunflexDialogs.Show("Timeout al registrar el movimiento.");
                    }

                    return;
                }

                var cajaService = new CajaService(App.DbContext);
                cajaService.RegistrarMovimiento(sesion.Id, tipo, monto, descripcion ?? string.Empty);

                GrunflexDialogs.Show($"{titulo} registrada correctamente.");
            }
            catch (Exception ex)
            {
                GrunflexDialogs.Show($"No se pudo registrar el movimiento:\n{ex.Message}");
            }
        }

        private void BorrarSeleccion_Click(object sender, RoutedEventArgs e)
        {
            if (VentasGrid.SelectedItem is not VentaItem seleccionado)
            {
                GrunflexDialogs.Show("Seleccione un producto para borrar.");
                return;
            }

            var ok = GrunflexDialogs.Show(
                "¿Borrar producto seleccionado?",
                "Confirmar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (ok == MessageBoxResult.Yes)
                _vm.EliminarSeleccion(seleccionado);
        }

        public void AbrirBusquedaDesdeAtajo()
        {
            SoporteContexto.Accion = "Buscar producto manual";
            AbrirBusqueda();
        }

        private void AbrirBusqueda()
        {
            var ventana = new BuscarProductoWindow
            {
                Owner = Window.GetWindow(this)
            };

            if (ventana.ShowDialog() == true && ventana.ProductoSeleccionado != null)
                _vm.AgregarDesdeBusquedaLocal(ventana.ProductoSeleccionado);
        }

        private void VentasGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item != _ultimoProductoAgregado)
                return;

            var brush = new SolidColorBrush(Colors.White);
            e.Row.Background = brush;

            var animation = new ColorAnimation
            {
                From = Colors.LightGreen,
                To = Colors.White,
                Duration = new Duration(TimeSpan.FromMilliseconds(600))
            };

            brush.BeginAnimation(SolidColorBrush.ColorProperty, animation);
            _ultimoProductoAgregado = null;
        }

        private void VentasGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (VentasGrid.SelectedItem is not VentaItem seleccionado)
                return;

            if (e.Key == Key.Delete)
            {
                BorrarSeleccion_Click(sender, new RoutedEventArgs());
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Add || e.Key == Key.OemPlus)
            {
                _vm.IncrementarCantidad(seleccionado);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Subtract || e.Key == Key.OemMinus)
            {
                _vm.DisminuirCantidad(seleccionado);
                e.Handled = true;
            }
        }

        public void LimpiarVenta() => _vm.LimpiarVenta();
        public void AplicarDescuentoGlobal(decimal porcentaje) => _vm.AplicarDescuentoGlobal(porcentaje);

        public List<DetalleVenta> ObtenerItemsActuales() => _vm.ObtenerItemsActuales();

        public List<VentaItem> ObtenerLineasActuales() =>
            _vm.Items.Select(x => new VentaItem
            {
                CodigoBarras = x.CodigoBarras,
                Producto = x.Producto,
                Precio = x.Precio,
                Cantidad = x.Cantidad,
                Existencia = x.Existencia,
                DescuentoPorcentaje = x.DescuentoPorcentaje
            }).ToList();

        public decimal ObtenerTotal() => _vm.ObtenerTotal();
    }
}
