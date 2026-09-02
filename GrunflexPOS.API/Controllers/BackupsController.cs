using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/backups")]
[AllowAnonymous]
public sealed class BackupsController : ControllerBase
{
    private readonly ApiDbContext _db;
    private readonly TenantBackupStorage _storage;

    public BackupsController(ApiDbContext db, TenantBackupStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    [HttpPost("upload")]
    [RequestSizeLimit(512 * 1024 * 1024)]
    [ProducesResponseType(typeof(BackupUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BackupUploadResponse>> Upload(
        IFormFile? file,
        CancellationToken cancellationToken)
    {
        var activationId = ResolveActivationId();
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest("Falta X-Tenant-Activation-Id.");

        if (file is null || file.Length <= 0)
            return BadRequest("Archivo vacío.");

        var license = await FindActiveLicenseAsync(activationId, cancellationToken);
        if (license is null)
            return BadRequest("ActivationId no registrado.");
        if (!license.CloudBackup)
            return StatusCode(StatusCodes.Status403Forbidden, "Módulo CloudBackup no activo.");
        if (license.ExpUtc <= DateTime.UtcNow)
            return StatusCode(StatusCodes.Status403Forbidden, "Licencia vencida.");

        await using var stream = file.OpenReadStream();
        var (relativePath, sizeBytes, sha256Hex) = await _storage.SaveAsync(
            activationId, file.FileName, stream, cancellationToken);

        var entity = new StoredBackup
        {
            ActivationId = activationId,
            OriginalFileName = Path.GetFileName(file.FileName),
            StorageFileName = relativePath,
            SizeBytes = sizeBytes,
            Sha256Hex = sha256Hex,
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.StoredBackups.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new BackupUploadResponse
        {
            Id = entity.Id,
            SizeBytes = sizeBytes,
            Sha256Hex = sha256Hex,
            CreatedAtUtc = entity.CreatedAtUtc
        });
    }

    [HttpGet]
    [ProducesResponseType(typeof(List<BackupListItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<List<BackupListItemResponse>>> List(
        [FromQuery] string activationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest();

        var aid = activationId.Trim();
        var list = await _db.StoredBackups.AsNoTracking()
            .Where(x => x.ActivationId == aid)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new BackupListItemResponse
            {
                Id = x.Id,
                OriginalFileName = x.OriginalFileName,
                SizeBytes = x.SizeBytes,
                Sha256Hex = x.Sha256Hex,
                CreatedAtUtc = x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(list);
    }

    [HttpGet("{id:guid}/download")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Download(
        Guid id,
        [FromQuery] string activationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest();

        var aid = activationId.Trim();
        var backup = await _db.StoredBackups.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id && x.ActivationId == aid, cancellationToken);
        if (backup is null)
            return NotFound();

        if (!_storage.TryOpenRead(backup.StorageFileName, out var stream, out var error) || stream is null)
            return NotFound(error);

        return File(stream, "application/zip", backup.OriginalFileName);
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        Guid id,
        [FromQuery] string activationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest();

        var aid = activationId.Trim();
        var backup = await _db.StoredBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.ActivationId == aid, cancellationToken);
        if (backup is null)
            return NotFound();

        if (!_storage.TryDelete(backup.StorageFileName, out _))
            return NotFound("Archivo no encontrado en almacenamiento.");

        _db.StoredBackups.Remove(backup);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private string? ResolveActivationId()
    {
        if (Request.Headers.TryGetValue("X-Tenant-Activation-Id", out var values))
        {
            var header = values.FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(header))
                return header;
        }

        return null;
    }

    private async Task<LicenseIssuerRecord?> FindActiveLicenseAsync(
        string activationId, CancellationToken cancellationToken) =>
        await _db.LicenseIssuerRecords.AsNoTracking()
            .Where(x => x.ActivationId == activationId.Trim())
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
}
