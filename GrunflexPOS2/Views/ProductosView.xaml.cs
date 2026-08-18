using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Security;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.Multicaja;
using GrunflexPOS2.Services.Multicaja.UI;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views
{
    public partial class ProductosView : UserControl
    {
        private readonly ConfiguracionService _cfg = new();
        private List<Producto> _productosCache = new();
        private ReactiveViewRefresh? _reactiveRefresh;

        public ProductosView()
        {
            InitializeComponent();
            Loaded += ProductosView_OnLoaded;
            Unloaded += ProductosView_OnUnloaded;
            CargarProductos();
        }

        public void RefrescarCatalogo() => _ = CargarProductosAsync(CancellationToken.None);

        private void ProductosView_OnLoaded(object sender, RoutedEventArgs e)
        {
            SetTabVisual(0);
            ActualizarTodosLosHints();
            AjustarAlturaGridProductos();

            if (MulticajaRuntime.UseApiOnlyClient && App.MulticajaRealtime != null)
            {
                _reactiveRefresh = new ReactiveViewRefresh(
                    this,
                    CargarProductosAsync,
                    App.MulticajaRealtime.Caches.Catalog,
                    App.MulticajaRealtime.Caches.Inventory);
                App.MulticajaRealtime.EventBus.CatalogChanged += OnBusCatalogChanged;
            }
        }

        private void ProductosView_OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (App.MulticajaRealtime != null)
                App.MulticajaRealtime.EventBus.CatalogChanged -= OnBusCatalogChanged;
            _reactiveRefresh?.Dispose();
            _reactiveRefresh = null;
        }

        private void OnBusCatalogChanged(object? sender, GrunflexPOS2.Services.Multicaja.Events.CatalogChangedEvent e) =>
            App.MulticajaRealtime?.Caches.Catalog.Invalidate("bus:catalog");

        private void ProductosView_SizeChanged(object sender, SizeChangedEventArgs e) =>
            AjustarAlturaGridProductos();

        private void AjustarAlturaGridProductos() =>
            PosGridHeightHelper.AjustarAlturaGrid(this, GridProductos, reservarSuperior: 120, minAltura: 180, maxFraccionViewport: 0.55);

        private void CampoProducto_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox tb)
                ActualizarHintPara(tb);
        }

        private void ActualizarTodosLosHints()
        {
            ActualizarHintPara(TxtCodigo);
            ActualizarHintPara(TxtDescripcion);
            ActualizarHintPara(TxtCosto);
            ActualizarHintPara(TxtVenta);
            ActualizarHintPara(TxtStock);
        }

        private void ActualizarHintPara(TextBox tb)
        {
            var vacio = string.IsNullOrWhiteSpace(tb.Text);
            var vis = vacio ? Visibility.Visible : Visibility.Collapsed;

            if (ReferenceEquals(tb, TxtCodigo))
                HintCodigo.Visibility = vis;
            else if (ReferenceEquals(tb, TxtDescripcion))
                HintDescripcion.Visibility = vis;
            else if (ReferenceEquals(tb, TxtCosto))
                HintCosto.Visibility = vis;
            else if (ReferenceEquals(tb, TxtVenta))
                HintVenta.Visibility = vis;
            else if (ReferenceEquals(tb, TxtStock))
                HintStock.Visibility = vis;
        }

        private void TabProducto_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out var idx))
                return;

            SetTabVisual(idx);

            if (idx == 1)
                MessageBox.Show(
                    "Seleccione un producto en la tabla para modificarlo.\n(Flujo de edición próximo.)",
                    "Modificar");
            else if (idx == 2)
                MessageBox.Show(
                    "Seleccione un producto en la tabla para eliminarlo.\n(Confirmación próxima.)",
                    "Eliminar");
        }

        private void SetTabVisual(int idx)
        {
            var active = (Style)FindResource("ProdTabActiveStyle");
            var inactive = (Style)FindResource("ProdTabInactiveStyle");

            BtnTabNuevo.Style = idx == 0 ? active : inactive;
            BtnTabModificar.Style = idx == 1 ? active : inactive;
            BtnTabEliminar.Style = idx == 2 ? active : inactive;
        }

        private void TxtBuscar_TextChanged(object sender, TextChangedEventArgs e) => AplicarFiltro();

        private void BtnFiltros_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Los filtros avanzados estarán disponibles próximamente.",
                "Filtros");
        }

        private void GridProductos_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void CmbPorPagina_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

        private void CargarProductos() => _ = CargarProductosAsync(CancellationToken.None);

        private async Task CargarProductosAsync(CancellationToken ct)
        {
            if (App.DbContext == null)
                return;

            ct.ThrowIfCancellationRequested();
            _productosCache = await App.DbContext.Productos
                .AsNoTracking()
                .OrderBy(p => p.Nombre)
                .ToListAsync(ct)
                .ConfigureAwait(true);
            AplicarFiltro();
        }

        private void AplicarFiltro()
        {
            var q = (TxtBuscar?.Text ?? string.Empty).Trim().ToLowerInvariant();

            IEnumerable<Producto> src = _productosCache;
            if (!string.IsNullOrEmpty(q))
            {
                src = _productosCache.Where(p =>
                    (p.Nombre ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    (p.CodigoBarras ?? string.Empty).ToLowerInvariant().Contains(q) ||
                    p.Id.ToString().Contains(q, StringComparison.OrdinalIgnoreCase));
            }

            GridProductos.ItemsSource = src.ToList();
        }

        private void BtnGuardar_Click(object sender, RoutedEventArgs e)
        {
            if (!UsuarioPermisosGate.EnsureProductos())
                return;

            try
            {
                if (string.IsNullOrWhiteSpace(TxtCodigo.Text) ||
                    string.IsNullOrWhiteSpace(TxtDescripcion.Text))
                {
                    MessageBox.Show("Debe completar código y descripción.");
                    return;
                }

                string costoTexto = TxtCosto.Text.Replace(".", "").Replace(",", "");
                string ventaTexto = TxtVenta.Text.Replace(".", "").Replace(",", "");
                string stockTexto = TxtStock.Text.Replace(".", "").Replace(",", "");

                if (!decimal.TryParse(costoTexto, out decimal costo))
                {
                    MessageBox.Show("Costo inválido");
                    return;
                }

                if (!decimal.TryParse(ventaTexto, out decimal precio))
                {
                    MessageBox.Show("Precio inválido");
                    return;
                }

                if (_cfg.GetCalcularMargenAutomatico())
                {
                    var margen = _cfg.GetMargenPorcentaje();
                    precio = costo * (1m + (margen / 100m));
                }

                if (_cfg.GetRedondeoHabilitado())
                    precio = AplicarRedondeo(precio, _cfg.GetModoRedondeo());

                if (!int.TryParse(stockTexto, out int stock))
                {
                    MessageBox.Show("Stock inválido");
                    return;
                }

                var producto = new Producto
                {
                    CodigoBarras = TxtCodigo.Text.Trim(),
                    Nombre = TxtDescripcion.Text.Trim(),
                    Costo = costo,
                    Precio = precio,
                    Stock = stock
                };

                App.DbContext!.Productos.Add(producto);
                App.DbContext.SaveChanges();

                MessageBox.Show("Producto guardado");

                Limpiar();
                CargarProductos();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error:\n" + ex.Message);
            }
        }

        private void Limpiar()
        {
            TxtCodigo.Text = "";
            TxtDescripcion.Text = "";
            TxtCosto.Text = "0";
            TxtVenta.Text = "0";
            TxtStock.Text = "0";

            ActualizarTodosLosHints();
            TxtDescripcion.Focus();
        }

        private static decimal AplicarRedondeo(decimal precio, string modo)
        {
            return (modo ?? string.Empty).Trim().ToLower() switch
            {
                "a unidades" => Math.Round(precio, 0, MidpointRounding.AwayFromZero),
                "a 50" => Math.Round(precio / 50m, 0, MidpointRounding.AwayFromZero) * 50m,
                "a 100" => Math.Round(precio / 100m, 0, MidpointRounding.AwayFromZero) * 100m,
                _ => Math.Round(precio, 1, MidpointRounding.AwayFromZero)
            };
        }
    }
}
