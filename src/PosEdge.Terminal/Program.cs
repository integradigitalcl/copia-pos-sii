using PosEdge.Terminal.Client;
using PosEdge.Terminal.Replica;
using PosEdge.Terminal.TerminalConfig;

// Production-first runner:
// - Reads `appsettings.Production.json` (published alongside exe)
// - Persists terminalId (and cashSessionId) on first boot under %LocalAppData%\PosEdge\terminal-config\
//
// CLI overrides (optional):
//   PosEdge.Terminal.exe [apiBase] [tenantId] [branchId] [terminalId] [cashSessionId] [offlineMode] [replicaPath]
var cfg = TerminalAppConfigLoader.Load(args);
var replica = new TerminalReplicaDb(cfg.ReplicaPath);
var client = new TerminalClient(cfg.ApiBaseUrl, cfg.TenantId, cfg.BranchId, cfg.TerminalId, cfg.CashSessionId, replica)
{
    OfflineMode = cfg.OfflineMode
};

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

Console.Title = "PosEdge Multicaja";
Console.WriteLine("Iniciando PosEdge Multicaja...");

try
{
    await client.StartAsync(cts.Token);
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("No se pudo iniciar la terminal.");
    Console.WriteLine(ex.Message);
    Console.WriteLine();
    Console.WriteLine("Sugerencia: Menú Inicio → PosEdge Multicaja → Reparar instalación");
    Console.WriteLine("Luego use Self-check para verificar el sistema.");
    Console.WriteLine();
    Console.WriteLine("Presione Enter para salir.");
    Console.ReadLine();
    return 1;
}

_ = Task.Run(() => client.HeartbeatLoopAsync(cts.Token), cts.Token);
_ = Task.Run(() => client.ReplayLoopAsync(cts.Token), cts.Token);
_ = Task.Run(() => client.SyncLoopAsync(cts.Token), cts.Token);

Console.WriteLine("Terminal connected. Type 'sale' or 'quit'.");
Console.WriteLine($"- ApiBase:      {cfg.ApiBaseUrl}");
Console.WriteLine($"- TenantId:     {cfg.TenantId:D}");
Console.WriteLine($"- BranchId:     {cfg.BranchId:D}");
Console.WriteLine($"- TerminalId:   {cfg.TerminalId:D}");
Console.WriteLine($"- CashSession:  {cfg.CashSessionId:D}");
Console.WriteLine($"- Replica DB:   {cfg.ReplicaPath}");
Console.WriteLine($"- OfflineMode:  {client.OfflineMode}");
Console.WriteLine("Type 'status' to see UX status.");

while (!cts.IsCancellationRequested)
{
    var line = Console.ReadLine();
    if (line == null) continue;
    if (line.Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
    if (line.Equals("status", StringComparison.OrdinalIgnoreCase))
    {
        var st = client.GetUxStatus();
        Console.WriteLine($"status_code={st.StatusCode} blocking={st.Blocking} msg=\"{st.UserMessage}\" action=\"{st.SuggestedAction}\"");
        continue;
    }
    if (line.Equals("sale", StringComparison.OrdinalIgnoreCase))
    {
        var rid = PosEdge.Shared.Ulid.NewUlidString();
        var body = new
        {
            tenantId = cfg.TenantId,
            branchId = cfg.BranchId,
            terminalId = cfg.TerminalId,
            cashSessionId = cfg.CashSessionId,
            requestId = rid,
            currency = "MXN",
            discountTotal = 0m,
            lines = new[]
            {
                new { productId = Guid.Parse("33333333-3333-3333-3333-333333333333"), sku = "SKU-COCA-600", name = "Refresco 600ml", qty = 1m, unitPrice = 18.50m, taxRate = 0.16m }
            },
            payments = new[]
            {
                new { method = "cash", amount = 20m, currency = "MXN", meta = (object?)null }
            }
        };
        Console.WriteLine($"Enqueue Sale.Commit requestId={rid}");
        try
        {
            await client.SubmitSaleCommitAsync(body, rid, cts.Token);
        }
        catch (Exception ex)
        {
            // Keep terminal alive under bad local state (e.g., lease exhausted/expired) for chaos tests and real ops.
            Console.WriteLine($"Sale error requestId={rid}: {ex.Message}");
            replica.AppendReplayLog("sale.error", ex.Message, rid);
        }
    }
}

return 0;
