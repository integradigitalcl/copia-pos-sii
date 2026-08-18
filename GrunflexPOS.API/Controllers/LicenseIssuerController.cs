using System.Globalization;
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
public class LicenseIssuerController : ControllerBase
{
    private readonly ApiDbContext _db;

    public LicenseIssuerController(ApiDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<LicenseIssuerRecordResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResult<LicenseIssuerRecordResponse>>> Listado(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? q = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 200);

        var query = _db.LicenseIssuerRecords.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = query.Where(x =>
                x.ActivationId.Contains(term) ||
                x.CustomerName.Contains(term) ||
                x.BusinessName.Contains(term));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var now = DateTime.UtcNow;
            var st = status.Trim();
            if (string.Equals(st, "active", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => x.ExpUtc > now);
            else if (string.Equals(st, "expired", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => x.ExpUtc <= now);
            else if (string.Equals(st, "expiring", StringComparison.OrdinalIgnoreCase))
                query = query.Where(x => x.ExpUtc > now && x.ExpUtc <= now.AddDays(7));
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new LicenseIssuerRecordResponse
            {
                Id = x.Id,
                ActivationId = x.ActivationId,
                CustomerName = x.CustomerName,
                BusinessName = x.BusinessName,
                LicenseType = x.LicenseType,
                NumberOfBoxes = x.NumberOfBoxes,
                ExpUtc = x.ExpUtc,
                Multicaja = x.Multicaja,
                OnlineSupport = x.OnlineSupport,
                CloudBackup = x.CloudBackup,
                PrioritySupport = x.PrioritySupport,
                OfflineGraceDays = x.OfflineGraceDays,
                LicenseToken = x.LicenseToken,
                CreatedAtUtc = x.CreatedAtUtc
            })
            .ToListAsync(cancellationToken);

        return Ok(new PagedResult<LicenseIssuerRecordResponse>
        {
            Items = items,
            Total = total,
            Page = page,
            PageSize = pageSize
        });
    }

    [HttpGet("stats")]
    [ProducesResponseType(typeof(LicenseIssuerStatsResponse), StatusCodes.Status200OK)]
    public async Task<ActionResult<LicenseIssuerStatsResponse>> Estadisticas(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var licenses = await _db.LicenseIssuerRecords.AsNoTracking().ToListAsync(cancellationToken);
        var activeList = licenses.Where(x => x.ExpUtc > now).ToList();
        var active = activeList.Count;
        var expired = licenses.Count(x => x.ExpUtc <= now);
        var clients = await _db.IssuerClientRecords.AsNoTracking().CountAsync(cancellationToken);
        var activations = await _db.IssuerActivationRecords.AsNoTracking().CountAsync(cancellationToken);

        var planBasico = 0;
        var planMedium = 0;
        var planPlus = 0;
        var planOtro = 0;
        foreach (var x in activeList)
            BucketPlan(x.LicenseType, ref planBasico, ref planMedium, ref planPlus, ref planOtro);

        var sparkCounts = new List<int>(6);
        var sparkLabels = new List<string>(6);
        for (var i = 0; i < 6; i++)
        {
            var anchor = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-5 + i);
            var next = anchor.AddMonths(1);
            sparkCounts.Add(licenses.Count(l => l.CreatedAtUtc >= anchor && l.CreatedAtUtc < next));
            sparkLabels.Add($"{MesCortoEs(anchor)} '{anchor:yy}");
        }

        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var prevStart = monthStart.AddMonths(-1);
        var newThis = licenses.Count(l => l.CreatedAtUtc >= monthStart && l.CreatedAtUtc < monthStart.AddMonths(1));
        var newPrev = licenses.Count(l => l.CreatedAtUtc >= prevStart && l.CreatedAtUtc < monthStart);

        var expiringSoon = licenses.Count(x => x.ExpUtc > now && x.ExpUtc <= now.AddDays(7));
        var pendingDevices = await _db.IssuerActivationRecords.AsNoTracking()
            .CountAsync(
                x => (x.DeviceName ?? string.Empty).Trim().Equals("Pendiente", StringComparison.OrdinalIgnoreCase),
                cancellationToken);

        return Ok(new LicenseIssuerStatsResponse
        {
            ActiveLicenses = active,
            ExpiredLicenses = expired,
            LicensesTotal = licenses.Count,
            ClientsTotal = clients,
            ActivationsTotal = activations,
            PlanBasico = planBasico,
            PlanMedium = planMedium,
            PlanPlus = planPlus,
            PlanOtro = planOtro,
            NewLicensesByMonthLast6 = sparkCounts,
            SparkMonthLabels = sparkLabels,
            NewLicensesThisCalendarMonthUtc = newThis,
            NewLicensesPreviousCalendarMonthUtc = newPrev,
            ExpiringWithin7Days = expiringSoon,
            PendingDeviceActivations = pendingDevices
        });
    }

    private static string MesCortoEs(DateTime utc)
    {
        try
        {
            return utc.ToString("MMM", CultureInfo.GetCultureInfo("es-ES")).TrimEnd('.').ToLowerInvariant();
        }
        catch (CultureNotFoundException)
        {
            return utc.ToString("MMM", CultureInfo.InvariantCulture).ToLowerInvariant();
        }
    }

    private static void BucketPlan(string? licenseType, ref int basico, ref int medium, ref int plus, ref int otro)
    {
        var t = (licenseType ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Contains("básico", StringComparison.OrdinalIgnoreCase) || t.Contains("basico", StringComparison.OrdinalIgnoreCase))
            basico++;
        else if (t.Contains("medium", StringComparison.OrdinalIgnoreCase))
            medium++;
        else if (t.Contains("plus", StringComparison.OrdinalIgnoreCase))
            plus++;
        else
            otro++;
    }

    [HttpPost]
    [ProducesResponseType(typeof(LicenseIssuerRecordResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LicenseIssuerRecordResponse>> Crear(
        [FromBody] LicenseIssuerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActivationId) ||
            string.IsNullOrWhiteSpace(request.CustomerName) ||
            string.IsNullOrWhiteSpace(request.BusinessName) ||
            string.IsNullOrWhiteSpace(request.LicenseToken))
        {
            return BadRequest("Campos obligatorios incompletos.");
        }

        var aid = request.ActivationId.Trim();
        var existing = await _db.LicenseIssuerRecords.FirstOrDefaultAsync(x => x.ActivationId == aid, cancellationToken);
        if (existing != null)
        {
            existing.CustomerName = request.CustomerName.Trim();
            existing.BusinessName = request.BusinessName.Trim();
            existing.LicenseType = string.IsNullOrWhiteSpace(request.LicenseType) ? "Suscripción" : request.LicenseType.Trim();
            existing.NumberOfBoxes = request.NumberOfBoxes;
            existing.ExpUtc = request.ExpUtc;
            existing.Multicaja = request.Multicaja;
            existing.OnlineSupport = request.OnlineSupport;
            existing.CloudBackup = request.CloudBackup;
            existing.PrioritySupport = request.PrioritySupport;
            existing.OfflineGraceDays = NormalizeOfflineGraceDays(request.OfflineGraceDays);
            existing.LicenseToken = request.LicenseToken.Trim();
            await _db.SaveChangesAsync(cancellationToken);
            return Ok(ToResponse(existing));
        }

        var entity = new LicenseIssuerRecord
        {
            Id = Guid.NewGuid(),
            ActivationId = aid,
            CustomerName = request.CustomerName.Trim(),
            BusinessName = request.BusinessName.Trim(),
            LicenseType = string.IsNullOrWhiteSpace(request.LicenseType) ? "Suscripción" : request.LicenseType.Trim(),
            NumberOfBoxes = request.NumberOfBoxes,
            ExpUtc = request.ExpUtc,
            Multicaja = request.Multicaja,
            OnlineSupport = request.OnlineSupport,
            CloudBackup = request.CloudBackup,
            PrioritySupport = request.PrioritySupport,
            OfflineGraceDays = NormalizeOfflineGraceDays(request.OfflineGraceDays),
            LicenseToken = request.LicenseToken.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };

        _db.LicenseIssuerRecords.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        await EnsureActivationStubAsync(entity, cancellationToken);

        return CreatedAtAction(nameof(Listado), new { page = 1, pageSize = 1, q = entity.ActivationId }, ToResponse(entity));
    }

    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(LicenseIssuerRecordResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LicenseIssuerRecordResponse>> Actualizar(
        Guid id,
        [FromBody] LicenseIssuerUpsertRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ActivationId) ||
            string.IsNullOrWhiteSpace(request.CustomerName) ||
            string.IsNullOrWhiteSpace(request.BusinessName) ||
            string.IsNullOrWhiteSpace(request.LicenseToken))
        {
            return BadRequest("Campos obligatorios incompletos.");
        }

        var entity = await _db.LicenseIssuerRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        entity.ActivationId = request.ActivationId.Trim();
        entity.CustomerName = request.CustomerName.Trim();
        entity.BusinessName = request.BusinessName.Trim();
        entity.LicenseType = string.IsNullOrWhiteSpace(request.LicenseType) ? "Suscripción" : request.LicenseType.Trim();
        entity.NumberOfBoxes = request.NumberOfBoxes;
        entity.ExpUtc = request.ExpUtc;
        entity.Multicaja = request.Multicaja;
        entity.OnlineSupport = request.OnlineSupport;
        entity.CloudBackup = request.CloudBackup;
        entity.PrioritySupport = request.PrioritySupport;
        entity.OfflineGraceDays = NormalizeOfflineGraceDays(request.OfflineGraceDays);
        entity.LicenseToken = request.LicenseToken.Trim();

        await _db.SaveChangesAsync(cancellationToken);
        await EnsureActivationStubAsync(entity, cancellationToken);

        return Ok(ToResponse(entity));
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Eliminar(Guid id, CancellationToken cancellationToken)
    {
        var entity = await _db.LicenseIssuerRecords.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (entity == null)
            return NotFound();

        var orphans = await _db.IssuerActivationRecords
            .Where(x => x.ActivationId == entity.ActivationId)
            .ToListAsync(cancellationToken);
        _db.IssuerActivationRecords.RemoveRange(orphans);

        _db.LicenseIssuerRecords.Remove(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private static LicenseIssuerRecordResponse ToResponse(LicenseIssuerRecord entity) =>
        new()
        {
            Id = entity.Id,
            ActivationId = entity.ActivationId,
            CustomerName = entity.CustomerName,
            BusinessName = entity.BusinessName,
            LicenseType = entity.LicenseType,
            NumberOfBoxes = entity.NumberOfBoxes,
            ExpUtc = entity.ExpUtc,
            Multicaja = entity.Multicaja,
            OnlineSupport = entity.OnlineSupport,
            CloudBackup = entity.CloudBackup,
            PrioritySupport = entity.PrioritySupport,
            OfflineGraceDays = entity.OfflineGraceDays,
            LicenseToken = entity.LicenseToken,
            CreatedAtUtc = entity.CreatedAtUtc
        };

    private async Task EnsureActivationStubAsync(LicenseIssuerRecord license, CancellationToken cancellationToken)
    {
        var aid = license.ActivationId.Trim();
        var exists = await _db.IssuerActivationRecords
            .AnyAsync(x => x.ActivationId == aid, cancellationToken);
        if (exists)
            return;

        var display = $"{license.CustomerName} — {license.BusinessName}".Trim();
        _db.IssuerActivationRecords.Add(new IssuerActivationRecord
        {
            Id = Guid.NewGuid(),
            ActivationId = aid,
            CustomerDisplay = display,
            DeviceName = "Pendiente",
            HardwareId = "-",
            ActivatedAtUtc = license.CreatedAtUtc,
            LastSeenUtc = DateTime.UtcNow,
            Status = "Activa"
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    private static int NormalizeOfflineGraceDays(int days) => Math.Clamp(days, 0, 365);
}
