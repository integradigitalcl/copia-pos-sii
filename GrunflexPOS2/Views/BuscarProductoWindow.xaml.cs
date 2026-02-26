using System.Collections.ObjectModel;
using System.Windows;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Views
{
    public partial class BuscarProductoWindow : Window
    {
        // 🔥 Usa Producto (Inventario)
        public Producto? ProductoSeleccionado { get; private set; }

        private ObservableCollection<Producto> _productos;

        public BuscarProductoWindow()
        {
            InitializeComponent();

            // 🔥 Datos de prueba simulando inventario
            _productos = new ObservableCollection<Producto>
            {
                new Producto { Id = 1001, Nombre = "SSD 240GB", Precio = 18990, Stock = 4 },
                new Producto { Id = 1002, Nombre = "RAM 8GB DDR4", Precio = 25990, Stock = 6 },
                new Producto { Id = 1003, Nombre = "Fuente 500W 500W", Precio = 32990, Stock = 2 }
            };

            // 🔥 Enlazamos al DataGrid
            dgProductos.ItemsSource = _productos;
        }

        private void BtnBuscar_Click(object sender, RoutedEventArgs e)
        {
            // Aquí luego podemos implementar filtro real
            MessageBox.Show("Buscar ejecutado");
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
