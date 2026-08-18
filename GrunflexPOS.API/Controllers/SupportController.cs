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
public sealed class SupportController : ControllerBase
{
    private readonly ApiDbContext _db;

    public SupportController(ApiDbContext db)
    {
        _db = db;
    }

    [HttpPost("tickets")]
    [ProducesResponseType(typeof(SupportTicketResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupportTicketResponse>> CreateTicket(
        [FromBody] SupportTicketCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActivationId) ||
            string.IsNullOrWhiteSpace(request.Subject) ||
            string.IsNullOrWhiteSpace(request.Body))
            return BadRequest("ActivationId, Subject y Body son obligatorios.");

        var aid = request.ActivationId.Trim();
        var exists = await _db.LicenseIssuerRecords.AsNoTracking()
            .AnyAsync(x => x.ActivationId == aid, cancellationToken);
        if (!exists)
            return BadRequest("ActivationId no registrado.");

        var entity = new SupportTicket
        {
            ActivationId = aid,
            Subject = request.Subject.Trim()[..Math.Min(request.Subject.Trim().Length, 200)],
            Body = request.Body.Trim()[..Math.Min(request.Body.Trim().Length, 8000)],
            Status = "Abierto",
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.SupportTickets.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        var dto = new SupportTicketResponse
        {
            Id = entity.Id,
            ActivationId = entity.ActivationId,
            Subject = entity.Subject,
            Body = entity.Body,
            Status = entity.Status,
            CreatedAtUtc = entity.CreatedAtUtc
        };

        return StatusCode(StatusCodes.Status201Created, dto);
    }

    [HttpGet("tickets")]
    [ProducesResponseType(typeof(List<SupportTicketResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<SupportTicketResponse>>> ListTickets(
        [FromQuery] string activationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest();

        var aid = activationId.Trim();
        var list = await _db.SupportTickets.AsNoTracking()
            .Where(x => x.ActivationId == aid)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new SupportTicketResponse
            {
                Id = x.Id,
                ActivationId = x.ActivationId,
                Subject = x.Subject,
                Body = x.Body,
                Status = x.Status,
                CreatedAtUtc = x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(list);
    }
}
