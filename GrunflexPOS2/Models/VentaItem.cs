using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GrunflexPOS2.Models
{
    public class VentaItem : INotifyPropertyChanged
    {
        private int _cantidad = 1;
        private decimal _descuentoPorcentaje;
        private decimal _precio;

        public string CodigoBarras { get; set; } = "";
        public string Producto { get; set; } = "";

        public decimal Precio
        {
            get => _precio;
            set
            {
                if (_precio == value)
                    return;
                _precio = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Importe));
            }
        }

        public int Existencia { get; set; }

        /// <summary>0–100. El importe usa precio de lista menos este porcentaje.</summary>
        public decimal DescuentoPorcentaje
        {
            get => _descuentoPorcentaje;
            set
            {
                if (value < 0)
                    value = 0;
                if (value > 100)
                    value = 100;
                if (_descuentoPorcentaje == value)
                    return;
                _descuentoPorcentaje = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Importe));
            }
        }

        public int Cantidad
        {
            get => _cantidad;
            set
            {
                if (value < 1)
                    value = 1;

                if (_cantidad == value)
                    return;

                _cantidad = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Importe));
            }
        }

        /// <summary>Importe de línea con descuento aplicado (persistencia como precio neto en detalle).</summary>
        public decimal Importe =>
            Math.Round(Precio * Cantidad * (1m - DescuentoPorcentaje / 100m), 2, MidpointRounding.AwayFromZero);

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
