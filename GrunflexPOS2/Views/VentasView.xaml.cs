using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using GrunflexPOS2.Models;
using System.Collections.Generic;

namespace GrunflexPOS2.Views
{
    public partial class VentasView : UserControl
    {
        private ObservableCollection<VentaItem> _itemsVenta;
        private VentaItem? _ultimoProductoAgregado;

        public event Action<decimal, int>? ResumenActualizado;

        public VentasView()
        {
            InitializeComponent();

            _itemsVenta = new ObservableCollection<VentaItem>();
            VentasGrid.ItemsSource = _itemsVenta;

            Loaded += VentasView_Loaded;
        }

        private void VentasView_Loaded(object sender, RoutedEventArgs e)
        {
            CodigoTextBox.Focus();
        }

        // ================= ENTER (PISTOLA) =================
        private void CodigoTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                string codigo = CodigoTextBox.Text.Trim();

                if (!string.IsNullOrEmpty(codigo))
                {
                    ProcesarCodigoBarras(codigo);
                }

                CodigoTextBox.Clear();
                CodigoTextBox.Focus();
            }
        }

        // ================= VALIDACIÓN =================
        private void ProcesarCodigoBarras(string codigo)
        {
            var producto = BuscarProductoPorCodigo(codigo);

            if (producto == null)
            {
                MessageBox.Show(
                    "El producto no existe en el sistema.",
                    "Producto no encontrado",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            AgregarProducto(producto);
        }

        // ================= BASE SIMULADA =================
        private Producto? BuscarProductoPorCodigo(string codigo)
        {
            var productos = new[]
            {
                new Producto { Id = 123, Nombre = "Mouse Logitech G203", Precio = 9990, Stock = 3 },
                new Producto { Id = 456, Nombre = "Teclado Redragon", Precio = 19990, Stock = 5 },
                new Producto { Id = 789, Nombre = "Audífonos Gamer", Precio = 14990, Stock = 2 }
            };

            return productos.FirstOrDefault(p => p.Id.ToString() == codigo);
        }

        private void Buscar_Click(object sender, RoutedEventArgs e)
        {
            AbrirBusqueda();
        }

        public void AbrirBusquedaDesdeAtajo()
        {
            AbrirBusqueda();
        }

        private void AbrirBusqueda()
        {
            var ventana = new BuscarProductoWindow();
            ventana.Owner = Window.GetWindow(this);

            if (ventana.ShowDialog() == true && ventana.ProductoSeleccionado != null)
            {
                public Producto? ProductoSeleccionado { get; set; }
            }
        }

        // ================= AGREGAR PRODUCTO =================
        private void AgregarProducto(Producto producto)
        {
            var existente = _itemsVenta
                .FirstOrDefault(x => x.CodigoBarras == producto.Id.ToString());

            if (existente != null)
            {
                if (existente.Cantidad >= existente.Existencia)
                {
                    MessageBox.Show(
                        "No hay suficiente stock disponible.",
                        "Stock insuficiente",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                existente.Cantidad++;
            }
            else
            {
                if (producto.Stock <= 0)
                {
                    MessageBox.Show(
                        "El producto no tiene stock disponible.",
                        "Sin stock",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var nuevo = new VentaItem
                {
                    CodigoBarras = producto.Id.ToString(),
                    Producto = producto.Nombre,
                    Precio = producto.Precio,
                    Existencia = producto.Stock,
                    Cantidad = 1
                };

                SuscribirseCambios(nuevo);

                _ultimoProductoAgregado = nuevo;
                _itemsVenta.Add(nuevo);
            }

            CalcularResumen();
        }

        // ================= ANIMACIÓN VERDE =================
        private void VentasGrid_LoadingRow(object sender, DataGridRowEventArgs e)
        {
            if (e.Row.Item == _ultimoProductoAgregado)
            {
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
        }

        private void VentasGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete)
            {
                if (VentasGrid.SelectedItem is VentaItem seleccionado)
                {
                    _itemsVenta.Remove(seleccionado);
                    CalcularResumen();
                }
            }
        }

        private void SuscribirseCambios(VentaItem item)
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(VentaItem.Cantidad))
                {
                    if (item.Cantidad > item.Existencia)
                    {
                        MessageBox.Show(
                            "Cantidad supera la existencia disponible.",
                            "Stock insuficiente",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);

                        item.Cantidad = item.Existencia;
                    }

                    CalcularResumen();
                }
            };
        }

        // ================= LIMPIAR VENTA =================
        public void LimpiarVenta()
        {
            _itemsVenta.Clear();
            CalcularResumen();
        }

        // 🔥 PARA HISTORIAL Y PDF (ESTO ESTÁ BIEN DISEÑADO)
        public List<DetalleVenta> ObtenerItemsActuales()
        {
            return _itemsVenta
                .Select(x => new DetalleVenta
                {
                    Producto = x.Producto,
                    Cantidad = x.Cantidad,
                    Precio = x.Precio
                })
                .ToList();
        }

        // ================= RESUMEN =================
        private void CalcularResumen()
        {
            decimal subtotal = _itemsVenta.Sum(x => x.Importe);
            int articulos = _itemsVenta.Sum(x => x.Cantidad);

            ResumenActualizado?.Invoke(subtotal, articulos);
        }
    }
}
