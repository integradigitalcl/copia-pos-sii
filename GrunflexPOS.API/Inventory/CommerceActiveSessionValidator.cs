using GrunflexPOS.API.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Inventory;

/// <summary>Validación de sesión/caja contra DB viva (no cache UI).</summary>
public sealed class CommerceActiveSessionValidator
{
    private readonly PosCommerceDbContext _db;
    private readonly ILogger<CommerceActiveSessionValidator> _log;

    public CommerceActiveSessionValidator(PosCommerceDbContext db, ILogger<CommerceActiveSessionValidator> log)
    {
        _db = db;
        _log = log;
    }

    public async Task<(bool Ok, string Code, string Message)> ValidateCajaSessionFreshAsync(
        Guid cajaId,
        Guid cajaSesionId,
        Guid usuarioId,
        Guid? terminalId,
        CancellationToken ct)
    {
        var sesion = await _db.CajaSesiones
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == cajaSesionId, ct);

        if (sesion == null)
        {
            _log.LogWarning("inventory.session missing sesionId={S}", cajaSesionId);
            return (false, "SESION_INVALIDA", "Sesión de caja no encontrada.");
        }

        if (!sesion.Abierta)
        {
            _log.LogWarning("inventory.session closed sesionId={S}", cajaSesionId);
            return (false, "SESION_CERRADA", "La sesión de caja está cerrada.");
        }

        if (sesion.CajaId != cajaId)
        {
            _log.LogWarning(
                "inventory.session caja mismatch sesion={S} sesionCaja={Sc} reqCaja={Rc}",
                cajaSesionId, sesion.CajaId, cajaId);
            return (false, "CAJA_MISMATCH", "La sesión no pertenece a la caja indicada.");
        }

        var usuarioOk = await _db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == usuarioId, ct);
        if (!usuarioOk)
            return (false, "USUARIO_INVALIDO", "Usuario no existe en el servidor.");

        if (terminalId.HasValue && terminalId.Value != Guid.Empty)
        {
            _log.LogDebug(
                "inventory.session terminal={T} caja={C} sesion={S}",
                terminalId, cajaId, cajaSesionId);
        }

        return (true, "", "");
    }
}
