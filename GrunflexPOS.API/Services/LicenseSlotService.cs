using GrunflexPOS.API.Commerce;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.Models;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

/// <summary>Única fuente de verdad para límite NumberOfBoxes (cajas activas + terminales activos).</summary>
public sealed class LicenseSlotService
{
    public const string MensajeLimiteCajasBase =
        "Ha alcanzado el límite de cajas/terminales de su plan.\n\n" +
        "Para agregar otra caja debe ampliar su licencia Multicaja. Contacte a ventas@grunflex.cl o a su distribuidor GrünFlex.";

    private static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(7);
    private readonly ApiDbContext _db;
    private readonly PosCommerceDbContext _pos;

    public LicenseSlotService(ApiDbContext db, PosCommerceDbContext pos)
    {
        _db = db;
        _pos = pos;
    }

    public static string MensajeLimiteCajas(int maxBoxes) =>
        $"{MensajeLimiteCajasBase}\n\nCajas permitidas en su plan: {maxBoxes}.";

    public async Task<LicenseIssuerRecord?> GetActiveLicenseAsync(string? activationId, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(activationId))
        {
            return await _db.LicenseIssuerRecords.AsNoTracking()
                .Where(x => x.ActivationId == activationId && x.ExpUtc > DateTime.UtcNow)
                .OrderByDescending(x => x.CreatedAtUtc)
                .FirstOrDefaultAsync(ct);
        }

        return await _db.LicenseIssuerRecords.AsNoTracking()
            .Where(x => x.ExpUtc > DateTime.UtcNow)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<int> GetMaxBoxesAsync(string? activationId, CancellationToken ct = default)
    {
        var lic = await GetActiveLicenseAsync(activationId, ct);
        return Math.Max(1, lic?.NumberOfBoxes ?? 1);
    }

    public async Task<int> CountActiveCajasAsync(Guid? excludeCajaId = null, CancellationToken ct = default)
    {
        var q = _pos.Cajas.Where(c => c.Activa);
        if (excludeCajaId is { } id && id != Guid.Empty)
            q = q.Where(c => c.Id != id);
        return await q.CountAsync(ct);
    }

    public async Task<(int ActiveTerminalSlots, int MaxBoxes)> CountActiveTerminalSlotsAsync(
        string? excludeFingerprint,
        string? activationId,
        CancellationToken ct = default)
    {
        var maxBoxes = await GetMaxBoxesAsync(activationId, ct);
        var activeCutoff = DateTime.UtcNow - InactiveAfter;

        var query = _db.TerminalRegistrations.Where(t =>
            t.Active && t.LastHeartbeatUtc >= activeCutoff);

        if (!string.IsNullOrWhiteSpace(excludeFingerprint))
            query = query.Where(t => t.MachineFingerprint != excludeFingerprint);

        var active = await query.CountAsync(ct);
        return (active, maxBoxes);
    }

    public async Task<(int UsedSlots, int MaxBoxes)> GetUnifiedSlotUsageAsync(
        string? activationId,
        string? excludeFingerprint = null,
        Guid? excludeCajaId = null,
        CancellationToken ct = default)
    {
        var max = await GetMaxBoxesAsync(activationId, ct);
        var (terminals, _) = await CountActiveTerminalSlotsAsync(excludeFingerprint, activationId, ct);
        var cajas = await CountActiveCajasAsync(excludeCajaId, ct);
        return (Math.Max(terminals, cajas), max);
    }

    public async Task<bool> CanAddActiveCajaAsync(string? activationId, CancellationToken ct = default)
    {
        var (used, max) = await GetUnifiedSlotUsageAsync(activationId, ct: ct);
        return used + 1 <= max;
    }

    public async Task<bool> CanRegisterNewTerminalAsync(
        string? machineFingerprint,
        string? activationId,
        CancellationToken ct = default)
    {
        var (used, max) = await GetUnifiedSlotUsageAsync(activationId, excludeFingerprint: machineFingerprint, ct: ct);
        return used + 1 <= max;
    }
}
