using GrunflexPOS.API.DTOs;

namespace GrunflexPOS.API.Inventory;

public static class MulticajaInventoryContextFactory
{
    public static InventoryOperationContext FromVenta(MulticajaVentaCommitRequest req) =>
        new()
        {
            CajaId = req.CajaId,
            CajaSesionId = req.CajaSesionId,
            UserId = req.UsuarioId,
            TerminalId = req.TerminalId,
            TerminalCode = req.TerminalCode,
            UserSessionId = req.UserSessionId,
            BranchId = req.BranchId,
            RequestId = req.RequestId?.Trim()
        };

    public static InventoryOperationContext FromAnulacion(MulticajaAnularVentaRequest req) =>
        new()
        {
            CajaId = req.CajaId,
            CajaSesionId = req.CajaSesionId,
            UserId = req.UsuarioId,
            TerminalId = req.TerminalId,
            TerminalCode = req.TerminalCode,
            UserSessionId = req.UserSessionId,
            BranchId = req.BranchId,
            RequestId = req.RequestId?.Trim()
        };

    public static InventoryOperationContext FromDevolucion(MulticajaDevolucionLineaRequest req) =>
        new()
        {
            CajaId = req.CajaId,
            CajaSesionId = req.CajaSesionId,
            UserId = req.UsuarioId,
            TerminalId = req.TerminalId,
            TerminalCode = req.TerminalCode,
            UserSessionId = req.UserSessionId,
            BranchId = req.BranchId,
            RequestId = req.RequestId?.Trim()
        };

    public static InventoryOperationContext FromInventarioAjuste(MulticajaInventarioAjusteRequest req) =>
        new()
        {
            CajaId = req.CajaId,
            CajaSesionId = req.CajaSesionId,
            UserId = req.UsuarioId,
            TerminalId = req.TerminalId,
            TerminalCode = req.TerminalCode,
            UserSessionId = req.UserSessionId,
            BranchId = req.BranchId,
            RequestId = req.RequestId?.Trim()
        };
}
