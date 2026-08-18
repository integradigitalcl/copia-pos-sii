using GrunflexPOS2.Data;
using GrunflexPOS2.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

var targetCount = 1000;
if (args.Length > 0 && int.TryParse(args[0], out var countArg) && countArg > 0)
    targetCount = countArg;

var cfg = new ConfigurationBuilder()
    .SetBasePath(Path.Combine(Directory.GetCurrentDirectory(), "GrunflexPOS2"))
    .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
    .Build();

var connectionString = cfg["ConnectionStrings:Default"];
if (string.IsNullOrWhiteSpace(connectionString))
{
    LocalDatabasePaths.EnsureDataDirectoryExists();
    connectionString = LocalDatabasePaths.DefaultConnectionString;
}

var options = new DbContextOptionsBuilder<GrunflexDbContext>()
    .UseSqlite(connectionString)
    .Options;

using var db = new GrunflexDbContext(options);

var user = db.Usuarios.FirstOrDefault();
if (user is null)
{
    user = new Usuario
    {
        Id = Guid.NewGuid(),
        Nombre = "Seeder QA",
        Username = "seeder",
        Rol = "Administrador",
        Password = "seed"
    };
    db.Usuarios.Add(user);
    db.SaveChanges();
}

var empresa = db.Empresas.FirstOrDefault();
if (empresa is null)
{
    empresa = new Empresa
    {
        Id = Guid.NewGuid(),
        Nombre = "Empresa Seeder"
    };
    db.Empresas.Add(empresa);
    db.SaveChanges();
}

var caja = db.Cajas.FirstOrDefault();
if (caja is null)
{
    caja = new Caja
    {
        Id = Guid.NewGuid(),
        Nombre = "Caja 1",
        EmpresaId = empresa.Id,
        Activa = true
    };
    db.Cajas.Add(caja);
    db.SaveChanges();
}

var sesion = db.CajaSesiones.FirstOrDefault(x => x.Abierta && x.CajaId == caja.Id);
if (sesion is null)
{
    sesion = new CajaSesion
    {
        Id = Guid.NewGuid(),
        CajaId = caja.Id,
        NumeroCaja = 1,
        Cajero = user.Username,
        UsuarioAperturaId = user.Id,
        FechaApertura = DateTime.UtcNow.AddDays(-30),
        MontoApertura = 100000,
        TotalVentas = 0,
        TotalIngresos = 0,
        TotalRetiros = 0,
        Diferencia = 0,
        Abierta = true
    };
    db.CajaSesiones.Add(sesion);
    db.SaveChanges();
}

var random = new Random();
var metodos = new[] { "Efectivo", "Tarjeta", "Transferencia", "Mixto" };
var nombresProductos = new[]
{
    "Bebida Cola 350ml",
    "Galletas Chocolate",
    "Pan Molde",
    "Leche Entera 1L",
    "Arroz 1Kg",
    "Queso Laminado",
    "Yogurt Natural",
    "Café Instantáneo",
    "Azúcar 1Kg",
    "Aceite 1L"
};

var startTicket = (db.Ventas.Max(v => (int?)v.NumeroTicket) ?? 0) + 1;
var now = DateTime.UtcNow;
var ventas = new List<VentaEntity>(targetCount);
var detalles = new List<DetalleVenta>(targetCount * 3);

for (var i = 0; i < targetCount; i++)
{
    var fecha = now.AddDays(-random.Next(0, 30)).AddMinutes(-random.Next(0, 1440));
    var cantidadItems = random.Next(1, 5);

    var venta = new VentaEntity
    {
        Id = Guid.NewGuid(),
        NumeroTicket = startTicket + i,
        Fecha = fecha,
        NumeroCaja = sesion.NumeroCaja,
        CajaId = caja.Id,
        Cajero = user.Username,
        Cliente = "Público en general",
        MetodoPago = metodos[random.Next(metodos.Length)],
        EstaAnulada = false,
        UsuarioId = user.Id,
        CajaSesionId = sesion.Id
    };

    decimal total = 0;
    for (var j = 0; j < cantidadItems; j++)
    {
        var cant = random.Next(1, 4);
        var precio = random.Next(900, 12000);
        total += cant * precio;

        detalles.Add(new DetalleVenta
        {
            Id = Guid.NewGuid(),
            VentaId = venta.Id,
            Producto = nombresProductos[random.Next(nombresProductos.Length)],
            Cantidad = cant,
            Precio = precio
        });
    }

    venta.Total = total;
    ventas.Add(venta);
}

db.Ventas.AddRange(ventas);
db.DetalleVentas.AddRange(detalles);
sesion.TotalVentas += ventas.Sum(x => x.Total);
db.SaveChanges();

Console.WriteLine($"OK: insertadas {ventas.Count} ventas y {detalles.Count} detalles.");
Console.WriteLine($"Tickets: {startTicket}..{startTicket + targetCount - 1}");
