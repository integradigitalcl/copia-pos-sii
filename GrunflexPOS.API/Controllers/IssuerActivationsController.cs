using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public class IssuerActivationsController : ControllerBase
{
    private readonly ApiDbContext _db;

    public IssuerActivationsController(ApiDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<IssuerActivationResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<IssuerActivationResponse>>> Listado(
        [FromQuery] string? q = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.IssuerActivationRecords.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                x.ActivationId.Contains(term) ||
                x.CustomerDisplay.Contains(term) ||
                x.DeviceName.Contains(term) ||
                x.HardwareId.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(status)
            && !string.Equals(status, "Todos", StringComparison.OrdinalIgnoreCase))
        {
            var st = status.Trim();
            query = query.Where(x => x.Status == st);
        }

        var items = await query
            .OrderByDescending(x => x.LastSeenUtc)
            .Select(x => new IssuerActivationResponse
            {
                Id = x.Id,
                ActivationId = x.ActivationId,
                CustomerDisplay = x.CustomerDisplay,
                DeviceName = x.DeviceName,
                HardwareId = x.HardwareId,
                ActivatedAtUtc = x.ActivatedAtUtc,
                LastSeenUtc = x.LastSeenUtc,
                Status = x.Status
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    [HttpPost]
    [ProducesResponseType(typeof(IssuerActivationResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IssuerActivationResponse>> Crear(
        [FromBody] IssuerActivationUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActivationId))
            return BadRequest("ActivationId obligatorio.");

        var entity = new IssuerActivationRecord
        {
            Id = Guid.NewGuid(),
            ActivationId = request.ActivationId.Trim(),
            CustomerDisplay = request.CustomerDisplay?.Trim() ?? string.Empty,
            DeviceName = request.DeviceName?.Trim() ?? "Principal",
            HardwareId = request.HardwareId?.Trim() ?? "-",
            ActivatedAtUtc = request.ActivatedAtUtc ?? DateTime.UtcNow,
            LastSeenUtc = request.LastSeenUtc ?? DateTime.UtcNow,
            Status = string.IsNullOrWhiteSpace(request.Status) ? "Activa" : request.Status.Trim()
        };

        _db.IssuerActivationRecords.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(Listado), Map(entity));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(IssuerActivationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssuerActivationResponse>> Actualizar(
        Guid id,
        [FromBody] IssuerActivationUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.IssuerActivationRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        if (!string.IsNullOrWhiteSpace(request.ActivationId))
            entity.ActivationId = request.ActivationId.Trim();
        if (request.CustomerDisplay != null)
            entity.CustomerDisplay = request.CustomerDisplay.Trim();
        if (request.DeviceName != null)
            entity.DeviceName = request.DeviceName.Trim();
        if (request.HardwareId != null)
            entity.HardwareId = request.HardwareId.Trim();
        if (request.ActivatedAtUtc.HasValue)
            entity.ActivatedAtUtc = request.ActivatedAtUtc.Value;
        if (request.LastSeenUtc.HasValue)
            entity.LastSeenUtc = request.LastSeenUtc.Value;
        if (!string.IsNullOrWhiteSpace(request.Status))
            entity.Status = request.Status.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        return Ok(Map(entity));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Eliminar(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.IssuerActivationRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        _db.IssuerActivationRecords.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static IssuerActivationResponse Map(IssuerActivationRecord x) =>
        new()
        {
            Id = x.Id,
            ActivationId = x.ActivationId,
            CustomerDisplay = x.CustomerDisplay,
            DeviceName = x.DeviceName,
            HardwareId = x.HardwareId,
            ActivatedAtUtc = x.ActivatedAtUtc,
            LastSeenUtc = x.LastSeenUtc,
            Status = x.Status
        };
}
