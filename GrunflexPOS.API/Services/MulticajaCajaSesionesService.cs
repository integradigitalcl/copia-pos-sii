using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

public sealed class MulticajaCajaSesionesService
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<MulticajaCajaSesionesService> _log;

    public MulticajaCajaSesionesService(PosCommerceDbContext db, ILogger<MulticajaCajaSesionesService> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<MulticajaCajaSesionDto?> ObtenerAbiertaAsync(Guid cajaId, CancellationToken ct)
    {
        var s = await _db.CajaSesiones.AsNoTracking()
            .FirstOrDefaultAsync(x => x.CajaId == cajaId && x.Abierta, ct);
        return s == null ? null : Map(s);
    }

    public async Task<MulticajaCajaSesionDto> AbrirAsync(MulticajaCajaSesionAbrirRequest req, CancellationToken ct)
    {
        if (await _db.CajaSesiones.AnyAsync(x => x.CajaId == req.CajaId && x.Abierta, ct))
            throw new InvalidOperationException("Ya hay una caja abierta para esta caja.");

        var caja = await _db.Cajas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == req.CajaId, ct)
                   ?? throw new InvalidOperationException("Caja no existe.");

        var usuarioExiste = await _db.Usuarios.AnyAsync(u => u.Id == req.UsuarioId, ct);
        if (!usuarioExiste)
            throw new InvalidOperationException("Usuario no existe.");

        var numeroCaja = await CalcularNumeroSecuencialAsync(req.CajaId, ct);
        var cajero = string.IsNullOrWhiteSpace(req.Username)
            ? (await _db.Usuarios.AsNoTracking().FirstAsync(u => u.Id == req.UsuarioId, ct)).Username
            : req.Username.Trim();

        var sesion = new CommerceCajaSesion
        {
            Id = Guid.NewGuid(),
            CajaId = req.CajaId,
            NumeroCaja = numeroCaja,
            Cajero = cajero,
            UsuarioAperturaId = req.UsuarioId,
            FechaApertura = DateTime.UtcNow,
            MontoApertura = req.MontoInicial,
            Abierta = true,
            TotalVentas = 0,
            TotalIngresos = 0,
            TotalRetiros = 0,
            Diferencia = 0
        };

        _db.CajaSesiones.Add(sesion);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("multicaja.sesion abierta id={Id} caja={Caja} numero={N}", sesion.Id, req.CajaId,
            numeroCaja);

        return Map(sesion);
    }

    private async Task<int> CalcularNumeroSecuencialAsync(Guid cajaId, CancellationToken ct)
    {
        try
        {
            var caja = await _db.Cajas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cajaId, ct);
            if (caja == null) return 1;

            var anteriores = await _db.Cajas.AsNoTracking()
                .CountAsync(c => c.FechaCreacion < caja.FechaCreacion
                                 || (c.FechaCreacion == caja.FechaCreacion &&
                                     string.Compare(c.Id.ToString(), caja.Id.ToString(), StringComparison.Ordinal) < 0),
                    ct);
            return anteriores + 1;
        }
        catch
        {
            return 1;
        }
    }

    private static MulticajaCajaSesionDto Map(CommerceCajaSesion s) =>
        new()
        {
            Id = s.Id,
            CajaId = s.CajaId,
            NumeroCaja = s.NumeroCaja,
            Cajero = s.Cajero,
            UsuarioAperturaId = s.UsuarioAperturaId,
            FechaApertura = s.FechaApertura,
            MontoApertura = s.MontoApertura,
            Abierta = s.Abierta,
            TotalVentas = s.TotalVentas,
            TotalIngresos = s.TotalIngresos,
            TotalRetiros = s.TotalRetiros
        };
}
