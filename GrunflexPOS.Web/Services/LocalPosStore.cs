using GrunflexPOS.Web.Data;
using GrunflexPOS.Web.Services.Licensing;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GrunflexPOS.Web.Services;

public sealed class LocalPosStore(
    IDbContextFactory<LocalPosDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<LocalPosStore> logger)
{
    private static readonly (string Code, string Name, string Category, decimal Price, decimal Stock, string Unit, string Accent)[] SeedProducts =
    [
        ("7790895000011", "Pan blanco molde", "Panadería", 1890m, 24m, "un.", "#2563EB"),
        ("7790895000028", "Leche entera 1L", "Lácteos", 1190m, 36m, "un.", "#7C3AED"),
        ("7790895000035", "Café instantáneo", "Despensa", 4590m, 12m, "un.", "#0891B2"),
        ("7790895000042", "Arroz grano largo 1kg", "Despensa", 1690m, 42m, "un.", "#EA580C"),
        ("7790895000059", "Bebida cola 1.5L", "Bebidas", 1990m, 18m, "un.", "#DB2777"),
        ("7790895000066", "Agua mineral 1.5L", "Bebidas", 990m, 31m, "un.", "#16A34A"),
        ("7790895000073", "Huevos bandeja 12", "Lácteos", 3990m, 9m, "un.", "#CA8A04"),
        ("7790895000080", "Detergente líquido", "Limpieza", 5290m, 7m, "un.", "#059669"),
        ("7790895000097", "Galletas chocolate", "Despensa", 1490m, 20m, "un.", "#F97316"),
        ("7790895000103", "Jugo natural 1L", "Bebidas", 2290m, 15m, "un.", "#E11D48"),
        ("7790895000110", "Queso laminado 250g", "Lácteos", 3490m, 11m, "un.", "#0EA5E9"),
        ("7790895000127", "Papel higiénico 4 un.", "Limpieza", 2990m, 16m, "un.", "#6366F1")
    ];

    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSalesColumnsAsync(db, cancellationToken);
        if (await db.Products.AnyAsync(cancellationToken))
            return;

        db.Products.AddRange(SeedProducts.Select(x => new LocalProduct
        {
            Code = x.Code, Name = x.Name, Category = x.Category, Price = x.Price,
            Cost = x.Price, WholesalePrice = x.Price, Stock = x.Stock, Unit = x.Unit, Accent = x.Accent
        }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task EnsureSalesColumnsAsync(LocalPosDbContext db, CancellationToken cancellationToken)
    {
        var columns = new[]
        {
            ("Sales", "TicketNumber", "INTEGER NOT NULL DEFAULT 0"),
            ("Sales", "Customer", "TEXT NOT NULL DEFAULT 'Público en general'"),
            ("Sales", "ReceivedAmount", "TEXT NOT NULL DEFAULT 0"),
            ("Sales", "ChangeAmount", "TEXT NOT NULL DEFAULT 0"),
            ("Sales", "PersonalConsumption", "INTEGER NOT NULL DEFAULT 0"),
            ("Sales", "Cancelled", "INTEGER NOT NULL DEFAULT 0"),
            ("Sales", "CancelledAtUtc", "TEXT NULL"),
            ("Sales", "EditedAtUtc", "TEXT NULL"),
            ("Sales", "EditedByUserName", "TEXT NULL"),
            ("Sales", "EditCount", "INTEGER NOT NULL DEFAULT 0"),
            ("Sales", "PrintTicket", "INTEGER NOT NULL DEFAULT 1"),
            ("Sales", "CashSessionId", "INTEGER NULL"),
            ("Sales", "CashSessionAmount", "TEXT NOT NULL DEFAULT 0"),
            ("Products", "CentralProductId", "INTEGER NULL"),
            ("Products", "Cost", "TEXT NOT NULL DEFAULT 0"),
            ("Products", "WholesalePrice", "TEXT NOT NULL DEFAULT 0"),
            ("Products", "MinStock", "TEXT NOT NULL DEFAULT 0"),
            ("Products", "MaxStock", "TEXT NOT NULL DEFAULT 0"),
            ("Products", "SaleType", "TEXT NOT NULL DEFAULT 'Unidad'"),
            ("Products", "Department", "TEXT NOT NULL DEFAULT 'General'"),
            ("SaleLines", "Code", "TEXT NOT NULL DEFAULT ''"),
            ("SaleLines", "ListUnitPrice", "TEXT NOT NULL DEFAULT 0"),
            ("SaleLines", "DiscountPercentage", "TEXT NOT NULL DEFAULT 0"),
            ("Users", "MustChangePassword", "INTEGER NOT NULL DEFAULT 0")
        };

        foreach (var (table, name, definition) in columns)
        {
            var exists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM pragma_table_info('" + table + "') WHERE name = {0}", name)
                .SingleAsync(cancellationToken);
            if (exists == 0)
                await db.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE \"" + table + "\" ADD COLUMN \"" + name + "\" " + definition, cancellationToken);
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "CashSessions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CashSessions" PRIMARY KEY AUTOINCREMENT,
                "RegisterName" TEXT NOT NULL DEFAULT 'Caja 1',
                "UserName" TEXT NOT NULL DEFAULT '',
                "OpenedAtUtc" TEXT NOT NULL,
                "OpeningAmount" TEXT NOT NULL DEFAULT 0,
                "ClosedAtUtc" TEXT NULL,
                "ClosingAmount" TEXT NULL,
                "Difference" TEXT NOT NULL DEFAULT 0,
                "TotalSales" TEXT NOT NULL DEFAULT 0,
                "TotalEntries" TEXT NOT NULL DEFAULT 0,
                "TotalExits" TEXT NOT NULL DEFAULT 0,
                "Open" INTEGER NOT NULL DEFAULT 1
            );
            CREATE TABLE IF NOT EXISTS "CashMovements" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_CashMovements" PRIMARY KEY AUTOINCREMENT,
                "CashSessionId" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "Type" TEXT NOT NULL,
                "Amount" TEXT NOT NULL,
                "Description" TEXT NOT NULL DEFAULT '',
                CONSTRAINT "FK_CashMovements_CashSessions_CashSessionId"
                    FOREIGN KEY ("CashSessionId") REFERENCES "CashSessions" ("Id") ON DELETE CASCADE
            );
            """, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Settings" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_Settings" PRIMARY KEY,
                "Value" TEXT NOT NULL DEFAULT '',
                "UpdatedAtUtc" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "Users" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Users" PRIMARY KEY AUTOINCREMENT,
                "UserName" TEXT NOT NULL,
                "PasswordHash" TEXT NOT NULL,
                "Role" TEXT NOT NULL DEFAULT 'Cajero',
                "Permissions" TEXT NOT NULL DEFAULT '',
                "Active" INTEGER NOT NULL DEFAULT 1,
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_UserName" ON "Users" ("UserName");
            CREATE TABLE IF NOT EXISTS "InventoryMovements" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_InventoryMovements" PRIMARY KEY AUTOINCREMENT,
                "ProductId" INTEGER NOT NULL,
                "ProductCode" TEXT NOT NULL DEFAULT '',
                "ProductName" TEXT NOT NULL DEFAULT '',
                "Quantity" TEXT NOT NULL DEFAULT 0,
                "StockBefore" TEXT NOT NULL DEFAULT 0,
                "StockAfter" TEXT NOT NULL DEFAULT 0,
                "Type" TEXT NOT NULL DEFAULT '',
                "Reference" TEXT NOT NULL DEFAULT '',
                "UserName" TEXT NOT NULL DEFAULT '',
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "InvoiceEmissions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_InvoiceEmissions" PRIMARY KEY AUTOINCREMENT,
                "TicketNumber" INTEGER NOT NULL DEFAULT 0,
                "RequestId" TEXT NOT NULL,
                "DocumentType" TEXT NOT NULL DEFAULT 'boleta',
                "Status" TEXT NOT NULL DEFAULT '',
                "ProviderDocumentId" TEXT NOT NULL DEFAULT '',
                "ProviderFolio" TEXT NOT NULL DEFAULT '',
                "Message" TEXT NOT NULL DEFAULT '',
                "PayloadJson" TEXT NOT NULL DEFAULT '',
                "ResponseJson" TEXT NOT NULL DEFAULT '',
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_InvoiceEmissions_RequestId" ON "InvoiceEmissions" ("RequestId");
            CREATE INDEX IF NOT EXISTS "IX_InvoiceEmissions_TicketNumber" ON "InvoiceEmissions" ("TicketNumber");
            CREATE TABLE IF NOT EXISTS "SaleEdits" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SaleEdits" PRIMARY KEY AUTOINCREMENT,
                "SaleId" INTEGER NOT NULL,
                "EditedAtUtc" TEXT NOT NULL,
                "UserName" TEXT NOT NULL DEFAULT '',
                "Summary" TEXT NOT NULL DEFAULT '',
                CONSTRAINT "FK_SaleEdits_Sales_SaleId"
                    FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_SaleEdits_SaleId" ON "SaleEdits" ("SaleId");
            """, cancellationToken);

        if (!await db.Users.AnyAsync(cancellationToken))
        {
            if (WebAuthPolicy.AllowDemoCredentials(configuration))
            {
                db.Users.AddRange(
                    new LocalUser { UserName = "admin", PasswordHash = WebAuthPolicy.HashPassword("admin"), Role = "Administrador", Permissions = "all" },
                    new LocalUser { UserName = "cajero", PasswordHash = WebAuthPolicy.HashPassword("cajero"), Role = "Cajero", Permissions = "sales" });
            }
            else
            {
                var adminPassword = WebAuthPolicy.TryReadInitialPassword() ?? WebAuthPolicy.DefaultInitialPassword;
                db.Users.Add(new LocalUser
                {
                    UserName = WebAuthPolicy.DefaultInitialUserName,
                    PasswordHash = WebAuthPolicy.HashPassword(adminPassword),
                    Role = "Administrador",
                    Permissions = "all",
                    MustChangePassword = true
                });
                WebAuthPolicy.WriteInitialCredentials(WebAuthPolicy.DefaultInitialUserName, adminPassword);
                logger.LogWarning(
                    "Usuario admin inicial creado. Credenciales en {Path}",
                    WebAuthPolicy.SharedCredentialsPath);
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        else if (!WebAuthPolicy.AllowDemoCredentials(configuration))
        {
            await EnforceProductionPasswordPolicyAsync(db, cancellationToken);
        }

        var zeroTickets = await db.Sales.Where(x => x.TicketNumber == 0).OrderBy(x => x.Id).ToListAsync(cancellationToken);
        if (zeroTickets.Count > 0)
        {
            var next = await db.Sales.Select(x => (long?)x.TicketNumber).MaxAsync(cancellationToken) ?? 0;
            foreach (var sale in zeroTickets)
                sale.TicketNumber = ++next;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<IReadOnlyList<PosProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Products.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name)
            .Select(x => new PosProduct(x.Id, x.Code, x.Name, x.Category, x.Price, x.Stock, x.Unit, x.Accent, x.CentralProductId,
                x.Cost, x.WholesalePrice, x.MinStock, x.MaxStock, x.SaleType, x.Department))
            .ToListAsync(cancellationToken);
    }

    public async Task<ProductWriteResult> CreateProductAsync(
        string code, string name, string category, decimal cost, decimal price,
        decimal wholesalePrice, decimal stock, decimal minStock, decimal maxStock,
        string unit, string saleType, string department, string userName,
        CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return ProductWriteResult.Failed("El código y la descripción son obligatorios.");
        if (price < 0 || cost < 0 || stock < 0 || wholesalePrice < 0 || minStock < 0 || maxStock < 0)
            return ProductWriteResult.Failed("Los valores del producto no pueden ser negativos.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Products.AnyAsync(x => x.Code.ToLower() == code.ToLower(), cancellationToken))
            return ProductWriteResult.Failed($"Ya existe un producto con el código {code}.");

        var product = new LocalProduct
        {
            Code = code,
            Name = name,
            Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim(),
            Cost = cost,
            Price = price,
            WholesalePrice = wholesalePrice,
            Stock = stock,
            MinStock = minStock,
            MaxStock = maxStock,
            Unit = string.IsNullOrWhiteSpace(unit) ? "un." : unit.Trim(),
            SaleType = string.IsNullOrWhiteSpace(saleType) ? "Unidad" : saleType.Trim(),
            Department = string.IsNullOrWhiteSpace(department) ? "General" : department.Trim(),
            Active = true
        };
        db.Products.Add(product);
        if (stock != 0)
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ProductCode = code,
                ProductName = name,
                Quantity = stock,
                StockBefore = 0,
                StockAfter = stock,
                Type = "ALTA_PRODUCTO",
                Reference = "Producto nuevo",
                UserName = userName
            });
        await db.SaveChangesAsync(cancellationToken);
        return ProductWriteResult.Successful(product.Id, "Producto guardado correctamente.");
    }

    public async Task<ProductWriteResult> UpdateProductAsync(
        int productId, string code, string name, string category, decimal cost, decimal price,
        decimal wholesalePrice, decimal stock, decimal minStock, decimal maxStock,
        string unit, string saleType, string department, string userName,
        CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        name = name.Trim();
        if (productId <= 0 || string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return ProductWriteResult.Failed("Selecciona un producto y completa código y descripción.");
        if (price < 0 || cost < 0 || stock < 0 || wholesalePrice < 0 || minStock < 0 || maxStock < 0)
            return ProductWriteResult.Failed("Los valores del producto no pueden ser negativos.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null || !product.Active)
            return ProductWriteResult.Failed("El producto ya no está disponible.");
        if (await db.Products.AnyAsync(x => x.Id != productId && x.Code.ToLower() == code.ToLower(), cancellationToken))
            return ProductWriteResult.Failed($"Ya existe otro producto con el código {code}.");

        var stockBefore = product.Stock;
        product.Code = code;
        product.Name = name;
        product.Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
        product.Cost = cost;
        product.Price = price;
        product.WholesalePrice = wholesalePrice;
        product.Stock = stock;
        product.MinStock = minStock;
        product.MaxStock = maxStock;
        product.Unit = string.IsNullOrWhiteSpace(unit) ? "un." : unit.Trim();
        product.SaleType = string.IsNullOrWhiteSpace(saleType) ? "Unidad" : saleType.Trim();
        product.Department = string.IsNullOrWhiteSpace(department) ? "General" : department.Trim();
        if (stockBefore != stock)
            db.InventoryMovements.Add(new LocalInventoryMovement
            {
                ProductId = product.Id,
                ProductCode = code,
                ProductName = name,
                Quantity = stock - stockBefore,
                StockBefore = stockBefore,
                StockAfter = stock,
                Type = "AJUSTE_PRODUCTO",
                Reference = "Edición de producto",
                UserName = userName
            });
        await db.SaveChangesAsync(cancellationToken);
        return ProductWriteResult.Successful(product.Id, "Producto actualizado correctamente.");
    }

    public async Task<ProductWriteResult> DeactivateProductAsync(
        int productId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null || !product.Active)
            return ProductWriteResult.Failed("Selecciona un producto activo.");
        product.Active = false;
        await db.SaveChangesAsync(cancellationToken);
        return ProductWriteResult.Successful(product.Id, "Producto eliminado del catálogo.");
    }

    public async Task SyncCentralProductsAsync(
        IReadOnlyCollection<MulticajaProductDto> centralProducts,
        CancellationToken cancellationToken = default)
    {
        if (centralProducts.Count == 0)
            return;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allProducts = await db.Products.ToListAsync(cancellationToken);
        var byCode = new Dictionary<string, LocalProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var existing in allProducts)
        {
            if (string.IsNullOrWhiteSpace(existing.Code))
                continue;
            byCode[existing.Code] = existing;
        }

        foreach (var central in centralProducts)
        {
            if (string.IsNullOrWhiteSpace(central.CodigoBarras))
                continue;
            if (!byCode.TryGetValue(central.CodigoBarras, out var product))
            {
                product = new LocalProduct { Code = central.CodigoBarras };
                db.Products.Add(product);
            }
            product.CentralProductId = central.Id;
            product.Name = central.Nombre;
            product.Cost = central.Costo;
            product.Price = central.Precio;
            product.WholesalePrice = central.PrecioMayoreo;
            product.Stock = central.Stock;
            product.MinStock = central.InvMinimo;
            product.MaxStock = central.InvMaximo;
            product.Unit = string.IsNullOrWhiteSpace(central.TipoVenta) ? "un." : central.TipoVenta;
            product.Category = string.IsNullOrWhiteSpace(central.Departamento) ? "General" : central.Departamento;
            product.Department = product.Category;
            product.Active = true;
        }
        await SaveChangesWithRetryAsync(db, cancellationToken);
    }

    private static async Task SaveChangesWithRetryAsync(
        LocalPosDbContext db,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return;
            }
            catch (DbUpdateException ex) when (IsSqliteBusy(ex) && attempt < 3)
            {
                await Task.Delay(40 * (attempt + 1), cancellationToken);
            }
        }
    }

    private static bool IsSqliteBusy(Exception ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite &&
        sqlite.SqliteErrorCode == 5;

    public async Task<SaleResult> RecordSaleAsync(
        IReadOnlyCollection<CartItem> cart, string userName, string paymentMethod,
        decimal receivedAmount = 0, bool personalConsumption = false, bool printTicket = true,
        long? cashSessionId = null, string customer = "Público en general",
        long? ticketNumberOverride = null, decimal cashSessionAmount = -1, bool enforceInventory = true,
        CancellationToken cancellationToken = default)
    {
        if (cart.Count == 0)
            return SaleResult.Failed("Agrega al menos un producto para cobrar.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(paymentMethod))
            paymentMethod = "Efectivo";

        var productIds = cart.Select(x => x.Product.Id).Where(id => id > 0).Distinct().ToArray();
        var products = await db.Products.Where(x => productIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var lines = new List<LocalSaleLine>();

        foreach (var item in cart.GroupBy(x => $"{x.Product.Id}:{x.UnitPrice}:{x.DiscountPercentage}").Select(group =>
                     new CartItem(group.First().Product, group.Sum(x => x.Quantity),
                         group.First().DiscountPercentage, group.First().UnitPrice)))
        {
            var isCommon = item.Product.Id <= 0;
            LocalProduct? product = null;
            if (!isCommon)
            {
                if (!products.TryGetValue(item.Product.Id, out product) || !product.Active)
                    return SaleResult.Failed($"El producto '{item.Product.Name}' ya no está disponible.");
                if (enforceInventory && (item.Quantity <= 0 || item.Quantity > product.Stock))
                    return SaleResult.Failed($"Stock insuficiente para '{product.Name}'.");
                product.Stock -= item.Quantity;
                db.InventoryMovements.Add(new LocalInventoryMovement
                {
                    ProductId = product.Id,
                    ProductCode = product.Code,
                    ProductName = product.Name,
                    Quantity = -item.Quantity,
                    StockBefore = product.Stock + item.Quantity,
                    StockAfter = product.Stock,
                    Type = personalConsumption ? "CONSUMO_PERSONAL" : "VENTA",
                    Reference = "Venta en curso",
                    UserName = userName
                });
            }
            else if (item.Quantity <= 0)
            {
                return SaleResult.Failed("La cantidad del producto común debe ser mayor a cero.");
            }

            var unitPrice = item.UnitPrice > 0 ? item.UnitPrice : item.Product.Price;
            var listTotal = Math.Round(unitPrice * item.Quantity, 2, MidpointRounding.AwayFromZero);
            var lineTotal = Math.Round(listTotal * (1m - item.DiscountPercentage / 100m), 2, MidpointRounding.AwayFromZero);
            var netUnitPrice = item.Quantity == 0 ? 0 : lineTotal / item.Quantity;
            lines.Add(new LocalSaleLine
            {
                ProductId = isCommon ? 0 : product!.Id,
                ProductName = item.Product.Name,
                Code = item.Product.Code,
                ListUnitPrice = unitPrice,
                UnitPrice = netUnitPrice,
                DiscountPercentage = item.DiscountPercentage,
                Quantity = item.Quantity,
                Total = lineTotal
            });
        }

        var subtotal = lines.Sum(x => Math.Round(x.ListUnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero));
        var discount = Math.Max(0m, subtotal - lines.Sum(x => x.Total));
        var total = personalConsumption ? 0m : lines.Sum(x => x.Total);
        if (!personalConsumption && paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
            receivedAmount = 0;
        else if (!personalConsumption && receivedAmount <= 0)
            receivedAmount = total;
        if (!personalConsumption && !paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase) &&
            receivedAmount < total)
            return SaleResult.Failed("El monto recibido es insuficiente.");

        var session = personalConsumption
            ? null
            : await db.CashSessions.SingleOrDefaultAsync(x => x.Id == cashSessionId && x.Open, cancellationToken)
              ?? await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (!personalConsumption && session is null)
        {
            session = new LocalCashSession { UserName = userName };
            db.CashSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
        }

        var nextTicket = (await db.Sales.Select(x => (long?)x.TicketNumber).MaxAsync(cancellationToken) ?? 0) + 1;
        var configuredFolio = await db.Settings.AsNoTracking()
            .Where(x => x.Key == "folio_actual")
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (long.TryParse(configuredFolio, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedFolio))
            nextTicket = Math.Max(nextTicket, requestedFolio);
        var ticketNumber = nextTicket;
        if (ticketNumberOverride is > 0)
            ticketNumber = ticketNumberOverride.Value;
        var cashToRegister = cashSessionAmount >= 0
            ? cashSessionAmount
            : paymentMethod == "Efectivo" ? total : 0m;
        var sale = new LocalSale
        {
            TicketNumber = ticketNumber, UserName = userName, PaymentMethod = paymentMethod,
            Customer = string.IsNullOrWhiteSpace(customer) ? "Público en general" : customer,
            Subtotal = subtotal, Discount = discount, Total = total,
            ReceivedAmount = personalConsumption ? 0 : receivedAmount,
            ChangeAmount = personalConsumption ? 0 : Math.Max(0, receivedAmount - total),
            PersonalConsumption = personalConsumption, PrintTicket = printTicket,
            CashSessionId = session?.Id, CashSessionAmount = cashToRegister, Lines = lines
        };
        db.Sales.Add(sale);
        if (session is not null && cashToRegister > 0)
        {
            session.TotalSales += cashToRegister;
            db.CashMovements.Add(new LocalCashMovement
            {
                CashSessionId = session.Id, Type = "VENTA", Amount = cashToRegister,
                Description = $"Venta Ticket #{ticketNumber}"
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        var folioSetting = await db.Settings.SingleOrDefaultAsync(x => x.Key == "folio_actual", cancellationToken);
        if (folioSetting is null)
            db.Settings.Add(new LocalSetting { Key = "folio_actual", Value = (ticketNumber + 1).ToString(CultureInfo.InvariantCulture) });
        else
            folioSetting.Value = (ticketNumber + 1).ToString(CultureInfo.InvariantCulture);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation("Venta local ticket {TicketNumber} registrada por {UserName}", ticketNumber, userName);
        return SaleResult.Succeeded(sale.Id, ticketNumber, sale.Total, sale.ChangeAmount);
    }

    public async Task<PosDashboard> GetDashboardAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var today = DateTime.UtcNow.Date;
        var sales = await db.Sales.Where(x => x.CreatedAtUtc >= today && !x.Cancelled && !x.PersonalConsumption)
            .AsNoTracking().ToListAsync(cancellationToken);
        var unitsInStock = await db.Products.AsNoTracking().Where(x => x.Active)
            .Select(x => x.Stock).ToListAsync(cancellationToken);
        return new PosDashboard(
            sales.Count,
            sales.Sum(x => x.Total),
            await db.Products.CountAsync(x => x.Active, cancellationToken),
            unitsInStock.Sum());
    }

    public async Task<bool> AdjustStockAsync(int productId, decimal delta, string userName = "",
        string reason = "Ajuste manual", CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null || product.Stock + delta < 0)
            return false;

        var before = product.Stock;
        product.Stock += delta;
        db.InventoryMovements.Add(new LocalInventoryMovement
        {
            ProductId = product.Id,
            ProductCode = product.Code,
            ProductName = product.Name,
            Quantity = delta,
            StockBefore = before,
            StockAfter = product.Stock,
            Type = delta > 0 ? "INGRESO" : "AJUSTE",
            Reference = string.IsNullOrWhiteSpace(reason) ? "Ajuste manual" : reason,
            UserName = userName
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<InventoryMovementInfo>> GetInventoryMovementsAsync(
        string? productFilter = null, int take = 300, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.InventoryMovements.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(productFilter))
            query = query.Where(x => x.ProductName.Contains(productFilter) || x.ProductCode.Contains(productFilter));
        return await query.OrderByDescending(x => x.CreatedAtUtc).Take(take)
            .Select(x => new InventoryMovementInfo(x.CreatedAtUtc, x.ProductCode, x.ProductName,
                x.Type, x.Quantity, x.StockBefore, x.StockAfter, x.UserName, x.Reference))
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> UpdateInventoryMetadataAsync(int productId, string? name, decimal? cost,
        decimal? price, decimal? wholesalePrice, decimal? minStock, decimal? maxStock,
        string? department, string? saleType, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.FirstOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null)
            return false;
        if (!string.IsNullOrWhiteSpace(name))
            product.Name = name.Trim();
        if (cost.HasValue)
            product.Cost = cost.Value;
        if (price.HasValue)
            product.Price = price.Value;
        if (wholesalePrice.HasValue)
            product.WholesalePrice = wholesalePrice.Value;
        if (minStock.HasValue)
            product.MinStock = minStock.Value;
        if (maxStock.HasValue)
            product.MaxStock = maxStock.Value;
        if (!string.IsNullOrWhiteSpace(department))
        {
            product.Department = department.Trim();
            product.Category = product.Department;
        }
        if (!string.IsNullOrWhiteSpace(saleType))
        {
            product.SaleType = saleType.Trim();
            product.Unit = product.SaleType;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CreateImportedInventoryProductAsync(string code, decimal stock, string? name,
        decimal? cost, decimal? price, decimal? wholesalePrice, decimal? minStock, decimal? maxStock,
        string? department, string? saleType, string userName = "Importación",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Products.AnyAsync(x => x.Code == code.Trim(), cancellationToken))
            return false;
        var normalizedDepartment = string.IsNullOrWhiteSpace(department) ? "General" : department.Trim();
        var normalizedSaleType = string.IsNullOrWhiteSpace(saleType) ? "Unidad" : saleType.Trim();
        var product = new LocalProduct
        {
            Code = code.Trim(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Producto {code.Trim()}" : name.Trim(),
            Category = normalizedDepartment,
            Department = normalizedDepartment,
            Cost = cost ?? 0,
            Price = price ?? 0,
            WholesalePrice = wholesalePrice ?? 0,
            Stock = stock,
            MinStock = minStock ?? 0,
            MaxStock = maxStock ?? 0,
            SaleType = normalizedSaleType,
            Unit = normalizedSaleType,
            Active = true
        };
        db.Products.Add(product);
        db.InventoryMovements.Add(new LocalInventoryMovement
        {
            ProductCode = product.Code,
            ProductName = product.Name,
            Quantity = stock,
            StockBefore = 0,
            StockAfter = stock,
            Type = "IMPORTACION",
            Reference = "Importación de inventario",
            UserName = userName
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<RecentSale>> GetRecentSalesAsync(int take = 12, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Sales.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(take)
            .Select(x => new RecentSale(x.Id, x.TicketNumber, x.CreatedAtUtc, x.UserName, x.PaymentMethod,
                x.Total, x.Discount, x.PersonalConsumption, x.Cancelled))
            .ToListAsync(cancellationToken);
    }

    public async Task<long> GetNextTicketNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await db.Sales.Select(x => (long?)x.TicketNumber).MaxAsync(cancellationToken) ?? 0) + 1;
    }

    public async Task<long> OpenCashSessionAsync(string userName, decimal openingAmount = 0,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (existing is not null)
            return existing.Id;
        var session = new LocalCashSession { UserName = userName, OpeningAmount = openingAmount };
        db.CashSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<LocalCashSession?> GetOpenCashSessionAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.CashSessions.AsNoTracking().FirstOrDefaultAsync(x => x.Open, cancellationToken);
    }

    public async Task<bool> RegisterCashMovementAsync(string userName, string type, decimal amount,
        string description, CancellationToken cancellationToken = default)
    {
        if (amount <= 0 || type is not ("INGRESO" or "RETIRO"))
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (session is null)
            return false;
        if (type == "INGRESO") session.TotalEntries += amount;
        if (type == "RETIRO") session.TotalExits += amount;
        db.CashMovements.Add(new LocalCashMovement
        {
            CashSessionId = session.Id, Type = type, Amount = amount,
            Description = string.IsNullOrWhiteSpace(description) ? userName : description
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> CloseCashSessionAsync(decimal closingAmount, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (session is null)
            return false;
        var expected = session.OpeningAmount + session.TotalSales + session.TotalEntries - session.TotalExits;
        session.ClosingAmount = closingAmount;
        session.Difference = closingAmount - expected;
        session.ClosedAtUtc = DateTime.UtcNow;
        session.Open = false;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<string> GetSettingAsync(string key, string defaultValue = "",
        CancellationToken cancellationToken = default)
    {
        if (LocalLicenseConfigStore.IsLicenseKey(key))
        {
            var local = LocalLicenseConfigStore.TryGet(key);
            if (!string.IsNullOrEmpty(local))
                return local;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        return setting?.Value ?? defaultValue;
    }

    public async Task SetSettingAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null)
            db.Settings.Add(new LocalSetting { Key = key, Value = value ?? string.Empty });
        else
        {
            setting.Value = value ?? string.Empty;
            setting.UpdatedAtUtc = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken);

        if (LocalLicenseConfigStore.IsLicenseKey(key))
            LocalLicenseConfigStore.Set(key, value ?? string.Empty);
    }

    public async Task SetSettingsAsync(IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken = default)
    {
        foreach (var pair in values)
            await SetSettingAsync(pair.Key, pair.Value, cancellationToken);
    }

    public async Task LinkDrawerToPrinterAsync(string printerName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(printerName))
            return;

        await SetSettingsAsync(new Dictionary<string, string>
        {
            ["cajon_habilitado"] = "true",
            ["cajon_modo"] = "printer",
            ["cajon_dispositivo"] = printerName.Trim()
        }, cancellationToken);
    }

    public async Task EnsureDrawerLinkedToPrinterAsync(CancellationToken cancellationToken = default)
    {
        var printer = (await GetSettingAsync("impresora_nombre", string.Empty, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(printer))
            return;

        var drawerDevice = (await GetSettingAsync("cajon_dispositivo", string.Empty, cancellationToken)).Trim();
        var drawerMode = (await GetSettingAsync("cajon_modo", "printer", cancellationToken)).Trim();
        if (!string.IsNullOrWhiteSpace(drawerDevice)
            && drawerMode.Equals("printer", StringComparison.OrdinalIgnoreCase)
            && drawerDevice.Equals(printer, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(drawerDevice)
            && !drawerMode.Equals("printer", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await LinkDrawerToPrinterAsync(printer, cancellationToken);
    }

    public async Task SaveInvoiceEmissionAsync(LocalInvoiceEmission emission, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.InvoiceEmissions.Add(emission);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InvoiceEmissionInfo>> GetRecentInvoiceEmissionsAsync(
        int take = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InvoiceEmissions.AsNoTracking()
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(take)
            .Select(x => new InvoiceEmissionInfo(
                x.Id, x.TicketNumber, x.DocumentType, x.Status, x.ProviderDocumentId,
                x.ProviderFolio, x.Message, x.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }

    public async Task<InvoiceEmissionInfo?> GetLatestInvoiceEmissionForTicketAsync(
        long ticketNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.InvoiceEmissions.AsNoTracking()
            .Where(x => x.TicketNumber == ticketNumber)
            .OrderByDescending(x => x.Id)
            .Select(x => new InvoiceEmissionInfo(
                x.Id, x.TicketNumber, x.DocumentType, x.Status, x.ProviderDocumentId,
                x.ProviderFolio, x.Message, x.CreatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<LocalUserInfo>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking().OrderBy(x => x.UserName)
            .Select(x => new LocalUserInfo(x.Id, x.UserName, x.Role, x.Permissions, x.Active, x.MustChangePassword))
            .ToListAsync(cancellationToken);
    }

    public async Task<LocalUserInfo?> AuthenticateUserAsync(string userName, string password,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var normalized = userName.Trim().ToLower();
        var user = await db.Users.SingleOrDefaultAsync(x => x.UserName.ToLower() == normalized && x.Active, cancellationToken);
        return user is not null && WebAuthPolicy.VerifyPassword(password, user.PasswordHash)
            ? new LocalUserInfo(user.Id, user.UserName, user.Role, user.Permissions, user.Active, user.MustChangePassword)
            : null;
    }

    public async Task<LocalUserInfo?> TryAuthenticateViaInitialCredentialsAsync(
        string userName, string password, CancellationToken cancellationToken = default)
    {
        if (!userName.Trim().Equals("admin", StringComparison.OrdinalIgnoreCase))
            return null;
        var expected = WebAuthPolicy.TryReadInitialPassword();
        if (string.IsNullOrWhiteSpace(expected) || !string.Equals(password, expected, StringComparison.Ordinal))
            return null;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(
            x => x.UserName.ToLower() == "admin", cancellationToken);
        if (user is null)
        {
            user = new LocalUser
            {
                UserName = "admin",
                Role = "Administrador",
                Permissions = "all",
                Active = true,
                MustChangePassword = true
            };
            db.Users.Add(user);
        }

        user.PasswordHash = WebAuthPolicy.HashPassword(password);
        user.MustChangePassword = true;
        user.Active = true;
        await db.SaveChangesAsync(cancellationToken);
        return new LocalUserInfo(user.Id, user.UserName, user.Role, user.Permissions, user.Active, user.MustChangePassword);
    }

    public async Task<bool> CreateUserAsync(string userName, string password, string role,
        CancellationToken cancellationToken = default)
    {
        userName = userName.Trim();
        if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrWhiteSpace(password))
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Users.AnyAsync(x => x.UserName.ToLower() == userName.ToLower(), cancellationToken))
            return false;
        db.Users.Add(new LocalUser
        {
            UserName = userName,
            PasswordHash = WebAuthPolicy.HashPassword(password),
            Role = string.IsNullOrWhiteSpace(role) ? "Cajero" : role.Trim(),
            Permissions = role.Equals("Administrador", StringComparison.OrdinalIgnoreCase) ? "all" : "sales"
        });
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateUserPasswordAsync(int id, string password,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(password))
            return false;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return false;
        user.PasswordHash = WebAuthPolicy.HashPassword(password);
        user.MustChangePassword = false;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteUserAsync(int id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null || user.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase))
            return false;
        db.Users.Remove(user);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<string?> BackupDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var source = await GetDatabasePathAsync(cancellationToken);
        if (!File.Exists(source))
            return null;
        var backupDir = Path.Combine(Path.GetDirectoryName(source)!, "backups");
        Directory.CreateDirectory(backupDir);
        var destination = Path.Combine(backupDir, $"grunflex-pos-{DateTime.Now:yyyyMMdd-HHmmss}.db");
        File.Copy(source, destination, overwrite: true);
        return destination;
    }

    public async Task<string> GetDatabasePathAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return db.Database.GetDbConnection().DataSource;
    }

    public async Task<string> ExportConfigurationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await db.Settings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);
        var users = await db.Users.AsNoTracking().Select(x => new { x.UserName, x.Role, x.Permissions, x.Active }).ToListAsync(cancellationToken);
        return JsonSerializer.Serialize(new { exportedAtUtc = DateTime.UtcNow, settings, users },
            new JsonSerializerOptions { WriteIndented = true });
    }

    private static async Task EnforceProductionPasswordPolicyAsync(
        LocalPosDbContext db, CancellationToken cancellationToken)
    {
        var users = await db.Users.ToListAsync(cancellationToken);
        var changed = false;
        foreach (var user in users.Where(u =>
                     u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase) &&
                     WebAuthPolicy.IsDemoPasswordHash(u.PasswordHash)))
        {
            user.MustChangePassword = true;
            changed = true;
        }

        if (changed)
            await db.SaveChangesAsync(cancellationToken);
    }

    public static string InitialCredentialsFilePath => WebAuthPolicy.InitialCredentialsPath;

    private static string HashPassword(string password) => WebAuthPolicy.HashPassword(password);

    private static bool VerifyPassword(string password, string hash) =>
        WebAuthPolicy.VerifyPassword(password, hash);
}

public sealed record PosProduct(int Id, string Code, string Name, string Category, decimal Price, decimal Stock, string Unit, string Accent, int? CentralProductId = null,
    decimal Cost = 0, decimal WholesalePrice = 0, decimal MinStock = 0, decimal MaxStock = 0,
    string SaleType = "Unidad", string Department = "General");
public sealed record ProductWriteResult(bool Success, string Message, int ProductId = 0)
{
    public static ProductWriteResult Successful(int productId, string message) => new(true, message, productId);
    public static ProductWriteResult Failed(string message) => new(false, message);
}
public sealed record CartItem(PosProduct Product, decimal Quantity, decimal DiscountPercentage = 0, decimal UnitPrice = 0)
{
    public decimal EffectiveUnitPrice => UnitPrice > 0 ? UnitPrice : Product.Price;
}
public sealed record SaleResult(bool Success, string Message, long? SaleId = null, long TicketNumber = 0,
    decimal Total = 0, decimal Change = 0)
{
    public static SaleResult Failed(string message) => new(false, message);
    public static SaleResult Succeeded(long id, long ticketNumber, decimal total, decimal change) =>
        new(true, "Venta registrada correctamente.", id, ticketNumber, total, change);
}
public sealed record PosDashboard(int SalesToday, decimal RevenueToday, int ActiveProducts, decimal UnitsInStock);
public sealed record RecentSale(long Id, long TicketNumber, DateTime CreatedAtUtc, string UserName,
    string PaymentMethod, decimal Total, decimal Discount, bool PersonalConsumption, bool Cancelled);
public sealed record LocalUserInfo(int Id, string UserName, string Role, string Permissions, bool Active, bool MustChangePassword = false);
public sealed record InventoryMovementInfo(DateTime CreatedAtUtc, string ProductCode, string ProductName,
    string Type, decimal Quantity, decimal StockBefore, decimal StockAfter, string UserName, string Reference);

public sealed record InvoiceEmissionInfo(
    long Id, long TicketNumber, string DocumentType, string Status, string ProviderDocumentId,
    string ProviderFolio, string Message, DateTime CreatedAtUtc);
