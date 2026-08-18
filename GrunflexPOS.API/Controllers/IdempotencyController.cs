using GrunflexPOS.API.Data;
using GrunflexPOS.API.Idempotency;
using GrunflexPOS.API.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

/// <summary>Consulta de registros de idempotencia (soporte / diagnóstico).</summary>
[ApiController]
[Route("api/idempotency")]
[Authorize(Policy = "ApiOperator")]
public sealed class IdempotencyController : ControllerBase
{
    private readonly ApiDbContext _db;

    public IdempotencyController(ApiDbContext db) => _db = db;

    [HttpGet("status")]
    public async Task<ActionResult<object>> GetStatus(
        [FromQuery] string requestId,
        [FromQuery] IdempotencyOperationType operationType,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(requestId))
            return BadRequest();

        var op = operationType.ToStorageName();
        var row = await _db.IdempotencyRecords.AsNoTracking()
            .Where(x => x.RequestId == requestId.Trim() && x.OperationType == op)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct);

        if (row == null)
            return NotFound();

        return Ok(new
        {
            row.RequestId,
            row.OperationType,
            row.RequestHash,
            row.TerminalId,
            row.CajaId,
            Status = row.Status.ToString(),
            row.ResponseCode,
            row.ResourceType,
            row.ResourceId,
            row.CreatedAt,
            row.CompletedAt,
            row.ExpiresAt,
            HasStoredResponse = !string.IsNullOrEmpty(row.ResponsePayload)
        });
    }
}
