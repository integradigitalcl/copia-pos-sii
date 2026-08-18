using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
[ServiceFilter(typeof(MulticajaSharedSecretFilter))]
public class ProductosController : ControllerBase
{
    private readonly PosCommerceDbContext _db;

    public ProductosController(PosCommerceDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<ProductoPorCodigoResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<ProductoPorCodigoResponse>>> Listado(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? q = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.Productos.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x => x.CodigoBarras == term || x.Nombre.Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(p => new ProductoPorCodigoResponse
            {
                Id = p.Id,
                CodigoBarras = p.CodigoBarras,
                Nombre = p.Nombre,
                Precio = p.Precio,
                Stock = p.Stock
            })
            .ToListAsync(cancellationToken);

        return Ok(new PagedResult<ProductoPorCodigoResponse>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    /// <summary>Busca un producto por código de barras (venta POS).</summary>
    [HttpGet("codigo/{codigo}")]
    [ProducesResponseType(typeof(ProductoPorCodigoResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductoPorCodigoResponse>> PorCodigo(string codigo, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(codigo))
            return NotFound();

        var trimmed = codigo.Trim();

        var p = await _db.Productos
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.CodigoBarras == trimmed, cancellationToken);

        if (p == null)
            return NotFound();

        return Ok(new ProductoPorCodigoResponse
        {
            Id = p.Id,
            CodigoBarras = p.CodigoBarras,
            Nombre = p.Nombre,
            Precio = p.Precio,
            Stock = p.Stock
        });
    }
}
