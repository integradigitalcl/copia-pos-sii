using System.Globalization;
using GrunflexPOS.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace GrunflexPOS.Web.Services;

public sealed class LocalReportsService(IDbContextFactory<LocalPosDbContext> dbFactory)
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CL");

    public async Task<ReportDashboard> GetDashboardAsync(
        DateTime fromLocal, DateTime toLocal,
        decimal ivaPercent = 19m, bool pricesIncludeTax = true,
        CancellationToken cancellationToken = default)
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
            .Include(x => x.Lines)
            .Where(x => x.CreatedAtUtc >= previousFromUtc && x.CreatedAtUtc < previousToUtc &&
                        !x.Cancelled && !x.PersonalConsumption)
            .ToListAsync(cancellationToken);

        var productIds = valid.SelectMany(x => x.Lines).Select(x => x.ProductId)
            .Concat(previousSales.SelectMany(x => x.Lines).Select(x => x.ProductId))
            .Where(id => id > 0).Distinct().ToArray();
        var productMeta = await db.Products.AsNoTracking()
            .Where(x => productIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Cost, x.Department })
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        decimal LineCost(LocalSaleLine line)
        {
            if (line.UnitCost > 0)
                return line.UnitCost;
            return productMeta.TryGetValue(line.ProductId, out var meta) ? meta.Cost : 0m;
        }

        string LineDepartment(LocalSaleLine line)
        {
            if (!string.IsNullOrWhiteSpace(line.Department))
                return line.Department.Trim();
            if (productMeta.TryGetValue(line.ProductId, out var meta) && !string.IsNullOrWhiteSpace(meta.Department))
                return meta.Department.Trim();
            return "General";
        }

        decimal SaleProfit(LocalSale sale) =>
            sale.Lines.Sum(line => line.Total - LineCost(line) * line.Quantity);

        var total = valid.Sum(x => x.Total);
        var profit = valid.Sum(SaleProfit);
        var previousTotal = previousSales.Sum(x => x.Total);
        var previousProfit = previousSales.Sum(SaleProfit);
        var previousTxn = previousSales.Count;
        var avgMargin = total == 0 ? 0 : profit / total * 100m;
        var previousAvgMargin = previousTotal == 0 ? 0 : previousProfit / previousTotal * 100m;

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

        var dayCount = Math.Max(1, (toLocal.Date - fromLocal.Date).Days);
        var useWeekdayLabels = dayCount <= 7;
        var daySeries = Enumerable.Range(0, dayCount).Select(offset =>
        {
            var date = fromLocal.Date.AddDays(offset);
            var daySales = valid.Where(x => x.CreatedAtUtc.ToLocalTime().Date == date).ToArray();
            var salesTotal = daySales.Sum(x => x.Total);
            var dayProfit = daySales.Sum(SaleProfit);
            var label = useWeekdayLabels
                ? Spanish.DateTimeFormat.GetDayName(date.DayOfWeek)
                : date.ToString("dd/MM", Spanish);
            if (useWeekdayLabels && label.Length > 0)
                label = char.ToUpper(label[0], Spanish) + label[1..];
            return new ReportDayPoint(date, label, salesTotal, dayProfit);
        }).ToArray();

        var days = daySeries.Select(x => new ReportPoint(x.Date, x.Sales)).ToArray();

        var deptGroups = lineItems
            .GroupBy(LineDepartment)
            .Select(g => new
            {
                Department = g.Key,
                Revenue = g.Sum(x => x.Total),
                Profit = g.Sum(x => x.Total - LineCost(x) * x.Quantity)
            })
            .OrderByDescending(x => x.Revenue)
            .ToArray();
        var topDepts = deptGroups.Take(4).ToArray();
        var otherDepts = deptGroups.Skip(4).ToArray();
        var departments = topDepts
            .Select(x => new ReportDepartmentRow(
                x.Department, x.Revenue, x.Profit,
                total == 0 ? 0 : x.Revenue / total * 100m))
            .Concat(otherDepts.Length == 0
                ? []
                : new[]
                {
                    new ReportDepartmentRow(
                        "Otros...",
                        otherDepts.Sum(x => x.Revenue),
                        otherDepts.Sum(x => x.Profit),
                        total == 0 ? 0 : otherDepts.Sum(x => x.Revenue) / total * 100m)
                })
            .ToArray();

        var (taxable, taxCollected) = ComputeTax(total, ivaPercent, pricesIncludeTax);

        return new ReportDashboard(
            total,
            valid.Length,
            lineItems.Sum(x => x.Quantity),
            valid.Length == 0 ? 0 : total / valid.Length,
            previousTotal,
            PercentChange(total, previousTotal),
            profit,
            previousProfit,
            PercentChange(profit, previousProfit),
            avgMargin,
            previousAvgMargin,
            PercentChange(avgMargin, previousAvgMargin),
            PercentChange(valid.Length, previousTxn),
            daySeries,
            days,
            Enumerable.Range(0, 24).Select(hour => new ReportPoint(
                new DateTime(2000, 1, 1, hour, 0, 0), valid
                    .Where(x => x.CreatedAtUtc.ToLocalTime().Hour == hour)
                    .Sum(x => x.Total))).ToArray(),
            valid.GroupBy(x => string.IsNullOrWhiteSpace(x.PaymentMethod) ? "Sin especificar" : x.PaymentMethod)
                .Select(group => new ReportPaymentRow(group.Key, group.Sum(x => x.Total), group.Count()))
                .OrderByDescending(x => x.Total).ToArray(),
            departments,
            products,
            valid30.Select(x => x.CreatedAtUtc.ToLocalTime().Date).Distinct().Count(),
            valid30.Sum(x => x.Total),
            valid30.Length,
            valid30.SelectMany(x => x.Lines).Sum(x => x.Quantity),
            valid30.SelectMany(x => x.Lines).GroupBy(x => x.ProductName)
                .OrderByDescending(group => group.Sum(x => x.Quantity))
                .Select(group => group.Key).FirstOrDefault() ?? "Sin ventas",
            personalConsumption,
            ivaPercent,
            taxCollected,
            taxable);
    }

    private static decimal PercentChange(decimal current, decimal previous) =>
        previous == 0 ? (current == 0 ? 0 : 100) : (current - previous) / previous * 100m;

    private static (decimal Taxable, decimal Collected) ComputeTax(
        decimal total, decimal ivaPercent, bool pricesIncludeTax)
    {
        if (total <= 0 || ivaPercent <= 0)
            return (0, 0);
        var rate = ivaPercent / 100m;
        if (pricesIncludeTax)
        {
            var taxable = Math.Round(total / (1m + rate), 2, MidpointRounding.AwayFromZero);
            return (taxable, Math.Round(total - taxable, 2, MidpointRounding.AwayFromZero));
        }

        var collected = Math.Round(total * rate, 2, MidpointRounding.AwayFromZero);
        return (total, collected);
    }

    public async Task<CashCloseReport?> GetCashCloseReportAsync(
        long sessionId, decimal? countedOverride = null,
        decimal ivaPercent = 19m, bool pricesIncludeTax = true,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.AsNoTracking()
            .Include(x => x.Movements)
            .SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken);
        if (session is null)
            return null;

        var sales = await db.Sales.AsNoTracking()
            .Where(x => x.CashSessionId == sessionId)
            .ToListAsync(cancellationToken);
        var closedAt = session.ClosedAtUtc ?? DateTime.UtcNow;
        // Consumos antiguos sin CashSessionId: asociar por ventana del turno.
        var orphanPersonal = await db.Sales.AsNoTracking()
            .Where(x => x.PersonalConsumption && !x.Cancelled && x.CashSessionId == null &&
                        x.CreatedAtUtc >= session.OpenedAtUtc && x.CreatedAtUtc <= closedAt)
            .ToListAsync(cancellationToken);
        if (orphanPersonal.Count > 0)
            sales = sales.Concat(orphanPersonal).ToList();

        var valid = sales.Where(x => !x.Cancelled && !x.PersonalConsumption).ToArray();
        var personalSales = sales.Where(x => !x.Cancelled && x.PersonalConsumption).ToArray();
        var personalConsumptionTotal = personalSales.Sum(x => x.Total);
        var personalConsumptionCount = personalSales.Length;
        var cancelledTotal = sales.Where(x => x.Cancelled).Sum(x => x.Total);

        decimal cashFromSales = 0m;
        decimal cardSales = 0m;
        decimal transferSales = 0m;
        decimal otherSales = 0m;
        foreach (var sale in valid)
        {
            var method = sale.PaymentMethod?.Trim() ?? string.Empty;
            if (method.Equals("Efectivo", StringComparison.OrdinalIgnoreCase))
            {
                cashFromSales += sale.Total;
            }
            else if (method.Equals("Mixto", StringComparison.OrdinalIgnoreCase))
            {
                var cashPart = Math.Clamp(sale.CashSessionAmount, 0, sale.Total);
                cashFromSales += cashPart;
                cardSales += Math.Max(0, sale.Total - cashPart);
            }
            else if (method.Equals("Tarjeta", StringComparison.OrdinalIgnoreCase))
            {
                cardSales += sale.Total;
            }
            else if (method.Equals("Transferencia", StringComparison.OrdinalIgnoreCase) ||
                     method.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
            {
                transferSales += sale.Total;
            }
            else
            {
                otherSales += sale.Total;
            }
        }

        // Misma fórmula que CloseCashSession / CashBalance: fondo + efectivo ventas + ingresos - egresos.
        // Consumo personal no ingresa dinero: no altera el efectivo esperado.
        var entries = session.TotalEntries;
        var exits = session.TotalExits;
        var cashSalesLedger = cashFromSales;
        var expectedCash = session.OpeningAmount + cashSalesLedger + entries - exits;
        var counted = countedOverride ?? session.ClosingAmount ?? expectedCash;
        var openedLocal = session.OpenedAtUtc.ToLocalTime();
        var closedLocal = closedAt.ToLocalTime();
        var duration = closedLocal - openedLocal;
        if (duration < TimeSpan.Zero)
            duration = TimeSpan.Zero;

        var totalSales = valid.Sum(x => x.Total);
        var txn = valid.Length;
        var avgTicket = txn == 0 ? 0 : totalSales / txn;
        var (taxable, taxCollected) = ComputeTax(totalSales, ivaPercent, pricesIncludeTax);
        var folio = $"CC-{closedLocal:yyyyMMdd}-{session.Id:D4}";

        return new CashCloseReport(
            Folio: folio,
            DateLocal: closedLocal.Date,
            RegisterName: string.IsNullOrWhiteSpace(session.RegisterName) ? "Caja 1" : session.RegisterName,
            CashierName: string.IsNullOrWhiteSpace(session.UserName) ? "Cajero" : session.UserName,
            OpenedAtLocal: openedLocal,
            ClosedAtLocal: closedLocal,
            Duration: duration,
            TotalSales: totalSales,
            Transactions: txn,
            AverageTicket: avgTicket,
            CashPayments: cashFromSales,
            CardPayments: cardSales,
            TransferPayments: transferSales,
            OtherPayments: otherSales,
            TaxRate: ivaPercent,
            TaxCollected: taxCollected,
            TaxableSales: taxable,
            OpeningAmount: session.OpeningAmount,
            CashFromSales: cashSalesLedger,
            CashExits: exits,
            CashEntries: entries,
            ExpectedCash: expectedCash,
            CountedCash: counted,
            Difference: counted - expectedCash,
            CancelledSales: cancelledTotal,
            PersonalConsumptionTotal: personalConsumptionTotal,
            PersonalConsumptionCount: personalConsumptionCount);
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
            query = query.Where(x => x.PaymentMethod == "Transferencia" || x.PaymentMethod == "Crédito");
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
        var validSales = sales.Where(x => !x.Cancelled && !x.PersonalConsumption).ToArray();
        var cashSales = validSales.Sum(LocalPosStore.ComputeSaleCashDrawerImpact);
        var cancelled = sales.Where(x => x.Cancelled).Sum(x => x.Total);
        // Totales de sesión (incluye DEVOLUCION en TotalExits); no mezclar solo RETIRO del movimiento.
        var entries = sessions.Sum(x => x.TotalEntries);
        var exits = sessions.Sum(x => x.TotalExits);
        var opening = sessions.Sum(x => x.OpeningAmount);
        // Esperado operativo del turno: fondo + ledger de efectivo de sesión + ingresos - egresos.
        var sessionCashSales = sessions.Sum(x => x.TotalSales);
        var expected = opening + sessionCashSales + entries - exits;
        var closed = sessions.Where(x => x.ClosingAmount.HasValue).Sum(x => x.ClosingAmount!.Value);
        return new ReportCashSummary(
            sessions.Count, opening, cashSales > 0 ? cashSales : sessionCashSales, entries, exits,
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

        var method = sale.PaymentMethod?.Trim() ?? string.Empty;
        decimal cashBase;
        if (method.Equals("Efectivo", StringComparison.OrdinalIgnoreCase))
            cashBase = saleTotalBefore;
        else if (method.Equals("Mixto", StringComparison.OrdinalIgnoreCase))
            cashBase = Math.Clamp(sale.CashSessionAmount, 0m, saleTotalBefore);
        else
            cashBase = 0m;
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

        if (sale.PaymentMethod.Equals("Transferencia", StringComparison.OrdinalIgnoreCase) ||
            sale.PaymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
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

    private static decimal GetSaleCashImpact(LocalSale sale) =>
        LocalPosStore.ComputeSaleCashDrawerImpact(sale);
}

public sealed record CashCloseReport(
    string Folio,
    DateTime DateLocal,
    string RegisterName,
    string CashierName,
    DateTime OpenedAtLocal,
    DateTime ClosedAtLocal,
    TimeSpan Duration,
    decimal TotalSales,
    int Transactions,
    decimal AverageTicket,
    decimal CashPayments,
    decimal CardPayments,
    decimal TransferPayments,
    decimal OtherPayments,
    decimal TaxRate,
    decimal TaxCollected,
    decimal TaxableSales,
    decimal OpeningAmount,
    decimal CashFromSales,
    decimal CashExits,
    decimal CashEntries,
    decimal ExpectedCash,
    decimal CountedCash,
    decimal Difference,
    decimal CancelledSales,
    decimal PersonalConsumptionTotal = 0,
    int PersonalConsumptionCount = 0);

public sealed record ReportDashboard(
    decimal Total, int Transactions, decimal Units, decimal AverageTicket,
    decimal PreviousTotal, decimal Variation,
    decimal Profit, decimal PreviousProfit, decimal ProfitVariation,
    decimal AvgMargin, decimal PreviousAvgMargin, decimal MarginVariation,
    decimal TxnVariation,
    IReadOnlyList<ReportDayPoint> DaySeries,
    IReadOnlyList<ReportPoint> Days,
    IReadOnlyList<ReportPoint> Hours, IReadOnlyList<ReportPaymentRow> Payments,
    IReadOnlyList<ReportDepartmentRow> Departments,
    IReadOnlyList<ReportProductRow> TopProducts, int ActiveDays30, decimal Total30,
    int Transactions30, decimal Units30, string LeadingProduct,
    IReadOnlyList<ReportPersonalConsumptionRow> PersonalConsumption,
    decimal TaxRate, decimal TaxCollected, decimal TaxableSales);
public sealed record ReportDayPoint(DateTime Date, string Label, decimal Sales, decimal Profit);
public sealed record ReportDepartmentRow(string Department, decimal Revenue, decimal Profit, decimal SharePercent);
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
