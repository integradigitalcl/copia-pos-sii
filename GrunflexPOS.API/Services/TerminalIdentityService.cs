using System.Security.Cryptography;
using System.Text;
using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using GrunflexPOS.API.Services;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

public static class TerminalTokenHasher
{
    public static string Hash(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(token.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool Verify(string token, string? storedHash) =>
        !string.IsNullOrWhiteSpace(storedHash)
        && string.Equals(Hash(token), storedHash, StringComparison.OrdinalIgnoreCase);
}

public sealed class TerminalIdentityService
{
    private static readonly TimeSpan InactiveAfter = TimeSpan.FromDays(7);
    private readonly ApiDbContext _db;
    private readonly LicenseSlotService _slots;
    private readonly ILogger<TerminalIdentityService> _log;

    public TerminalIdentityService(ApiDbContext db, LicenseSlotService slots, ILogger<TerminalIdentityService> log)
    {
        _db = db;
        _slots = slots;
        _log = log;
    }

    public async Task<(TerminalRegistration? Terminal, string? Error)> ValidateAsync(
        Guid terminalId,
        Guid installationId,
        string terminalToken,
        CancellationToken ct)
    {
        var t = await _db.TerminalRegistrations
            .FirstOrDefaultAsync(x => x.Id == terminalId, ct);
        if (t == null)
            return (null, "Terminal no encontrada.");
        if (t.InstallationId != installationId)
            return (null, "InstallationId no coincide.");
        if (!TerminalTokenHasher.Verify(terminalToken, t.TerminalTokenHash))
            return (null, "Token inválido.");
        if (!t.Active)
            return (null, "Terminal desactivada.");
        return (t, null);
    }

    public async Task AuditAsync(
        Guid terminalId,
        string eventType,
        string? details,
        string? ip,
        CancellationToken ct)
    {
        _db.TerminalAuditLogs.Add(new TerminalAuditLog
        {
            TerminalId = terminalId,
            EventType = eventType,
            Details = details,
            IpAddress = ip
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<TerminalEnterpriseRegisterResponse> RegisterAsync(
        TerminalEnterpriseRegisterRequest req,
        string? ip,
        CancellationToken ct)
    {
        if (req.InstallationId == Guid.Empty)
            return Deny("InstallationId requerido.", req.InstallationId);

        if (string.IsNullOrWhiteSpace(req.TerminalToken) || req.TerminalToken.Length < 16)
            return Deny("TerminalToken inválido.", req.InstallationId);

        var lic = await _db.LicenseIssuerRecords
            .AsNoTracking()
            .Where(x => x.ActivationId == req.ActivationId)
            .OrderByDescending(x => x.CreatedAtUtc)
            .FirstOrDefaultAsync(ct);

        var maxBoxes = await _slots.GetMaxBoxesAsync(req.ActivationId, ct);
        var now = DateTime.UtcNow;
        var activeCutoff = now - InactiveAfter;
        var tokenHash = TerminalTokenHasher.Hash(req.TerminalToken);

        var byInstall = await _db.TerminalRegistrations
            .FirstOrDefaultAsync(t => t.InstallationId == req.InstallationId, ct);

        var byFp = byInstall == null && !string.IsNullOrWhiteSpace(req.MachineFingerprint)
            ? await _db.TerminalRegistrations
                .FirstOrDefaultAsync(t => t.MachineFingerprint == req.MachineFingerprint, ct)
            : null;

        var existing = byInstall ?? byFp;
        var excludeTerminalId = existing?.Id ?? Guid.Empty;

        var activeSlots = await _db.TerminalRegistrations
            .Where(t => t.Active && t.LastHeartbeatUtc >= activeCutoff
                        && t.Id != excludeTerminalId)
            .CountAsync(ct);

        var (unifiedUsed, unifiedMax) = await _slots.GetUnifiedSlotUsageAsync(
            req.ActivationId, req.MachineFingerprint, ct: ct);

        if (existing != null)
        {
            existing.Active = true;
            existing.LastHeartbeatUtc = now;
            existing.Version = req.Version ?? existing.Version;
            existing.ActivationId = req.ActivationId ?? existing.ActivationId;
            existing.InstallationId ??= req.InstallationId;
            existing.TerminalTokenHash = tokenHash;
            if (req.CajaId is { } c && c != Guid.Empty) existing.CajaId = c;
            if (req.BranchId is { } b && b != Guid.Empty) existing.BranchId = b;
            if (!string.IsNullOrWhiteSpace(req.DisplayName)) existing.DisplayName = req.DisplayName.Trim();
            if (!string.IsNullOrWhiteSpace(ip)) existing.IpAddress = ip;
            existing.DeactivatedAtUtc = null;
            existing.DeactivatedReason = null;
            await _db.SaveChangesAsync(ct);

            await AuditAsync(existing.Id, "registered", "re-register", ip, ct);

            return new TerminalEnterpriseRegisterResponse
            {
                Granted = true,
                TerminalId = existing.Id,
                InstallationId = existing.InstallationId ?? req.InstallationId,
                SlotsInUse = unifiedUsed,
                SlotsTotal = unifiedMax
            };
        }

        if (!await _slots.CanRegisterNewTerminalAsync(req.MachineFingerprint, req.ActivationId, ct))
        {
            _log.LogWarning("Terminal enterprise denied install={Install} slots {Used}/{Max}",
                req.InstallationId, unifiedUsed, unifiedMax);
            return new TerminalEnterpriseRegisterResponse
            {
                Granted = false,
                Reason = LicenseSlotService.MensajeLimiteCajas(unifiedMax),
                InstallationId = req.InstallationId,
                SlotsInUse = unifiedUsed,
                SlotsTotal = unifiedMax
            };
        }

        var t = new TerminalRegistration
        {
            InstallationId = req.InstallationId,
            TerminalTokenHash = tokenHash,
            CajaId = req.CajaId is { } cid && cid != Guid.Empty ? cid : null,
            BranchId = req.BranchId is { } bid && bid != Guid.Empty ? bid : null,
            DisplayName = string.IsNullOrWhiteSpace(req.DisplayName) ? req.MachineName : req.DisplayName.Trim(),
            MachineFingerprint = string.IsNullOrWhiteSpace(req.MachineFingerprint)
                ? $"install-{req.InstallationId:N}"
                : req.MachineFingerprint,
            MachineName = req.MachineName ?? string.Empty,
            Version = req.Version ?? string.Empty,
            ActivationId = req.ActivationId ?? string.Empty,
            FirstSeenUtc = now,
            LastHeartbeatUtc = now,
            Active = true,
            IpAddress = ip
        };
        _db.TerminalRegistrations.Add(t);
        await _db.SaveChangesAsync(ct);
        await AuditAsync(t.Id, "registered", "new", ip, ct);

        _log.LogInformation("Terminal enterprise registered id={Id} install={Install}",
            t.Id, req.InstallationId);

        return new TerminalEnterpriseRegisterResponse
        {
            Granted = true,
            TerminalId = t.Id,
            InstallationId = req.InstallationId,
            SlotsInUse = activeSlots + 1,
            SlotsTotal = maxBoxes
        };
    }

    public async Task DeactivateAsync(Guid terminalId, string reason, CancellationToken ct)
    {
        var t = await _db.TerminalRegistrations.FirstOrDefaultAsync(x => x.Id == terminalId, ct);
        if (t == null) return;
        t.Active = false;
        t.DeactivatedAtUtc = DateTime.UtcNow;
        t.DeactivatedReason = reason;
        await _db.SaveChangesAsync(ct);
        await AuditAsync(terminalId, "deactivated", reason, null, ct);
    }

    private static TerminalEnterpriseRegisterResponse Deny(string reason, Guid install) =>
        new() { Granted = false, Reason = reason, InstallationId = install };
}
