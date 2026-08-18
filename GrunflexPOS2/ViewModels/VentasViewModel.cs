using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using GrunflexPOS2.Models;
using GrunflexPOS2.Models.Entities;
using GrunflexPOS2.Services;
using GrunflexPOS2.Services.DTOs;

namespace GrunflexPOS2.ViewModels;

/// <summary>Lógica de venta y carrito (sin acceso a base de datos desde la UI).</summary>
public sealed class VentasViewModel : INotifyPropertyChanged
{
    private readonly IProductoLookupService _productos;
    private readonly ConfiguracionService _cfg = new();
    private readonly object _scanGate = new();
    private DispatcherTimer? _timerEstado;
    private string _mensajeEstado = "";
    private bool _mensajeEsError;
    private bool _escaneoEnCurso;

    public VentasViewModel(IProductoLookupService productos)
    {
        _productos = productos;
        Items = new ObservableCollection<VentaItem>();
    }

    public ObservableCollection<VentaItem> Items { get; }

    /// <summary>subtotal lista (Σ precio×cant.), monto descuento (lista − neto), total neto (Σ importe), piezas.</summary>
    public event Action<decimal, decimal, decimal, int>? ResumenActualizado;
    public event Action<VentaItem>? ProductoAgregado;

    public string MensajeEstado
    {
        get => _mensajeEstado;
        private set
        {
            if (_mensajeEstado == value)
                return;
            _mensajeEstado = value;
            OnPropertyChanged();
        }
    }

    public bool MensajeEsError
    {
        get => _mensajeEsError;
        private set
        {
            if (_mensajeEsError == value)
                return;
            _mensajeEsError = value;
            OnPropertyChanged();
        }
    }

    public bool EscaneoEnCurso
    {
        get => _escaneoEnCurso;
        private set
        {
            if (_escaneoEnCurso == value)
                return;
            _escaneoEnCurso = value;
            OnPropertyChanged();
        }
    }

    public async Task AgregarPorCodigoBarrasAsync(string codigo)
    {
        lock (_scanGate)
        {
            if (_escaneoEnCurso)
                return;
            EscaneoEnCurso = true;
        }

        try
        {
            SoporteContexto.Accion = "Escanear producto";

            ProductoPosDto? dto;
            try
            {
                dto = await _productos.ObtenerPorCodigoBarrasAsync(codigo).ConfigureAwait(true);
            }
            catch (HttpRequestException ex)
            {
                PosDiagnostics.Log("Red/API productos", ex);
                MostrarEstado("Error de conexión", esError: true);
                return;
            }
            catch (TaskCanceledException ex)
            {
                PosDiagnostics.Log("Timeout API productos", ex);
                MostrarEstado("Error de conexión", esError: true);
                return;
            }
            catch (Exception ex)
            {
                PosDiagnostics.Log("API productos", ex);
                MostrarEstado("Error de conexión", esError: true);
                return;
            }

            if (dto == null)
            {
                SoporteContexto.Error = "Producto no encontrado";
                MostrarEstado("Producto no encontrado", esError: true);
                return;
            }

            AgregarOIncrementar(dto);
        }
        finally
        {
            EscaneoEnCurso = false;
        }
    }

    /// <summary>Búsqueda manual (ventana) hasta migrar a API.</summary>
    public void AgregarDesdeBusquedaLocal(Producto producto)
    {
        SoporteContexto.Accion = "Agregar producto";
        var dto = new ProductoPosDto
        {
            Id = producto.Id,
            CodigoBarras = producto.CodigoBarras,
            Nombre = producto.Nombre,
            Precio = producto.Precio,
            Stock = producto.Stock
        };
        AgregarOIncrementar(dto);
    }

    private void AgregarOIncrementar(ProductoPosDto dto)
    {
        SoporteContexto.Producto = dto.Nombre;
        SoporteContexto.Accion = "Agregar producto";

        var existente = Items.FirstOrDefault(x => x.CodigoBarras == dto.CodigoBarras);

        if (existente != null)
        {
            bool usarInventario = _cfg.GetUsarInventario();
            if (usarInventario && existente.Cantidad >= existente.Existencia)
            {
                SoporteContexto.Error = "Stock insuficiente";
                MostrarEstado("Stock insuficiente", esError: true);
                return;
            }

            existente.Cantidad++;
        }
        else
        {
            bool usarInventario = _cfg.GetUsarInventario();
            if (usarInventario && dto.Stock <= 0)
            {
                SoporteContexto.Error = "Producto sin stock";
                MostrarEstado("Producto sin stock", esError: true);
                return;
            }

            var nuevo = new VentaItem
            {
                CodigoBarras = dto.CodigoBarras,
                Producto = dto.Nombre,
                Precio = dto.Precio,
                Existencia = usarInventario ? dto.Stock : int.MaxValue,
                Cantidad = 1
            };

            SuscribirCambiosLinea(nuevo);
            Items.Add(nuevo);
            ProductoAgregado?.Invoke(nuevo);
        }

        NotificarResumen();
    }

    private void SuscribirCambiosLinea(VentaItem item)
    {
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VentaItem.Cantidad) && item.Cantidad > item.Existencia)
            {
                if (_cfg.GetUsarInventario())
                {
                    SoporteContexto.Error = "Cantidad supera stock";
                    MostrarEstado("Cantidad supera stock", esError: true);
                    item.Cantidad = item.Existencia;
                    return;
                }
            }

            if (e.PropertyName is nameof(VentaItem.Cantidad)
                or nameof(VentaItem.DescuentoPorcentaje)
                or nameof(VentaItem.Precio))
                NotificarResumen();
        };
    }

    public void EliminarSeleccion(VentaItem? item)
    {
        if (item == null)
            return;

        Items.Remove(item);
        SoporteContexto.Accion = "Eliminar producto";
        NotificarResumen();
    }

    public void IncrementarCantidad(VentaItem? item)
    {
        if (item == null)
            return;

        if (item.Cantidad >= item.Existencia)
        {
            SoporteContexto.Error = "Stock insuficiente";
            MostrarEstado("Stock insuficiente", esError: true);
            return;
        }

        item.Cantidad++;
        SoporteContexto.Accion = "Aumentar cantidad";
        NotificarResumen();
    }

    public void DisminuirCantidad(VentaItem? item)
    {
        if (item == null)
            return;

        if (item.Cantidad <= 1)
        {
            EliminarSeleccion(item);
            return;
        }

        item.Cantidad--;
        SoporteContexto.Accion = "Disminuir cantidad";
        NotificarResumen();
    }

    public void LimpiarVenta()
    {
        Items.Clear();
        SoporteContexto.Accion = "Limpiar venta";
        SoporteContexto.Producto = "";
        NotificarResumen();
    }

    public void AplicarDescuentoGlobal(decimal porcentaje)
    {
        if (porcentaje < 0)
            porcentaje = 0;
        if (porcentaje > 100)
            porcentaje = 100;

        foreach (var item in Items)
            item.DescuentoPorcentaje = porcentaje;

        SoporteContexto.Accion = "Aplicar descuento global";
        NotificarResumen();
    }

    public List<DetalleVenta> ObtenerItemsActuales()
    {
        return Items
            .Select(x =>
            {
                var precioNetoUnitario = x.Cantidad > 0 ? x.Importe / x.Cantidad : 0m;
                return new DetalleVenta
                {
                    CodigoBarras = string.IsNullOrWhiteSpace(x.CodigoBarras) ? null : x.CodigoBarras.Trim(),
                    Producto = x.Producto,
                    Cantidad = x.Cantidad,
                    Precio = precioNetoUnitario
                };
            })
            .ToList();
    }

    public decimal ObtenerTotal() => Items.Sum(x => x.Importe);

    private void NotificarResumen()
    {
        var subtotalLista = Items.Sum(x => Math.Round(x.Precio * x.Cantidad, 2, MidpointRounding.AwayFromZero));
        var totalNeto = Items.Sum(x => x.Importe);
        var montoDescuento = Math.Round(subtotalLista - totalNeto, 2, MidpointRounding.AwayFromZero);
        if (montoDescuento < 0)
            montoDescuento = 0;

        var articulos = Items.Sum(x => x.Cantidad);
        SoporteContexto.Venta = totalNeto;
        ResumenActualizado?.Invoke(subtotalLista, montoDescuento, totalNeto, articulos);
    }

    private void MostrarEstado(string mensaje, bool esError)
    {
        MensajeEsError = esError;
        MensajeEstado = mensaje;

        _timerEstado?.Stop();
        if (string.IsNullOrEmpty(mensaje))
            return;

        _timerEstado = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _timerEstado.Tick += (_, _) =>
        {
            _timerEstado?.Stop();
            MensajeEstado = "";
        };
        _timerEstado.Start();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
