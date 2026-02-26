using System.ComponentModel;

namespace GrunflexPOS2.Models
{
    public class ProductoVenta : INotifyPropertyChanged
    {
        private int _cantidad;
        private decimal _precio;

        public string CodigoBarras { get; set; } = string.Empty;
        public string Producto { get; set; } = string.Empty;

        public int Cantidad
        {
            get => _cantidad;
            set
            {
                _cantidad = value;
                OnPropertyChanged(nameof(Cantidad));
                OnPropertyChanged(nameof(Importe));
            }
        }

        public decimal Precio
        {
            get => _precio;
            set
            {
                _precio = value;
                OnPropertyChanged(nameof(Precio));
                OnPropertyChanged(nameof(Importe));
            }
        }

        public decimal Importe => Cantidad * Precio;

        public int Existencia { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}