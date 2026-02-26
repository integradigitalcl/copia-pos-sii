using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GrunflexPOS2.Models
{
    public class VentaItem : INotifyPropertyChanged
    {
        private int _cantidad;

        public string CodigoBarras { get; set; } = "";
        public string Producto { get; set; } = "";
        public decimal Precio { get; set; }
        public int Existencia { get; set; }

        public int Cantidad
        {
            get => _cantidad;
            set
            {
                if (value < 1)
                    value = 1;

                _cantidad = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Importe));
            }
        }

        public decimal Importe => Precio * Cantidad;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
