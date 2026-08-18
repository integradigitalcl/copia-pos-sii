using System.Collections.Generic;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Domain.Repositories;

public interface IVentaRepository
{
    List<Venta> ObtenerVentas();
    void GuardarVentas(List<Venta> ventas);
}
