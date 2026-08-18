using Grunflex.Licensing;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[AllowAnonymous]
public sealed class LicensingController : ControllerBase
{
    private readonly ApiDbContext _db;
    private readonly LicensingIssueService _issue;

    public LicensingController(ApiDbContext db, LicensingIssueService issue)
    {
        _db = db;
        _issue = issue;
    }

    [HttpPost("activate")]
    [ProducesResponseType(typeof(LicensingTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LicensingTokenResponse>> Activate(
        [FromBody] LicensingActivateRequest request,
        CancellationToken cancellationToken)
    {
        return await IssueTokenInternal(request, cancellationToken);
    }

    [HttpPost("refresh")]
    [ProducesResponseType(typeof(LicensingTokenResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<ActionResult<LicensingTokenResponse>> Refresh(
        [FromBody] LicensingActivateRequest request,
        CancellationToken cancellationToken)
    {
        return await IssueTokenInternal(request, cancellationToken);
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(LicensingStatusResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LicensingStatusResponse>> Status(
        [FromQuery] string activationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(activationId))
            return BadRequest();

        var record = await _db.LicenseIssuerRecords.AsNoTracking()
            .Where(x => x.ActivationId == activationId.Trim())
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null)
        {
            return Ok(new LicensingStatusResponse { Found = false });
        }

        var now = DateTime.UtcNow;
        return Ok(new LicensingStatusResponse
        {
            Found = true,
            Expired = record.ExpUtc <= now,
            ExpUtc = record.ExpUtc,
            Multicaja = record.Multicaja,
            OnlineSupport = record.OnlineSupport,
            CloudBackup = record.CloudBackup,
            PrioritySupport = record.PrioritySupport,
            OfflineGraceDays = EffectiveOfflineGraceDays(record.OfflineGraceDays),
            NumberOfBoxes = record.NumberOfBoxes > 0 ? record.NumberOfBoxes : 1
        });
    }

    [HttpGet("public-key")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<object> PublicKey()
    {
        var pem = _issue.PublicKeyPem;
        if (string.IsNullOrWhiteSpace(pem))
            return NotFound("Clave pública de licencias no configurada.");

        return Ok(new { publicKeyPem = pem });
    }

    private async Task<ActionResult<LicensingTokenResponse>> IssueTokenInternal(
        LicensingActivateRequest request,
        CancellationToken cancellationToken)
    {
        if (!_issue.IsConfigured)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Servidor sin clave de firma de licencias.");

        if (string.IsNullOrWhiteSpace(request.ActivationId) || string.IsNullOrWhiteSpace(request.MachineName))
            return BadRequest("ActivationId y MachineName son obligatorios.");

        var aid = request.ActivationId.Trim();
        var machine = request.MachineName.Trim();

        var record = await _db.LicenseIssuerRecords.AsNoTracking()
            .Where(x => x.ActivationId == aid)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

        if (record == null)
            return NotFound("ActivationId no registrado.");

        if (record.ExpUtc <= DateTime.UtcNow)
            return BadRequest("La licencia comercial está vencida en servidor.");

        var payload = new GrunflexLicensePayload
        {
            Customer = $"{record.CustomerName} — {record.BusinessName}",
            Machine = machine,
            ExpUtc = record.ExpUtc,
            Multicaja = record.Multicaja,
            OnlineSupport = record.OnlineSupport,
            CloudBackup = record.CloudBackup,
            PrioritySupport = record.PrioritySupport,
            ActivationId = record.ActivationId,
            OfflineGraceDays = EffectiveOfflineGraceDays(record.OfflineGraceDays),
            NumberOfBoxes = record.NumberOfBoxes > 0 ? record.NumberOfBoxes : 1
        };

        var token = _issue.SignPayload(payload);

        return Ok(new LicensingTokenResponse
        {
            LicenseToken = token,
            ExpUtc = record.ExpUtc,
            Multicaja = record.Multicaja,
            OnlineSupport = record.OnlineSupport,
            CloudBackup = record.CloudBackup,
            PrioritySupport = record.PrioritySupport,
            ActivationId = record.ActivationId,
            LastValidUtc = DateTime.UtcNow,
            OfflineGraceDays = payload.OfflineGraceDays,
            NumberOfBoxes = record.NumberOfBoxes > 0 ? record.NumberOfBoxes : 1
        });
    }

    private static int EffectiveOfflineGraceDays(int recordDays) =>
        recordDays > 0 ? recordDays : GrunflexLicenseDefaults.OfflineGraceDays;
}
