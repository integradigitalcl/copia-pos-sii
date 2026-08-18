using GrunflexPOS.API.Data;
using GrunflexPOS.API.Hubs;
using GrunflexPOS.API.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.API.Services;

public static class SyncDomains
{
    public const string Products = "products";
    public const string Inventory = "inventory";
    public const string Users = "users";
    public const string Cajas = "cajas";
    public const string Config = "config";
}

/// <summary>Registra cambios incrementales y opcionalmente notifica vía SignalR.</summary>
public sealed class SyncChangeRecorder
{
    private readonly ApiDbContext _db;
    private readonly IMulticajaSyncNotifier? _notifier;
    private readonly ILogger<SyncChangeRecorder> _log;

    public SyncChangeRecorder(
        ApiDbContext db,
        ILogger<SyncChangeRecorder> log,
        IMulticajaSyncNotifier? notifier = null)
    {
        _db = db;
        _log = log;
        _notifier = notifier;
    }

    public async Task RecordAsync(
        string domain,
        string entityId,
        string changeType,
        bool isDeleted = false,
        Guid? cajaId = null,
        CancellationToken ct = default)
    {
        var entry = new MulticajaSyncChangeLog
        {
            Domain = domain,
            EntityId = entityId,
            ChangeType = changeType,
            ChangedAtUtc = DateTime.UtcNow,
            IsDeleted = isDeleted
        };
        _db.MulticajaSyncChangeLogs.Add(entry);
        await _db.SaveChangesAsync(ct);

        _log.LogDebug("multicaja.sync.record domain={D} entity={E} type={T} cursor={C}",
            domain, entityId, changeType, entry.Id);

        if (_notifier != null)
            await _notifier.NotifyChangeAsync(domain, entityId, changeType, cajaId, ct);
    }

    public async Task RecordInventoryAsync(IEnumerable<int> productIds, Guid? cajaId, CancellationToken ct)
    {
        foreach (var pid in productIds.Distinct())
        {
            await RecordAsync(SyncDomains.Inventory, pid.ToString(), "inventory-adjusted", cajaId: cajaId, ct: ct);
            await RecordAsync(SyncDomains.Products, pid.ToString(), "product-updated", cajaId: cajaId, ct: ct);
        }
    }
}

public interface IMulticajaSyncNotifier
{
    Task NotifyChangeAsync(string domain, string entityId, string changeType, Guid? cajaId, CancellationToken ct);
    Task NotifySyncResetAsync(Guid? cajaId, CancellationToken ct);
}

public sealed class MulticajaSyncNotifier : IMulticajaSyncNotifier
{
    private readonly IHubContext<MulticajaSyncHub> _hub;
    private readonly ILogger<MulticajaSyncNotifier> _log;

    public MulticajaSyncNotifier(
        IHubContext<MulticajaSyncHub> hub,
        ILogger<MulticajaSyncNotifier> log)
    {
        _hub = hub;
        _log = log;
    }

    public async Task NotifyChangeAsync(string domain, string entityId, string changeType, Guid? cajaId, CancellationToken ct)
    {
        var payload = new { domain, entityId, changeType, atUtc = DateTime.UtcNow };
        await _hub.Clients.All.SendAsync(changeType, payload, ct);
        if (cajaId is { } c)
            await _hub.Clients.Group(MulticajaSyncHub.GroupForCaja(c)).SendAsync(changeType, payload, ct);
        _log.LogDebug("multicaja.hub notify {Type} {Domain}/{Entity}", changeType, domain, entityId);
    }

    public async Task NotifySyncResetAsync(Guid? cajaId, CancellationToken ct)
    {
        var payload = new { atUtc = DateTime.UtcNow };
        await _hub.Clients.All.SendAsync("sync-reset", payload, ct);
        if (cajaId is { } c)
            await _hub.Clients.Group(MulticajaSyncHub.GroupForCaja(c)).SendAsync("sync-reset", payload, ct);
    }
}
