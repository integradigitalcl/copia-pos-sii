using GrunflexPOS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.Web.Services;

public sealed class LocalReportsService(IDbContextFactory<LocalPosDbContext> dbFactory)
{
    public async Task<ReportDashboard> GetDashboardAsync(
        DateTime fromLocal, DateTime toLocal, CancellationToken cancellationToken = default)
    {
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sales = await db.Sales.AsNoTracking()
            .Include(x => x.Lines)
            .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc)
            .ToListAsync(cancellationToken);
        var valid = sales.Where(x => !x.Cancelled && !x.PersonalConsumption).ToArray();

        var previousLength = toLocal - fromLocal;
        var previousFromUtc = fromLocal.Subtract(previousLength).ToUniversalTime();
        var previousToUtc = fromLocal.ToUniversalTime();
        var previousSales = await db.Sales.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= previousFromUtc && x.CreatedAtUtc < previousToUtc &&
                        !x.Cancelled && !x.PersonalConsumption)
            .Select(x => x.Total).ToListAsync(cancellationToken);
        var previousTotal = previousSales.Sum();

        var last30FromUtc = DateTime.Today.AddDays(-29).ToUniversalTime();
        var last30ToUtc = DateTime.Today.AddDays(1).ToUniversalTime();
        var last30 = await db.Sales.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.CreatedAtUtc >= last30FromUtc && x.CreatedAtUtc < last30ToUtc)
            .ToListAsync(cancellationToken);
        var valid30 = last30.Where(x => !x.Cancelled && !x.PersonalConsumption).ToArray();
        var lineItems = valid.SelectMany(x => x.Lines).ToArray();
        var products = lineItems
            .GroupBy(x => new { x.ProductId, x.ProductName, x.Code })
            .Select(group => new ReportProductRow(
                group.Key.ProductId, group.Key.Code, group.Key.ProductName,
                group.Sum(x => x.Quantity), group.Sum(x => x.Total)))
            .OrderByDescending(x => x.Quantity).ThenByDescending(x => x.Revenue)
            .Take(5).ToArray();
        var personalConsumption = sales
            .Where(x => !x.Cancelled && x.PersonalConsumption)
            .SelectMany(sale => sale.Lines.Select(line => new ReportPersonalConsumptionRow(
                line.ProductName, line.Code, line.Quantity, line.Total,
                sale.UserName, sale.TicketNumber, sale.CreatedAtUtc)))
            .OrderByDescending(x => x.CreatedAtUtc).ThenByDescending(x => x.TicketNumber)
            .ToArray();

        return new ReportDashboard(
            valid.Sum(x => x.Total),
            valid.Length,
            lineItems.Sum(x => x.Quantity),
            valid.Length == 0 ? 0 : valid.Sum(x => x.Total) / valid.Length,
            previousTotal,
            previousTotal == 0 ? (valid.Length == 0 ? 0 : 100) :
                (valid.Sum(x => x.Total) - previousTotal) / previousTotal * 100,
            Enumerable.Range(0, Math.Max(1, (toLocal.Date - fromLocal.Date).Days))
                .Select(offset =>
                {
                    var date = fromLocal.Date.AddDays(offset);
                    return new ReportPoint(date, valid.Where(x => x.CreatedAtUtc.ToLocalTime().Date == date)
                        .Sum(x => x.Total));
                }).ToArray(),
            Enumerable.Range(0, 24).Select(hour => new ReportPoint(
                new DateTime(2000, 1, 1, hour, 0, 0), valid
                    .Where(x => x.CreatedAtUtc.ToLocalTime().Hour == hour)
                    .Sum(x => x.Total))).ToArray(),
            valid.GroupBy(x => string.IsNullOrWhiteSpace(x.PaymentMethod) ? "Sin especificar" : x.PaymentMethod)
                .Select(group => new ReportPaymentRow(group.Key, group.Sum(x => x.Total), group.Count()))
                .OrderByDescending(x => x.Total).ToArray(),
            products,
            valid30.Select(x => x.CreatedAtUtc.ToLocalTime().Date).Distinct().Count(),
            valid30.Sum(x => x.Total),
            valid30.Length,
            valid30.SelectMany(x => x.Lines).Sum(x => x.Quantity),
            valid30.SelectMany(x => x.Lines).GroupBy(x => x.ProductName)
                .OrderByDescending(group => group.Sum(x => x.Quantity))
                .Select(group => group.Key).FirstOrDefault() ?? "Sin ventas",
            personalConsumption);
    }

    public async Task<ReportSalesPage> GetSalesAsync(
        DateTime fromLocal, DateTime toLocal, string? search, string? cashier,
        bool creditOnly, int page, int pageSize, CancellationToken cancellationToken = default)
    {
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Sales.AsNoTracking()
            .Where(x => x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc);
        if (!string.IsNullOrWhiteSpace(cashier) && cashier != "Todos")
            query = query.Where(x => x.UserName == cashier);
        if (creditOnly)
            query = query.Where(x => x.PaymentMethod == "Crédito");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.UserName.Contains(term) ||
                                     x.Customer.Contains(term) ||
                                     x.TicketNumber.ToString().Contains(term));
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.CreatedAtUtc)
            .Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize)
            .Select(x => new ReportSaleRow(
                x.Id, x.TicketNumber, x.CreatedAtUtc, x.UserName, x.Customer,
                x.PaymentMethod, x.Total, x.Discount, x.Lines.Count,
                x.Cancelled, x.PersonalConsumption, x.EditCount > 0))
            .ToListAsync(cancellationToken);
        return new ReportSalesPage(rows, total);
    }

    public async Task<ReportSaleDetail?> GetSaleDetailAsync(
        long saleId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sale = await db.Sales.AsNoTracking()
            .Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == saleId, cancellationToken);
        if (sale is null)
            return null;

        var edits = await db.SaleEdits.AsNoTracking()
            .Where(x => x.SaleId == saleId)
            .OrderByDescending(x => x.EditedAtUtc)
            .Select(x => new ReportSaleEdit(x.Id, x.EditedAtUtc, x.UserName, x.Summary))
            .ToListAsync(cancellationToken);

        return new ReportSaleDetail(
            sale.Id, sale.TicketNumber, sale.CreatedAtUtc, sale.UserName, sale.Customer,
            sale.PaymentMethod, sale.Subtotal, sale.Discount, sale.Total, sale.ReceivedAmount,
            sale.ChangeAmount, sale.CashSessionId, sale.Cancelled, sale.PersonalConsumption,
            sale.EditCount > 0, sale.EditedAtUtc, sale.EditedByUserName, edits,
            sale.Lines.Where(line => line.Quantity > 0)
                .Select(line => new ReportSaleLine(
                    line.Id, line.ProductId, line.Code, line.ProductName,
                    line.Quantity, line.ListUnitPrice, line.UnitPrice, line.Total))
                .ToArray());
    }

    public async Task<IReadOnlyList<string>> GetCashiersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sales.AsNoTracking().Select(x => x.UserName).Distinct()
            .OrderBy(x => x).ToListAsync(cancellationToken);
    }

    public async Task<ReportCashSummary> GetCashSummaryAsync(
        DateTime fromLocal, DateTime toLocal, long? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sessionsQuery = db.CashSessions.AsNoTracking()
            .Include(x => x.Movements)
            .Where(x => x.OpenedAtUtc < toUtc &&
                        (x.ClosedAtUtc == null || x.ClosedAtUtc >= fromUtc));
        if (sessionId is not null)
            sessionsQuery = sessionsQuery.Where(x => x.Id == sessionId);
        var sessions = await sessionsQuery.ToListAsync(cancellationToken);
        var ids = sessions.Select(x => x.Id).ToArray();
        var sales = await db.Sales.AsNoTracking()
            .Where(x => x.CashSessionId != null && ids.Contains(x.CashSessionId.Value) &&
                        x.CreatedAtUtc >= fromUtc && x.CreatedAtUtc < toUtc)
            .ToListAsync(cancellationToken);
        var valid = sales.Where(x => !x.Cancelled && !x.PersonalConsumption &&
                                     x.PaymentMethod == "Efectivo").ToArray();
        var cancelled = sales.Where(x => x.Cancelled).Sum(x => x.Total);
        var entries = sessions.SelectMany(x => x.Movements)
            .Where(x => x.Type is "INGRESO" or "ENTRADA").Sum(x => x.Amount);
        var exits = sessions.SelectMany(x => x.Movements)
            .Where(x => x.Type is "RETIRO" or "SALIDA").Sum(x => x.Amount);
        var opening = sessions.Sum(x => x.OpeningAmount);
        var expected = opening + valid.Sum(x => x.Total) + entries - exits;
        var closed = sessions.Where(x => x.ClosingAmount.HasValue).Sum(x => x.ClosingAmount!.Value);
        return new ReportCashSummary(
            sessions.Count, opening, valid.Sum(x => x.Total), entries, exits,
            cancelled, expected, closed, closed - expected,
            sessions.OrderByDescending(x => x.OpenedAtUtc)
                .Select(x => new ReportCashSessionRow(x.Id, x.RegisterName, x.UserName,
                    x.OpenedAtUtc, x.ClosedAtUtc, x.OpeningAmount, x.ClosingAmount,
                    x.Difference, x.Open))
                .ToArray());
    }

    public async Task<IReadOnlyList<ReportCashSessionRow>> GetCashSessionsAsync(
        DateTime fromLocal, DateTime toLocal, CancellationToken cancellationToken = default)
    {
        var fromUtc = fromLocal.ToUniversalTime();
        var toUtc = toLocal.ToUniversalTime();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CashSessions.AsNoTracking()
            .Where(x => x.OpenedAtUtc < toUtc && (x.ClosedAtUtc == null || x.ClosedAtUtc >= fromUtc))
            .OrderByDescending(x => x.OpenedAtUtc)
            .Select(x => new ReportCashSessionRow(x.Id, x.RegisterName, x.UserName,
                x.OpenedAtUtc, x.ClosedAtUtc, x.OpeningAmount, x.ClosingAmount,
                x.Difference, x.Open))
            .ToListAsync(cancellationToken);
    }

    public async Task<ReportActionResult> CancelSaleAsync(
        long saleId, string userName, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sale = await db.Sales.Include(x => x.Lines).SingleOrDefaultAsync(
            x => x.Id == saleId, cancellationToken);
        if (sale is null || sale.Cancelled)
            return ReportActionResult.Failed("La venta ya está anulada o no existe.");

        foreach (var line in sale.Lines)
        {
            var product = await db.Products.SingleOrDefaultAsync(x => x.Id == line.ProductId, cancellationToken);
            if (product is null)
                continue;
            var before = product.Stock;
            product.Stock += line.Quantity;
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ProductId = product.Id, ProductCode = product.Code, ProductName = product.Name,
                Quantity = line.Quantity, StockBefore = before, StockAfter = product.Stock,
                Type = "ANULACION", Reference = $"Venta #{sale.TicketNumber}", UserName = userName
            });
        }
        sale.Cancelled = true;
        sale.CancelledAtUtc = DateTime.UtcNow;
        if (sale.CashSessionId is not null)
        {
            var cashImpact = GetSaleCashImpact(sale);
            if (cashImpact > 0)
            {
                var session = await db.CashSessions.SingleOrDefaultAsync(
                    x => x.Id == sale.CashSessionId, cancellationToken);
                if (session is not null)
                {
                    session.TotalSales = Math.Max(0, session.TotalSales - cashImpact);
                    db.CashMovements.Add(new LocalCashMovement
                    {
                        CashSessionId = session.Id, Type = "ANULACION",
                        Amount = -cashImpact, Description = $"Anulación Ticket #{sale.TicketNumber}"
                    });
                }
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReportActionResult.Successful("Venta anulada y stock restaurado.");
    }

    public async Task<ReportActionResult> RefundLineAsync(
        long saleId, long lineId, decimal quantity, string userName,
        CancellationToken cancellationToken = default)
    {
        if (quantity <= 0)
            return ReportActionResult.Failed("La cantidad a devolver debe ser mayor a cero.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sale = await db.Sales.Include(x => x.Lines).SingleOrDefaultAsync(
            x => x.Id == saleId, cancellationToken);
        var line = sale?.Lines.SingleOrDefault(x => x.Id == lineId);
        if (sale is null || line is null || sale.Cancelled || quantity > line.Quantity)
            return ReportActionResult.Failed("No se puede devolver esa cantidad.");

        var summary = await ApplyLineQuantityReductionAsync(
            db, sale, line, quantity, userName, cancellationToken);
        RecordSaleEdit(db, sale, userName, summary);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReportActionResult.Successful("Artículo devuelto y stock restaurado.");
    }

    public async Task<ReportActionResult> EditSaleAsync(
        long saleId, IReadOnlyList<EditSaleLineRequest> edits, string userName,
        CancellationToken cancellationToken = default)
    {
        if (edits.Count == 0)
            return ReportActionResult.Failed("No hay cambios para guardar.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var sale = await db.Sales.Include(x => x.Lines).SingleOrDefaultAsync(
            x => x.Id == saleId, cancellationToken);
        if (sale is null || sale.Cancelled)
            return ReportActionResult.Failed("La venta ya está anulada o no existe.");

        var summaries = new List<string>();
        foreach (var edit in edits)
        {
            var line = sale.Lines.SingleOrDefault(x => x.Id == edit.LineId);
            if (line is null)
                return ReportActionResult.Failed("Uno de los artículos ya no existe en el ticket.");
            if (edit.NewQuantity > line.Quantity)
                return ReportActionResult.Failed("Solo puedes reducir cantidades del ticket.");
            if (edit.NewQuantity == line.Quantity)
                continue;

            var removed = line.Quantity - edit.NewQuantity;
            summaries.Add(await ApplyLineQuantityReductionAsync(
                db, sale, line, removed, userName, cancellationToken));
        }

        if (summaries.Count == 0)
            return ReportActionResult.Failed("No hay cambios para guardar.");

        RecordSaleEdit(db, sale, userName, string.Join("; ", summaries));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ReportActionResult.Successful("Ticket editado y stock actualizado.");
    }

    private static async Task<string> ApplyLineQuantityReductionAsync(
        LocalPosDbContext db, LocalSale sale, LocalSaleLine line, decimal quantity,
        string userName, CancellationToken cancellationToken)
    {
        var lineQuantityBefore = line.Quantity;
        var saleTotalBefore = sale.Total;
        var amount = Math.Round(line.Total * quantity / lineQuantityBefore, 2, MidpointRounding.AwayFromZero);
        var cashImpact = ComputeCashRefundAmount(sale, saleTotalBefore, amount);
        var productName = line.ProductName;
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == line.ProductId, cancellationToken);
        if (product is not null)
        {
            var before = product.Stock;
            product.Stock += quantity;
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ProductId = product.Id, ProductCode = product.Code, ProductName = product.Name,
                Quantity = quantity, StockBefore = before, StockAfter = product.Stock,
                Type = "DEVOLUCION", Reference = $"Venta #{sale.TicketNumber}", UserName = userName
            });
        }

        line.Quantity -= quantity;
        line.Total = Math.Max(0, line.Total - amount);
        if (line.Quantity <= 0)
        {
            db.SaleLines.Remove(line);
            sale.Lines.Remove(line);
        }

        RecalculateSaleTotals(sale);
        ApplySaleCashRefund(sale, cashImpact);
        await RegisterSaleCashRefundAsync(db, sale, cashImpact, cancellationToken);

        return $"Quitó {quantity:0.##} × {productName}";
    }

    private static async Task RegisterSaleCashRefundAsync(
        LocalPosDbContext db, LocalSale sale, decimal cashImpact, CancellationToken cancellationToken)
    {
        if (cashImpact <= 0 || sale.CashSessionId is null)
            return;

        var openSession = await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (openSession is not null && openSession.Id == sale.CashSessionId)
        {
            openSession.TotalSales = Math.Max(0, openSession.TotalSales - cashImpact);
            db.CashMovements.Add(new LocalCashMovement
            {
                CashSessionId = openSession.Id, Type = "DEVOLUCION",
                Amount = -cashImpact, Description = $"Devolución Ticket #{sale.TicketNumber}"
            });
            return;
        }

        if (openSession is not null)
        {
            openSession.TotalExits += cashImpact;
            db.CashMovements.Add(new LocalCashMovement
            {
                CashSessionId = openSession.Id, Type = "DEVOLUCION",
                Amount = cashImpact, Description = $"Devolución Ticket #{sale.TicketNumber}"
            });
            return;
        }

        var saleSession = await db.CashSessions.SingleOrDefaultAsync(
            x => x.Id == sale.CashSessionId, cancellationToken);
        if (saleSession is null)
            return;

        saleSession.TotalSales = Math.Max(0, saleSession.TotalSales - cashImpact);
        db.CashMovements.Add(new LocalCashMovement
        {
            CashSessionId = saleSession.Id, Type = "DEVOLUCION",
            Amount = -cashImpact, Description = $"Devolución Ticket #{sale.TicketNumber}"
        });
    }

    private static void RecalculateSaleTotals(LocalSale sale)
    {
        var activeLines = sale.Lines.Where(x => x.Quantity > 0).ToArray();
        sale.Subtotal = activeLines.Sum(x =>
            Math.Round(x.ListUnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero));
        var linesTotal = activeLines.Sum(x => x.Total);
        sale.Discount = Math.Max(0m, sale.Subtotal - linesTotal);
        sale.Total = Math.Max(0m, linesTotal);
    }

    private static decimal ComputeCashRefundAmount(LocalSale sale, decimal saleTotalBefore, decimal refundAmount)
    {
        if (sale.PersonalConsumption || sale.CashSessionId is null || refundAmount <= 0 || saleTotalBefore <= 0)
            return 0m;

        var cashBase = sale.CashSessionAmount > 0
            ? sale.CashSessionAmount
            : sale.PaymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase)
                ? saleTotalBefore
                : 0m;
        if (cashBase <= 0)
            return 0m;

        return Math.Round(cashBase * refundAmount / saleTotalBefore, 2, MidpointRounding.AwayFromZero);
    }

    private static void ApplySaleCashRefund(LocalSale sale, decimal cashImpact)
    {
        if (cashImpact <= 0)
            return;

        if (sale.CashSessionAmount > 0)
            sale.CashSessionAmount = Math.Max(0, sale.CashSessionAmount - cashImpact);

        if (sale.PaymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
            return;

        sale.ReceivedAmount = Math.Max(0, sale.ReceivedAmount - cashImpact);
        sale.ChangeAmount = Math.Max(0, sale.ReceivedAmount - sale.Total);
    }

    private static void RecordSaleEdit(LocalPosDbContext db, LocalSale sale, string userName, string summary)
    {
        sale.EditedAtUtc = DateTime.UtcNow;
        sale.EditedByUserName = userName;
        sale.EditCount += 1;
        db.SaleEdits.Add(new LocalSaleEdit
        {
            SaleId = sale.Id,
            EditedAtUtc = DateTime.UtcNow,
            UserName = userName,
            Summary = summary
        });
    }

    private static decimal GetSaleCashImpact(LocalSale sale)
    {
        if (sale.CashSessionAmount > 0)
            return sale.CashSessionAmount;
        return sale.PaymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase) ? sale.Total : 0m;
    }
}

public sealed record ReportDashboard(
    decimal Total, int Transactions, decimal Units, decimal AverageTicket,
    decimal PreviousTotal, decimal Variation, IReadOnlyList<ReportPoint> Days,
    IReadOnlyList<ReportPoint> Hours, IReadOnlyList<ReportPaymentRow> Payments,
    IReadOnlyList<ReportProductRow> TopProducts, int ActiveDays30, decimal Total30,
    int Transactions30, decimal Units30, string LeadingProduct,
    IReadOnlyList<ReportPersonalConsumptionRow> PersonalConsumption);
public sealed record ReportPersonalConsumptionRow(
    string ProductName, string Code, decimal Quantity, decimal Total,
    string Cashier, long TicketNumber, DateTime CreatedAtUtc);
public sealed record ReportPoint(DateTime Label, decimal Total);
public sealed record ReportPaymentRow(string Method, decimal Total, int Transactions);
public sealed record ReportProductRow(int ProductId, string Code, string Name, decimal Quantity, decimal Revenue);
public sealed record ReportSalesPage(IReadOnlyList<ReportSaleRow> Rows, int TotalCount);
public sealed record ReportSaleRow(long Id, long TicketNumber, DateTime CreatedAtUtc, string UserName,
    string Customer, string PaymentMethod, decimal Total, decimal Discount, int ItemCount,
    bool Cancelled, bool PersonalConsumption, bool Edited);
public sealed record ReportSaleDetail(long Id, long TicketNumber, DateTime CreatedAtUtc, string UserName,
    string Customer, string PaymentMethod, decimal Subtotal, decimal Discount, decimal Total,
    decimal ReceivedAmount, decimal ChangeAmount, long? CashSessionId, bool Cancelled, bool PersonalConsumption,
    bool Edited, DateTime? EditedAtUtc, string? EditedByUserName,
    IReadOnlyList<ReportSaleEdit> Edits, IReadOnlyList<ReportSaleLine> Lines);
public sealed record ReportSaleEdit(long Id, DateTime EditedAtUtc, string UserName, string Summary);
public sealed record EditSaleLineRequest(long LineId, decimal NewQuantity);
public sealed record ReportSaleLine(long Id, int ProductId, string Code, string ProductName,
    decimal Quantity, decimal ListUnitPrice, decimal UnitPrice, decimal Total);
public sealed record ReportActionResult(bool Success, string Message)
{
    public static ReportActionResult Successful(string message) => new(true, message);
    public static ReportActionResult Failed(string message) => new(false, message);
}
public sealed record ReportCashSummary(int Sessions, decimal Opening, decimal CashSales,
    decimal Entries, decimal Exits, decimal Cancelled, decimal Expected, decimal Closed,
    decimal Difference, IReadOnlyList<ReportCashSessionRow> History);
public sealed record ReportCashSessionRow(long Id, string RegisterName, string UserName,
    DateTime OpenedAtUtc, DateTime? ClosedAtUtc, decimal OpeningAmount,
    decimal? ClosingAmount, decimal Difference, bool Open);
