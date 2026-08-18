using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

/// <summary>Endpoints enterprise de identidad/heartbeat de terminal (GUID persistente).</summary>
[ApiController]
[Route("api/terminals/enterprise")]
[AllowAnonymous]
public sealed class TerminalsEnterpriseController : ControllerBase
{
    private readonly TerminalIdentityService _identity;
    private readonly TerminalHeartbeatService _heartbeat;
    private readonly ApiDbContext _db;

    public TerminalsEnterpriseController(
        TerminalIdentityService identity,
        TerminalHeartbeatService heartbeat,
        ApiDbContext db)
    {
        _identity = identity;
        _heartbeat = heartbeat;
        _db = db;
    }

    [HttpPost("register")]
    public Task<TerminalEnterpriseRegisterResponse> Register(
        [FromBody] TerminalEnterpriseRegisterRequest req,
        CancellationToken ct) =>
        _identity.RegisterAsync(req, ResolveClientIp(), ct);

    [HttpPost("heartbeat")]
    public Task<TerminalEnterpriseHeartbeatResponse> Heartbeat(
        [FromBody] TerminalEnterpriseHeartbeatRequest req,
        CancellationToken ct) =>
        _heartbeat.BeatAsync(req, ResolveClientIp(), ct);

    [HttpPost("validate")]
    public async Task<TerminalValidateResponse> Validate(
        [FromBody] TerminalValidateRequest req,
        CancellationToken ct)
    {
        var (t, err) = await _identity.ValidateAsync(req.TerminalId, req.InstallationId, req.TerminalToken, ct);
        if (t == null)
            return new TerminalValidateResponse { Valid = false, Reason = err };

        var hb = await _db.TerminalHeartbeats.AsNoTracking()
            .FirstOrDefaultAsync(x => x.TerminalId == req.TerminalId, ct);

        return new TerminalValidateResponse
        {
            Valid = true,
            Active = t.Active,
            CajaId = t.CajaId,
            LastSeenAtUtc = hb?.LastSeenAtUtc ?? t.LastHeartbeatUtc
        };
    }

    [HttpPost("{id:guid}/deactivate")]
    public async Task<IActionResult> Deactivate(Guid id, [FromBody] DeactivateRequest req, CancellationToken ct)
    {
        await _identity.DeactivateAsync(id, req.Reason ?? "admin", ct);
        return NoContent();
    }

    [HttpGet("heartbeats")]
    public async Task<IActionResult> ListHeartbeats(CancellationToken ct)
    {
        var list = await _heartbeat.ListAsync(ct);
        return Ok(list.Select(x => new
        {
            x.TerminalId,
            x.CajaId,
            x.CurrentUserId,
            x.CurrentSessionId,
            x.LastSeenAtUtc,
            offline = TerminalHeartbeatService.IsOffline(x.LastSeenAtUtc)
        }));
    }

    [HttpGet("audit/{terminalId:guid}")]
    public async Task<IActionResult> Audit(Guid terminalId, CancellationToken ct)
    {
        var rows = await _db.TerminalAuditLogs.AsNoTracking()
            .Where(x => x.TerminalId == terminalId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(200)
            .ToListAsync(ct);
        return Ok(rows);
    }

    private string? ResolveClientIp()
    {
        var fwd = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fwd))
        {
            var first = fwd.Split(',')[0].Trim();
            if (!string.IsNullOrEmpty(first)) return first;
        }

        var remote = HttpContext.Connection.RemoteIpAddress;
        if (remote == null) return null;
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        var text = remote.ToString();
        if (text is "127.0.0.1" or "::1") return null;
        return text;
    }

    public sealed class DeactivateRequest
    {
        public string? Reason { get; set; }
    }
}
