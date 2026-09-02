using Microsoft.AspNetCore.Components.Server.Circuits;

namespace GrunflexPOS.Web.Services.Diagnostics;

public sealed class PosCircuitDiagnostics(ILogger<PosCircuitDiagnostics> logger) : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogInformation("Circuito Blazor abierto: {CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        logger.LogWarning("Circuito Blazor cerrado: {CircuitId}", circuit.Id);
        return Task.CompletedTask;
    }
}
