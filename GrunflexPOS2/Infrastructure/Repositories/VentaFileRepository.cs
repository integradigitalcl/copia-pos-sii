using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using GrunflexPOS2.Domain.Repositories;
using GrunflexPOS2.Models;

namespace GrunflexPOS2.Infrastructure.Repositories;

public sealed class VentaFileRepository : IVentaRepository
{
    private readonly string _rutaArchivo;

    public VentaFileRepository(string rutaArchivo)
    {
        _rutaArchivo = rutaArchivo;
    }

    public List<Venta> ObtenerVentas()
    {
        try
        {
            if (!File.Exists(_rutaArchivo))
                return new List<Venta>();

            var json = File.ReadAllText(_rutaArchivo);
            return JsonSerializer.Deserialize<List<Venta>>(json) ?? new List<Venta>();
        }
        catch
        {
            return new List<Venta>();
        }
    }

    public void GuardarVentas(List<Venta> ventas)
    {
        try
        {
            var carpeta = Path.GetDirectoryName(_rutaArchivo);
            if (!string.IsNullOrWhiteSpace(carpeta) && !Directory.Exists(carpeta))
                Directory.CreateDirectory(carpeta);

            var json = JsonSerializer.Serialize(ventas, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_rutaArchivo, json);
        }
        catch
        {
            // No romper flujo principal por almacenamiento auxiliar
        }
    }
}
