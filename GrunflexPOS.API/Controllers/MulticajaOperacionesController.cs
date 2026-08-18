using Grunflex.Licensing.Security;
using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.Security;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

/// <summary>
/// Operaciones multicaja contra <c>grunflex.db</c> (POS). LAN + opcional clave compartida.
/// </summary>
[ApiController]
[Route("api/multicaja")]
[AllowAnonymous]
[ServiceFilter(typeof(MulticajaSharedSecretFilter))]
[ServiceFilter(typeof(MulticajaLicenseModuleFilter))]
public sealed class MulticajaOperacionesController : ControllerBase
{
    private readonly MulticajaVentaProcessor _ventas;
    private readonly MulticajaAnulacionProcessor _anulaciones;
    private readonly MulticajaDevolucionProcessor _devoluciones;
    private readonly MulticajaCierreCajaProcessor _cierres;
    private readonly MulticajaMovimientoCajaProcessor _movimientosCaja;
    private readonly MulticajaInventarioProcessor _inventario;
    private readonly MulticajaCajaSesionesService _sesiones;
    private readonly PosCommerceDbContext _pos;
    private readonly LicenseSlotService _licenseSlots;
    private readonly ILogger<MulticajaOperacionesController> _log;

    public MulticajaOperacionesController(
        MulticajaVentaProcessor ventas,
        MulticajaAnulacionProcessor anulaciones,
        MulticajaDevolucionProcessor devoluciones,
        MulticajaCierreCajaProcessor cierres,
        MulticajaMovimientoCajaProcessor movimientosCaja,
        MulticajaInventarioProcessor inventario,
        MulticajaCajaSesionesService sesiones,
        PosCommerceDbContext pos,
        LicenseSlotService licenseSlots,
        ILogger<MulticajaOperacionesController> log)
    {
        _ventas = ventas;
        _anulaciones = anulaciones;
        _devoluciones = devoluciones;
        _cierres = cierres;
        _movimientosCaja = movimientosCaja;
        _inventario = inventario;
        _sesiones = sesiones;
        _pos = pos;
        _licenseSlots = licenseSlots;
        _log = log;
    }

    [HttpPost("ventas/commit")]
    [ProducesResponseType(typeof(MulticajaVentaCommitResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaVentaCommitResponse>> CommitVenta(
        [FromBody] MulticajaVentaCommitRequest body,
        CancellationToken ct)
    {
        var r = await _ventas.CommitAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpPost("ventas/anular")]
    [ProducesResponseType(typeof(MulticajaAnularVentaResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaAnularVentaResponse>> AnularVenta(
        [FromBody] MulticajaAnularVentaRequest body,
        CancellationToken ct)
    {
        var r = await _anulaciones.AnularAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpPost("ventas/devolucion-linea")]
    [ProducesResponseType(typeof(MulticajaDevolucionLineaResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaDevolucionLineaResponse>> DevolucionLinea(
        [FromBody] MulticajaDevolucionLineaRequest body,
        CancellationToken ct)
    {
        var r = await _devoluciones.DevolverLineaAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpPost("inventario/ajustar")]
    [ProducesResponseType(typeof(MulticajaInventarioAjusteResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaInventarioAjusteResponse>> AjustarInventario(
        [FromBody] MulticajaInventarioAjusteRequest body,
        CancellationToken ct)
    {
        var r = await _inventario.AjustarAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpPost("caja-sesiones/cerrar")]
    [ProducesResponseType(typeof(MulticajaCierreCajaResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaCierreCajaResponse>> CerrarSesion(
        [FromBody] MulticajaCierreCajaRequest body,
        CancellationToken ct)
    {
        var r = await _cierres.CerrarAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpPost("caja-sesiones/movimiento")]
    [ProducesResponseType(typeof(MulticajaMovimientoCajaResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaMovimientoCajaResponse>> RegistrarMovimientoCaja(
        [FromBody] MulticajaMovimientoCajaRequest body,
        CancellationToken ct)
    {
        var r = await _movimientosCaja.RegistrarAsync(body, ct);
        if (!r.Ok)
            return Conflict(r);
        return Ok(r);
    }

    [HttpGet("cajas/{cajaId:guid}/exists")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CajaExiste(Guid cajaId, CancellationToken ct)
    {
        var ok = await _pos.Cajas.AnyAsync(c => c.Id == cajaId, ct);
        return ok ? NoContent() : NotFound();
    }

    [HttpGet("caja-sesiones/abierta")]
    [ProducesResponseType(typeof(MulticajaCajaSesionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<MulticajaCajaSesionDto>> SesionAbierta([FromQuery] Guid cajaId,
        CancellationToken ct)
    {
        var s = await _sesiones.ObtenerAbiertaAsync(cajaId, ct);
        return s == null ? NotFound() : Ok(s);
    }

    [HttpPost("caja-sesiones/abrir")]
    [ProducesResponseType(typeof(MulticajaCajaSesionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MulticajaCajaSesionDto>> AbrirSesion(
        [FromBody] MulticajaCajaSesionAbrirRequest body,
        CancellationToken ct)
    {
        try
        {
            var dto = await _sesiones.AbrirAsync(body, ct);
            return Ok(dto);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "multicaja.sesion abrir falló");
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("login")]
    [ProducesResponseType(typeof(MulticajaLoginResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaLoginResponse>> LoginPos(
        [FromBody] MulticajaLoginRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || body.Password == null)
            return Ok(new MulticajaLoginResponse { Ok = false, Error = "Credenciales incompletas." });

        var u = await _pos.Usuarios
            .FirstOrDefaultAsync(x => x.Username == body.Username, ct);

        if (u == null || !PasswordHasher.TryVerifyAndUpgrade(body.Password, u.Password, out var upgraded))
            return Ok(new MulticajaLoginResponse { Ok = false, Error = "Usuario o contraseña incorrectos." });

        if (upgraded != null)
        {
            u.Password = upgraded;
            await _pos.SaveChangesAsync(ct);
        }

        return Ok(new MulticajaLoginResponse
        {
            Ok = true,
            Id = u.Id,
            Username = u.Username,
            Nombre = u.Nombre,
            Rol = u.Rol
        });
    }

    [HttpPost("cajas/auto-registro")]
    [ProducesResponseType(typeof(MulticajaCajaAutoRegistroResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaCajaAutoRegistroResponse>> AutoRegistroCaja(
        [FromBody] MulticajaCajaAutoRegistroRequest body,
        CancellationToken ct)
    {
            var machine = NormalizarNombreEquipo(body.MachineName);

        try
        {
            if (!await _pos.Empresas.AnyAsync(ct))
                return Ok(new MulticajaCajaAutoRegistroResponse
                    { Ok = false, Error = "No hay empresa en el servidor. Configure la caja principal primero." });

            var empresa = await _pos.Empresas.OrderBy(e => e.FechaCreacion).FirstAsync(ct);

            var sufijoMaquina = $"({machine})";
            var marcadorLegacy = $"{machine} - Caja";

            var ya = await _pos.Cajas.FirstOrDefaultAsync(c =>
                c.Nombre == marcadorLegacy || c.Nombre.Contains(sufijoMaquina), ct);

            if (ya != null)
            {
                AsegurarNombreIncluyeEquipo(ya, machine);
                await _pos.SaveChangesAsync(ct);
                return Ok(new MulticajaCajaAutoRegistroResponse
                    { Ok = true, CajaId = ya.Id, Nombre = ya.Nombre });
            }

            var lic = await _licenseSlots.GetActiveLicenseAsync(null, ct);
            if (!await _licenseSlots.CanAddActiveCajaAsync(lic?.ActivationId, ct))
            {
                var maxBoxes = await _licenseSlots.GetMaxBoxesAsync(lic?.ActivationId, ct);
                return Ok(new MulticajaCajaAutoRegistroResponse
                {
                    Ok = false,
                    Error = LicenseSlotService.MensajeLimiteCajas(maxBoxes)
                });
            }

            var siguiente = await _pos.Cajas.CountAsync(ct) + 1;
            var nombreNuevo = $"Caja {siguiente} {sufijoMaquina}";
            var caja = new CommerceCaja
            {
                Id = Guid.NewGuid(),
                Nombre = nombreNuevo,
                EmpresaId = empresa.Id,
                Activa = true,
                FechaCreacion = DateTime.UtcNow
            };
            _pos.Cajas.Add(caja);
            await _pos.SaveChangesAsync(ct);

            _log.LogInformation("multicaja.caja auto-registrada id={Id} nombre={N}", caja.Id, nombreNuevo);

            return Ok(new MulticajaCajaAutoRegistroResponse
                { Ok = true, CajaId = caja.Id, Nombre = nombreNuevo });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "multicaja.caja auto-registro");
            return Ok(new MulticajaCajaAutoRegistroResponse { Ok = false, Error = ex.Message });
        }
    }

    [HttpPost("cajas/{cajaId:guid}/vincular-equipo")]
    [ProducesResponseType(typeof(MulticajaCajaAutoRegistroResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<MulticajaCajaAutoRegistroResponse>> VincularEquipo(
        Guid cajaId,
        [FromBody] MulticajaCajaAutoRegistroRequest body,
        CancellationToken ct)
    {
        var machine = NormalizarNombreEquipo(body.MachineName);
        try
        {
            var caja = await _pos.Cajas.FirstOrDefaultAsync(c => c.Id == cajaId, ct);
            if (caja == null)
                return Ok(new MulticajaCajaAutoRegistroResponse { Ok = false, Error = "Caja no encontrada." });

            AsegurarNombreIncluyeEquipo(caja, machine);
            await _pos.SaveChangesAsync(ct);
            _log.LogInformation("multicaja.caja vinculada id={Id} nombre={N} equipo={Eq}", caja.Id, caja.Nombre, machine);

            return Ok(new MulticajaCajaAutoRegistroResponse
                { Ok = true, CajaId = caja.Id, Nombre = caja.Nombre });
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "multicaja.caja vincular-equipo");
            return Ok(new MulticajaCajaAutoRegistroResponse { Ok = false, Error = ex.Message });
        }
    }

    private static string NormalizarNombreEquipo(string? machineName) =>
        string.IsNullOrWhiteSpace(machineName) ? "PC" : machineName.Trim();

    private static void AsegurarNombreIncluyeEquipo(CommerceCaja caja, string machine)
    {
        var sufijo = $"({machine})";
        if (caja.Nombre.Contains(sufijo, StringComparison.OrdinalIgnoreCase))
            return;

        var baseName = caja.Nombre.Trim();
        var idx = baseName.IndexOf('(');
        if (idx > 0)
            baseName = baseName[..idx].Trim();

        caja.Nombre = $"{baseName} {sufijo}";
    }

    [HttpGet("cajas/{cajaId:guid}/ordinal")]
    [ProducesResponseType(typeof(int), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<int>> OrdinalCaja(Guid cajaId, CancellationToken ct)
    {
        var caja = await _pos.Cajas.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cajaId, ct);
        if (caja == null)
            return NotFound();

        // SQLite no traduce string.Compare en EF; ordenar en memoria.
        var ordenadas = await _pos.Cajas.AsNoTracking()
            .OrderBy(c => c.FechaCreacion)
            .ThenBy(c => c.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        var idx = ordenadas.FindIndex(id => id == cajaId);
        return Ok(idx < 0 ? 1 : idx + 1);
    }

    [HttpGet("usuarios")]
    [ProducesResponseType(typeof(List<MulticajaUsuarioDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MulticajaUsuarioDto>>> ListarUsuarios(CancellationToken ct)
    {
        var list = await _pos.Usuarios.AsNoTracking()
            .OrderBy(u => u.Username)
            .Select(u => new MulticajaUsuarioDto
            {
                Id = u.Id,
                Username = u.Username,
                Nombre = u.Nombre,
                Rol = u.Rol
            })
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpPost("usuarios")]
    [ProducesResponseType(typeof(MulticajaUsuarioDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MulticajaUsuarioDto>> CrearUsuario(
        [FromBody] MulticajaUsuarioUpsertRequest body,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Nombre))
            return BadRequest("Usuario y nombre son obligatorios.");
        if (string.IsNullOrWhiteSpace(body.Password))
            return BadRequest("La contraseña es obligatoria al crear.");

        var uNorm = body.Username.Trim();
        if (await _pos.Usuarios.AnyAsync(x => x.Username == uNorm, ct))
            return BadRequest("Ya existe un usuario con ese nombre de usuario.");

        var u = new CommerceUsuario
        {
            Id = Guid.NewGuid(),
            Username = uNorm,
            Password = PasswordHasher.Hash(body.Password),
            Nombre = body.Nombre.Trim(),
            Rol = string.IsNullOrWhiteSpace(body.Rol) ? "Cajero" : body.Rol.Trim()
        };
        _pos.Usuarios.Add(u);
        await _pos.SaveChangesAsync(ct);
        return Ok(ToUsuarioDto(u));
    }

    [HttpPut("usuarios/{id:guid}")]
    [ProducesResponseType(typeof(MulticajaUsuarioDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<MulticajaUsuarioDto>> ActualizarUsuario(
        Guid id,
        [FromBody] MulticajaUsuarioUpsertRequest body,
        CancellationToken ct)
    {
        var u = await _pos.Usuarios.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u == null)
            return NotFound();

        if (string.IsNullOrWhiteSpace(body.Username) || string.IsNullOrWhiteSpace(body.Nombre))
            return BadRequest("Usuario y nombre son obligatorios.");

        var uNorm = body.Username.Trim();
        if (await _pos.Usuarios.AnyAsync(x => x.Username == uNorm && x.Id != id, ct))
            return BadRequest("Ya existe otro usuario con ese nombre de usuario.");

        u.Username = uNorm;
        u.Nombre = body.Nombre.Trim();
        if (!string.IsNullOrWhiteSpace(body.Password))
            u.Password = PasswordHasher.Hash(body.Password);
        if (!string.IsNullOrWhiteSpace(body.Rol))
            u.Rol = body.Rol.Trim();

        await _pos.SaveChangesAsync(ct);
        return Ok(ToUsuarioDto(u));
    }

    [HttpDelete("usuarios/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> EliminarUsuario(Guid id, CancellationToken ct)
    {
        var u = await _pos.Usuarios.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (u == null)
            return NoContent();

        var tieneVentas = await _pos.Ventas.AnyAsync(v => v.UsuarioId == id, ct);
        if (tieneVentas)
            return BadRequest("No se puede eliminar: el usuario tiene ventas registradas en el servidor.");

        _pos.Usuarios.Remove(u);
        await _pos.SaveChangesAsync(ct);
        return NoContent();
    }

    private static MulticajaUsuarioDto ToUsuarioDto(CommerceUsuario u) => new()
    {
        Id = u.Id,
        Username = u.Username,
        Nombre = u.Nombre,
        Rol = u.Rol
    };

    [HttpGet("productos")]
    [ProducesResponseType(typeof(List<MulticajaProductoDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<List<MulticajaProductoDto>>> ListarProductos(CancellationToken ct)
    {
        var list = await _pos.Productos.AsNoTracking()
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
        return Ok(list);
    }
}
