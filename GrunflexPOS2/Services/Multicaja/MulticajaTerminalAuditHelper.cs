using GrunflexPOS2.Services.Multicaja.Terminal;

namespace GrunflexPOS2.Services.Multicaja;

/// <summary>
/// Rellena campos de auditoría inventario (terminal / sesión) en requests multicaja.
/// </summary>
public static class MulticajaTerminalAuditHelper
{
    public static void Enrich(MulticajaVentaCommitRequest req) =>
        Apply(req, req.CajaSesionId);

    public static void Enrich(MulticajaAnularVentaRequest req) =>
        Apply(req, req.CajaSesionId);

    public static void Enrich(MulticajaDevolucionLineaRequest req) =>
        Apply(req, req.CajaSesionId);

    public static void Enrich(MulticajaInventarioAjusteRequest req) =>
        Apply(req, req.CajaSesionId);

    private static void Apply(IMulticajaInventoryAuditRequest req, Guid cajaSesionId)
    {
        var (terminalId, terminalCode, branchId) = ResolveTerminal();
        if (terminalId.HasValue && terminalId.Value != Guid.Empty)
            req.TerminalId = terminalId;
        if (!string.IsNullOrWhiteSpace(terminalCode))
            req.TerminalCode = terminalCode;
        if (cajaSesionId != Guid.Empty)
            req.UserSessionId = cajaSesionId;
        if (branchId.HasValue && branchId.Value != Guid.Empty)
            req.BranchId = branchId;
    }

    private static (Guid? TerminalId, string? TerminalCode, Guid? BranchId) ResolveTerminal()
    {
        var term = App.Terminal;
        if (term != null)
        {
            var id = term.TerminalId;
            if (id is { } tid && tid != Guid.Empty)
            {
                var code = NormalizeCode(term.Identity.DisplayName) ?? NormalizeCode(Environment.MachineName);
                return (tid, code, term.Identity.BranchId);
            }
        }

        try
        {
            var snap = new TerminalIdentityStore().LoadOrCreate();
            if (snap.TerminalId is { } stored && stored != Guid.Empty)
            {
                var code = NormalizeCode(snap.DisplayName) ?? NormalizeCode(Environment.MachineName);
                return (stored, code, snap.BranchId);
            }
        }
        catch
        {
            // noop
        }

        return (null, NormalizeCode(Environment.MachineName), null);
    }

    private static string? NormalizeCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var t = value.Trim();
        return t.Length > 64 ? t[..64] : t;
    }
}

/// <summary>Campos opcionales de auditoría inventario en operaciones multicaja.</summary>
public interface IMulticajaInventoryAuditRequest
{
    Guid? TerminalId { get; set; }
    string? TerminalCode { get; set; }
    Guid? UserSessionId { get; set; }
    Guid? BranchId { get; set; }
}
