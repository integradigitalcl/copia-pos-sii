using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

/// <summary>
/// Endpoints de endurecimiento de licencia server-side (Fase 5.1).
///
/// - <c>POST /api/terminals/register</c>: registra una caja contra la API local. Es idempotente
///   por <c>MachineFingerprint</c>. Verifica que no se supere <c>NumberOfBoxes</c> de la licencia.
/// - <c>POST /api/terminals/heartbeat</c>: mantiene viva la terminal. Marca <c>LastHeartbeatUtc</c>.
///   El POS llama cada 5 minutos. Cajas inactivas por &gt;7 días se consideran liberadas.
/// - <c>GET /api/terminals</c>: lista para soporte/admin.
/// - <c>DELETE /api/terminals/{id}</c>: libera slot manualmente.
///
/// Se acepta anonymous porque el POS aún no tiene credenciales propias; el control real
/// está en el modelo de despliegue: SOLO accesible en la LAN privada del negocio.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class TerminalsController : ControllerBase
{
    private static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(7);
    private readonly ApiDbContext _db;
    private readonly LicenseSlotService _licenseSlots;
    private readonly ILogger<TerminalsController> _log;

    public TerminalsController(ApiDbContext db, LicenseSlotService licenseSlots, ILogger<TerminalsController> log)
    {
        _db = db;
        _licenseSlots = licenseSlots;
        _log = log;
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(TerminalRegisterResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TerminalRegisterResponse>> Register(
        [FromBody] TerminalRegisterRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MachineFingerprint))
            return BadRequest(new { error = "MachineFingerprint requerido." });

        var now = DateTime.UtcNow;
        var ip = ResolveClientIp();

        var existing = await _db.TerminalRegistrations
            .FirstOrDefaultAsync(t => t.MachineFingerprint == req.MachineFingerprint, ct);

        if (existing != null)
        {
            existing.Active = true;
            existing.LastHeartbeatUtc = now;
            existing.Version = req.Version ?? string.Empty;
            existing.MachineName = req.MachineName ?? existing.MachineName;
            existing.ActivationId = req.ActivationId ?? existing.ActivationId;
            if (!string.IsNullOrWhiteSpace(ip))
                existing.IpAddress = ip;
            await _db.SaveChangesAsync(ct);

            var (used, max) = await _licenseSlots.GetUnifiedSlotUsageAsync(req.ActivationId, req.MachineFingerprint, ct: ct);
            return Ok(new TerminalRegisterResponse
            {
                Granted = true,
                SlotsInUse = used,
                SlotsTotal = max,
                TerminalId = existing.Id
            });
        }

        if (!await _licenseSlots.CanRegisterNewTerminalAsync(req.MachineFingerprint, req.ActivationId, ct))
        {
            var (used, max) = await _licenseSlots.GetUnifiedSlotUsageAsync(req.ActivationId, req.MachineFingerprint, ct: ct);
            _log.LogWarning("Terminal denied: {fp} (slots {used}/{max})", req.MachineFingerprint, used, max);
            return Ok(new TerminalRegisterResponse
            {
                Granted = false,
                Reason = LicenseSlotService.MensajeLimiteCajas(max),
                SlotsInUse = used,
                SlotsTotal = max
            });
        }

        var (usedSlots, maxSlots) = await _licenseSlots.GetUnifiedSlotUsageAsync(req.ActivationId, req.MachineFingerprint, ct: ct);

        var t = new TerminalRegistration
        {
            MachineFingerprint = req.MachineFingerprint,
            MachineName = req.MachineName ?? string.Empty,
            Version = req.Version ?? string.Empty,
            ActivationId = req.ActivationId ?? string.Empty,
            FirstSeenUtc = now,
            LastHeartbeatUtc = now,
            Active = true,
            IpAddress = ip
        };
        _db.TerminalRegistrations.Add(t);
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Terminal registered: {fp} as id {id} (slots {used}/{max})",
            req.MachineFingerprint, t.Id, usedSlots + 1, maxSlots);

        return Ok(new TerminalRegisterResponse
        {
            Granted = true,
            SlotsInUse = usedSlots + 1,
            SlotsTotal = maxSlots,
            TerminalId = t.Id
        });
    }

    [HttpPost("heartbeat")]
    [ProducesResponseType(typeof(TerminalHeartbeatResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<TerminalHeartbeatResponse>> Heartbeat(
        [FromBody] TerminalHeartbeatRequest req,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MachineFingerprint))
            return BadRequest(new { error = "MachineFingerprint requerido." });

        var t = await _db.TerminalRegistrations
            .FirstOrDefaultAsync(x => x.MachineFingerprint == req.MachineFingerprint, ct);
        if (t == null)
            return Ok(new TerminalHeartbeatResponse { Authorized = false, Reason = "No registrada." });
        if (!t.Active)
            return Ok(new TerminalHeartbeatResponse { Authorized = false, Reason = "Terminal desactivada por admin." });

        t.LastHeartbeatUtc = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req.Version)) t.Version = req.Version;
        if (!string.IsNullOrWhiteSpace(req.MachineName)) t.MachineName = req.MachineName.Trim();
        var ip = ResolveClientIp();
        if (!string.IsNullOrWhiteSpace(ip))
            t.IpAddress = ip;
        await _db.SaveChangesAsync(ct);
        return Ok(new TerminalHeartbeatResponse { Authorized = true });
    }

    private string? ResolveClientIp()
    {
        var fwd = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first))
                return first;
        }

        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote == null)
            return null;

        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();

        var text = remote.ToString();
        if (string.Equals(text, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
            || string.Equals(text, "::1", StringComparison.OrdinalIgnoreCase))
            return null;

        return text;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<TerminalListItem>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IEnumerable<TerminalListItem>>> List(CancellationToken ct)
    {
        var list = await _db.TerminalRegistrations
            .AsNoTracking()
            .OrderByDescending(x => x.LastHeartbeatUtc)
            .Select(x => new TerminalListItem
            {
                Id = x.Id,
                MachineFingerprint = x.MachineFingerprint,
                MachineName = x.MachineName,
                Version = x.Version,
                ActivationId = x.ActivationId,
                FirstSeenUtc = x.FirstSeenUtc,
                LastHeartbeatUtc = x.LastHeartbeatUtc,
                Active = x.Active,
                IpAddress = x.IpAddress
            })
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Release(Guid id, CancellationToken ct)
    {
        var t = await _db.TerminalRegistrations.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t == null) return NotFound();
        t.Active = false;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }
}
