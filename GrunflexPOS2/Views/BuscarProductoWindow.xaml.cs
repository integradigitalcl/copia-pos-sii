using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GrunflexPOS2.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS2.Views
{
    public partial class BuscarProductoWindow : Window
    {
        public Producto? ProductoSeleccionado { get; private set; }

        private List<Producto> _productos = new();
        private bool _autocompletando;

        public BuscarProductoWindow()
        {
            InitializeComponent();
            Loaded += BuscarProductoWindow_Loaded;
        }

        private void BuscarProductoWindow_Loaded(object sender, RoutedEventArgs e)
        {
            CargarProductos();
            AplicarFiltro();
            txtBuscar.Focus();
        }

        private void CargarProductos()
        {
            if (App.DbContext == null)
            {
                _productos = new List<Producto>();
                dgProductos.ItemsSource = _productos;
                return;
            }

            _productos = App.DbContext.Productos
                .AsNoTracking()
                .OrderBy(p => p.Nombre)
                .ToList();
        }

        private static bool Coincide(Producto p, string texto)
        {
            if (string.IsNullOrWhiteSpace(texto))
                return true;

            var q = texto.Trim();
            return (p.Nombre ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase)
                   || (p.CodigoBarras ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase);
        }

        private void AplicarFiltro()
        {
            var q = txtBuscar.Text.Trim();
            var filtrados = _productos.Where(p => Coincide(p, q)).ToList();
            dgProductos.ItemsSource = filtrados;
            if (filtrados.Count > 0)
                dgProductos.SelectedIndex = 0;
        }

        private void IntentarAutocompletarInline()
        {
            if (_autocompletando)
                return;

            var prefijo = txtBuscar.Text;
            if (string.IsNullOrWhiteSpace(prefijo))
                return;

            if (txtBuscar.SelectionLength > 0 && txtBuscar.SelectionStart < prefijo.Length)
                return;

            var sugerencia = _productos.FirstOrDefault(p =>
            {
                var nombre = p.Nombre ?? string.Empty;
                var codigo = p.CodigoBarras ?? string.Empty;
                return nombre.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)
                       || codigo.StartsWith(prefijo, StringComparison.OrdinalIgnoreCase);
            });

            if (sugerencia == null)
                return;

            var sugerido = (sugerencia.CodigoBarras ?? string.Empty).StartsWith(prefijo, StringComparison.OrdinalIgnoreCase)
                ? (sugerencia.CodigoBarras ?? string.Empty)
                : (sugerencia.Nombre ?? string.Empty);

            if (sugerido.Length <= prefijo.Length)
                return;

            _autocompletando = true;
            txtBuscar.Text = sugerido;
            txtBuscar.SelectionStart = prefijo.Length;
            txtBuscar.SelectionLength = sugerido.Length - prefijo.Length;
            _autocompletando = false;
        }

        private void BtnBuscar_Click(object sender, RoutedEventArgs e)
        {
            AplicarFiltro();
            IntentarAutocompletarInline();
        }

        private void TxtBuscar_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_autocompletando)
                return;
            AplicarFiltro();
            IntentarAutocompletarInline();
        }

        private void TxtBuscar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                BtnSeleccionar_Click(sender, e);
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                BtnCancelar_Click(sender, e);
                e.Handled = true;
            }
        }

        private void BtnSeleccionar_Click(object sender, RoutedEventArgs e)
        {
            if (dgProductos.SelectedItem is Producto producto)
            {
                ProductoSeleccionado = producto;
                DialogResult = true;
            }
        }

        private void BtnCancelar_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}