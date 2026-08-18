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
public class IssuerClientsController : ControllerBase
{
    private readonly ApiDbContext _db;

    public IssuerClientsController(ApiDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<IssuerClientResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<IssuerClientResponse>>> Listado(
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        var query = _db.IssuerClientRecords.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                x.ClientCode.Contains(term) ||
                x.Name.Contains(term) ||
                x.Business.Contains(term) ||
                x.Email.Contains(term));
        }

        var clients = await query.OrderByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        var licenses = await _db.LicenseIssuerRecords.AsNoTracking().ToListAsync(cancellationToken);

        var result = new List<IssuerClientResponse>();
        foreach (var c in clients)
        {
            var n = Norm(c.Name);
            var b = Norm(c.Business);
            var linked = licenses.Count(l =>
                string.Equals(Norm(l.CustomerName), n, StringComparison.Ordinal)
                && string.Equals(Norm(l.BusinessName), b, StringComparison.Ordinal));

            result.Add(new IssuerClientResponse
            {
                Id = c.Id,
                ClientCode = c.ClientCode,
                Name = c.Name,
                Business = c.Business,
                Email = c.Email,
                Phone = c.Phone,
                CreatedAtUtc = c.CreatedAtUtc,
                LinkedLicensesCount = linked
            });
        }

        return Ok(result);
    }

    [HttpPost]
    [ProducesResponseType(typeof(IssuerClientResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IssuerClientResponse>> Crear(
        [FromBody] IssuerClientUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Business))
            return BadRequest("Nombre y negocio son obligatorios.");

        var code = await NextClientCodeAsync(cancellationToken);
        var entity = new IssuerClientRecord
        {
            Id = Guid.NewGuid(),
            ClientCode = code,
            Name = request.Name.Trim(),
            Business = request.Business.Trim(),
            Email = request.Email?.Trim() ?? string.Empty,
            Phone = request.Phone?.Trim() ?? string.Empty,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.IssuerClientRecords.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return Created($"/api/IssuerClients/{entity.Id}", Map(entity, 0));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(IssuerClientResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IssuerClientResponse>> Actualizar(
        Guid id,
        [FromBody] IssuerClientUpsertRequest request,
        CancellationToken cancellationToken)
    {
        var entity = await _db.IssuerClientRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Business))
            return BadRequest("Nombre y negocio son obligatorios.");

        entity.Name = request.Name.Trim();
        entity.Business = request.Business.Trim();
        entity.Email = request.Email?.Trim() ?? string.Empty;
        entity.Phone = request.Phone?.Trim() ?? string.Empty;

        await _db.SaveChangesAsync(cancellationToken);

        var licenses = await _db.LicenseIssuerRecords.AsNoTracking().ToListAsync(cancellationToken);
        var linked = CountLinked(licenses, entity);

        return Ok(Map(entity, linked));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Eliminar(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.IssuerClientRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        _db.IssuerClientRecords.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static string Norm(string s) => s.Trim().ToLowerInvariant();

    private static int CountLinked(List<LicenseIssuerRecord> licenses, IssuerClientRecord c)
    {
        var n = Norm(c.Name);
        var b = Norm(c.Business);
        return licenses.Count(l =>
            string.Equals(Norm(l.CustomerName), n, StringComparison.Ordinal)
            && string.Equals(Norm(l.BusinessName), b, StringComparison.Ordinal));
    }

    private async Task<string> NextClientCodeAsync(CancellationToken ct)
    {
        var codes = await _db.IssuerClientRecords
            .AsNoTracking()
            .Select(x => x.ClientCode)
            .ToListAsync(ct);

        var max = 0;
        foreach (var code in codes)
        {
            if (code.StartsWith("CLI-", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(code.AsSpan(4), out var n)
                && n > max)
                max = n;
        }

        return $"CLI-{(max + 1):D4}";
    }

    private static IssuerClientResponse Map(IssuerClientRecord c, int linked) =>
        new()
        {
            Id = c.Id,
            ClientCode = c.ClientCode,
            Name = c.Name,
            Business = c.Business,
            Email = c.Email,
            Phone = c.Phone,
            CreatedAtUtc = c.CreatedAtUtc,
            LinkedLicensesCount = linked
        };
}
