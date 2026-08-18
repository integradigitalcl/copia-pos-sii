using Microsoft.AspNetCore.SignalR;

namespace GrunflexPOS.API.Hubs;

/// <summary>
/// Hub minimalista: eventos de invalidación (no catálogos completos).
/// Cliente hace delta pull tras recibir evento.
/// </summary>
public sealed class MulticajaSyncHub : Hub
{
    public static string GroupForCaja(Guid cajaId) => $"caja-{cajaId:N}";

    public Task JoinCaja(string cajaId)
    {
        if (!Guid.TryParse(cajaId, out var id) || id == Guid.Empty)
            return Task.CompletedTask;
        return Groups.AddToGroupAsync(Context.ConnectionId, GroupForCaja(id));
    }

    public Task LeaveCaja(string cajaId)
    {
        if (!Guid.TryParse(cajaId, out var id) || id == Guid.Empty)
            return Task.CompletedTask;
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupForCaja(id));
    }
}
