using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GrunflexPOS.API.Controllers;

/// <summary>Recepción y gestión de respaldos SQLite del POS.</summary>
[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class BackupsController : ControllerBase
{
    private readonly TenantBackupStorage _storage;

    public BackupsController(TenantBackupStorage storage)
    {
        _storage = storage;
    }

    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public IActionResult List([FromHeader(Name = "X-Tenant-Activation-Id")] string? activationId)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest("Falta el encabezado X-Tenant-Activation-Id.");

        var items = _storage.ListForTenant(activationId.Trim());
        return Ok(items);
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult Delete(
        [FromHeader(Name = "X-Tenant-Activation-Id")] string? activationId,
        [FromQuery] string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest("Falta el encabezado X-Tenant-Activation-Id.");
        if (string.IsNullOrWhiteSpace(relativePath))
            return BadRequest("Query relativePath es obligatorio.");

        if (!_storage.TryDeleteForTenant(activationId.Trim(), relativePath.Trim(), out var err))
        {
            if (err != null && err.Contains("no encontrado", StringComparison.OrdinalIgnoreCase))
                return NotFound(err);
            return BadRequest(err ?? "No se pudo eliminar.");
        }

        return Ok();
    }

    [HttpPost("upload")]
    [RequestSizeLimit(524_288_000)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
            return BadRequest("Se espera multipart/form-data.");

        var activation = Request.Headers["X-Tenant-Activation-Id"].ToString();
        if (string.IsNullOrWhiteSpace(activation))
            return BadRequest("Falta el encabezado X-Tenant-Activation-Id.");

        var form = await Request.ReadFormAsync(cancellationToken);
        var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
        if (file == null || file.Length == 0)
            return BadRequest("No se recibió archivo.");

        await using var stream = file.OpenReadStream();
        var (relativePath, sizeBytes, sha256Hex) =
            await _storage.SaveAsync(activation.Trim(), file.FileName, stream, cancellationToken).ConfigureAwait(false);

        return Ok(new { relativePath, sizeBytes, sha256 = sha256Hex });
    }
}
