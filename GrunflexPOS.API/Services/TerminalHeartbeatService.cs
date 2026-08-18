using GrunflexPOS.API.Data;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Models;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

/// <summary>
/// Heartbeat enterprise con throttling (no spam DB) y registro en TerminalHeartbeats.
/// </summary>
public sealed class TerminalHeartbeatService
{
    public static readonly TimeSpan MinPersistInterval = TimeSpan.FromSeconds(45);
    public static readonly TimeSpan OfflineThreshold = TimeSpan.FromMinutes(3);

    private readonly ApiDbContext _db;
    private readonly TerminalIdentityService _identity;
    private readonly ILogger<TerminalHeartbeatService> _log;

    public TerminalHeartbeatService(
        ApiDbContext db,
        TerminalIdentityService identity,
        ILogger<TerminalHeartbeatService> log)
    {
        _db = db;
        _identity = identity;
        _log = log;
    }

    public async Task<TerminalEnterpriseHeartbeatResponse> BeatAsync(
        TerminalEnterpriseHeartbeatRequest req,
        string? ip,
        CancellationToken ct)
    {
        var (terminal, err) = await _identity.ValidateAsync(
            req.TerminalId, req.InstallationId, req.TerminalToken, ct);
        if (terminal == null)
        {
            return new TerminalEnterpriseHeartbeatResponse
            {
                Authorized = false,
                Reason = err,
                TerminalActive = false
            };
        }

        var now = DateTime.UtcNow;
        terminal.LastHeartbeatUtc = now;
        terminal.Version = req.Version ?? terminal.Version;
        if (req.CajaId is { } c && c != Guid.Empty) terminal.CajaId = c;
        if (!string.IsNullOrWhiteSpace(req.DisplayName)) terminal.DisplayName = req.DisplayName.Trim();
        if (!string.IsNullOrWhiteSpace(ip)) terminal.IpAddress = ip;

        var hb = await _db.TerminalHeartbeats
            .FirstOrDefaultAsync(x => x.TerminalId == req.TerminalId, ct);

        var shouldPersist = req.Reconnect
                            || hb == null
                            || now - hb.LastSeenAtUtc >= MinPersistInterval;

        if (shouldPersist)
        {
            if (hb == null)
            {
                hb = new TerminalHeartbeatRecord { TerminalId = req.TerminalId };
                _db.TerminalHeartbeats.Add(hb);
            }

            hb.LastSeenAtUtc = now;
            hb.CajaId = req.CajaId ?? terminal.CajaId;
            hb.CurrentUserId = req.CurrentUserId;
            hb.CurrentSessionId = req.CurrentSessionId;
            hb.ClientVersion = req.Version;
            hb.IpAddress = ip;
        }

        await _db.SaveChangesAsync(ct);

        if (req.Reconnect)
            _log.LogInformation("multicaja.heartbeat reconnect terminal={T} caja={C}", req.TerminalId, req.CajaId);

        return new TerminalEnterpriseHeartbeatResponse
        {
            Authorized = true,
            TerminalActive = terminal.Active,
            ServerTimeUtc = now
        };
    }

    public static bool IsOffline(DateTime? lastSeenUtc, DateTime? nowUtc = null)
    {
        if (lastSeenUtc == null) return true;
        return (nowUtc ?? DateTime.UtcNow) - lastSeenUtc.Value > OfflineThreshold;
    }

    public async Task<IReadOnlyList<TerminalHeartbeatRecord>> ListAsync(CancellationToken ct) =>
        await _db.TerminalHeartbeats.AsNoTracking()
            .OrderByDescending(x => x.LastSeenAtUtc)
            .ToListAsync(ct);
}
