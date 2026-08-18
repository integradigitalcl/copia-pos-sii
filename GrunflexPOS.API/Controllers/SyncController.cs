using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/multicaja/sync")]
[AllowAnonymous]
[ServiceFilter(typeof(MulticajaLicenseModuleFilter))]
public sealed class SyncController : ControllerBase
{
    private const int MaxPageSize = 1000;
    private const int MaxIdsPerRequest = 200;
    private readonly ApiDbContext _db;
    private readonly PosCommerceDbContext _commerce;
    private readonly ILogger<SyncController> _log;

    public SyncController(ApiDbContext db, PosCommerceDbContext commerce, ILogger<SyncController> log)
    {
        _db = db;
        _commerce = commerce;
        _log = log;
    }

    /// <summary>Delta pull incremental. Cursor = último Id de MulticajaSyncChangeLogs visto.</summary>
    [HttpGet("changes")]
    [ProducesResponseType(typeof(SyncChangesResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<SyncChangesResponse>> GetChanges(
        [FromQuery] long cursor = 0,
        [FromQuery] int limit = 500,
        [FromQuery] string? domains = null,
        CancellationToken ct = default)
    {
        if (limit <= 0) limit = 500;
        if (limit > MaxPageSize) limit = MaxPageSize;

        HashSet<string>? domainFilter = null;
        if (!string.IsNullOrWhiteSpace(domains))
        {
            domainFilter = domains.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(d => d.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        var query = _db.MulticajaSyncChangeLogs.AsNoTracking()
            .Where(x => x.Id > cursor);

        if (domainFilter != null && domainFilter.Count > 0)
            query = query.Where(x => domainFilter.Contains(x.Domain));

        var rows = await query
            .OrderBy(x => x.Id)
            .Take(limit)
            .ToListAsync(ct);

        var deleted = rows.Where(x => x.IsDeleted).Select(x => x.EntityId).Distinct().ToList();
        var changes = rows.Where(x => !x.IsDeleted).Select(x => new SyncChangeItemDto
        {
            Cursor = x.Id,
            Domain = x.Domain,
            EntityId = x.EntityId,
            ChangeType = x.ChangeType,
            ChangedAtUtc = x.ChangedAtUtc
        }).ToList();

        var next = rows.Count > 0 ? rows[^1].Id : cursor;

        _log.LogDebug("multicaja.sync.changes cursor={C} next={N} count={Cnt}", cursor, next, rows.Count);

        return Ok(new SyncChangesResponse
        {
            NextCursor = next,
            ServerTimeUtc = DateTime.UtcNow,
            Changes = changes,
            DeletedIds = deleted,
            ResetRequired = false
        });
    }

    /// <summary>Productos por IDs (sync fina, payload mínimo).</summary>
    [HttpGet("products/by-ids")]
    [ProducesResponseType(typeof(List<MulticajaProductoDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MulticajaProductoDto>>> GetProductsByIds(
        [FromQuery] string ids,
        CancellationToken ct)
    {
        var idList = ParseIds(ids);
        if (idList.Count == 0)
            return Ok(new List<MulticajaProductoDto>());

        if (idList.Count > MaxIdsPerRequest)
            idList = idList.Take(MaxIdsPerRequest).ToList();

        var list = await _commerce.Productos.AsNoTracking()
            .Where(p => idList.Contains(p.Id))
            .OrderBy(p => p.Nombre)
            .Select(p => new MulticajaProductoDto
            {
                Id = p.Id,
                Nombre = p.Nombre,
                Costo = p.Costo,
                Precio = p.Precio,
                Stock = p.Stock,
                CodigoBarras = p.CodigoBarras,
                PrecioMayoreo = p.PrecioMayoreo,
                InvMinimo = p.InvMinimo,
                InvMaximo = p.InvMaximo,
                TipoVenta = p.TipoVenta,
                Departamento = p.Departamento,
                CategoriaId = p.CategoriaId
            })
            .ToListAsync(ct);

        _log.LogDebug("multicaja.sync.products.byids requested={Req} returned={Ret}", idList.Count, list.Count);
        return Ok(list);
    }

    /// <summary>Inventario (stock + precios) por IDs — misma proyección que productos.</summary>
    [HttpGet("inventory/by-ids")]
    [ProducesResponseType(typeof(List<MulticajaProductoDto>), StatusCodes.Status200OK)]
    public Task<ActionResult<List<MulticajaProductoDto>>> GetInventoryByIds(
        [FromQuery] string ids,
        CancellationToken ct) =>
        GetProductsByIds(ids, ct);

    private static List<int> ParseIds(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
            return new List<int>();
        return ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var v) ? v : 0)
            .Where(v => v > 0)
            .Distinct()
            .ToList();
    }
}
