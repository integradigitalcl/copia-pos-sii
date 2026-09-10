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
    public async Task EnsureCreatedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await EnsureSalesColumnsAsync(db, cancellationToken);
        // No se siembran productos ni stock. PC nueva → catálogo vacío (inventario 0).
        // Instalación encima de una versión previa → se conserva grunflex-pos.db existente.
    }

    private async Task EnsureSalesColumnsAsync(LocalPosDbContext db, CancellationToken cancellationToken)
    {
        // EnsureCreated no crea tablas si el archivo ya existía sin esquema (instalación interrumpida).
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "Products" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY AUTOINCREMENT,
                "Code" TEXT NOT NULL DEFAULT '',
                "Name" TEXT NOT NULL DEFAULT '',
                "Category" TEXT NOT NULL DEFAULT '',
                "Price" TEXT NOT NULL DEFAULT 0,
                "Cost" TEXT NOT NULL DEFAULT 0,
                "WholesalePrice" TEXT NOT NULL DEFAULT 0,
                "Stock" TEXT NOT NULL DEFAULT 0,
                "MinStock" TEXT NOT NULL DEFAULT 0,
                "MaxStock" TEXT NOT NULL DEFAULT 0,
                "Unit" TEXT NOT NULL DEFAULT 'un.',
                "SaleType" TEXT NOT NULL DEFAULT 'Unidad',
                "Department" TEXT NOT NULL DEFAULT 'General',
                "Accent" TEXT NOT NULL DEFAULT '#2563EB',
                "CentralProductId" INTEGER NULL,
                "Active" INTEGER NOT NULL DEFAULT 1
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Products_Code" ON "Products" ("Code");
            CREATE TABLE IF NOT EXISTS "Sales" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_Sales" PRIMARY KEY AUTOINCREMENT,
                "TicketNumber" INTEGER NOT NULL DEFAULT 0,
                "CreatedAtUtc" TEXT NOT NULL,
                "UserName" TEXT NOT NULL DEFAULT '',
                "PaymentMethod" TEXT NOT NULL DEFAULT '',
                "Customer" TEXT NOT NULL DEFAULT 'Público en general',
                "Subtotal" TEXT NOT NULL DEFAULT 0,
                "Discount" TEXT NOT NULL DEFAULT 0,
                "Total" TEXT NOT NULL DEFAULT 0,
                "ReceivedAmount" TEXT NOT NULL DEFAULT 0,
                "ChangeAmount" TEXT NOT NULL DEFAULT 0,
                "PersonalConsumption" INTEGER NOT NULL DEFAULT 0,
                "Cancelled" INTEGER NOT NULL DEFAULT 0,
                "CancelledAtUtc" TEXT NULL,
                "EditedAtUtc" TEXT NULL,
                "EditedByUserName" TEXT NULL,
                "EditCount" INTEGER NOT NULL DEFAULT 0,
                "PrintTicket" INTEGER NOT NULL DEFAULT 1,
                "CashSessionId" INTEGER NULL,
                "CashSessionAmount" TEXT NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS "SaleLines" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_SaleLines" PRIMARY KEY AUTOINCREMENT,
                "SaleId" INTEGER NOT NULL,
                "ProductId" INTEGER NOT NULL,
                "ProductName" TEXT NOT NULL DEFAULT '',
                "Code" TEXT NOT NULL DEFAULT '',
                "ListUnitPrice" TEXT NOT NULL DEFAULT 0,
                "UnitPrice" TEXT NOT NULL DEFAULT 0,
                "DiscountPercentage" TEXT NOT NULL DEFAULT 0,
                "Quantity" TEXT NOT NULL DEFAULT 0,
                "Total" TEXT NOT NULL DEFAULT 0,
                "UnitCost" TEXT NOT NULL DEFAULT 0,
                "Department" TEXT NOT NULL DEFAULT '',
                CONSTRAINT "FK_SaleLines_Sales_SaleId"
                    FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_SaleLines_SaleId" ON "SaleLines" ("SaleId");
            """, cancellationToken);

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
            ("SaleLines", "UnitCost", "TEXT NOT NULL DEFAULT 0"),
            ("SaleLines", "Department", "TEXT NOT NULL DEFAULT ''"),
            ("Users", "MustChangePassword", "INTEGER NOT NULL DEFAULT 0"),
            ("Products", "IsFavorite", "INTEGER NOT NULL DEFAULT 0"),
            ("Products", "PromotionComponentsJson", "TEXT NOT NULL DEFAULT ''")
        };

        foreach (var (table, name, definition) in columns)
        {
            // pragma_table_info devuelve 0 filas si la tabla no existe; sin esta guarda el ALTER fallaba.
            var tableExists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", table)
                .SingleAsync(cancellationToken);
            if (tableExists == 0)
                continue;

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
                "MustChangePassword" INTEGER NOT NULL DEFAULT 0,
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

        await RepairSaleTicketNumbersAsync(db, cancellationToken);
        await SyncFolioSettingAsync(db, cancellationToken);
    }

    /// <summary>
    /// Reasigna folios en 0 o duplicados antes de crear el índice único.
    /// Una reinstalación sobre grunflex-pos.db viejo deja varias ventas con TicketNumber=0
    /// (columna agregada con DEFAULT 0) y CREATE UNIQUE INDEX abortaba el arranque.
    /// </summary>
    private static async Task RepairSaleTicketNumbersAsync(LocalPosDbContext db, CancellationToken cancellationToken)
    {
        var tableExists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", "Sales")
            .SingleAsync(cancellationToken);
        if (tableExists == 0)
            return;

        var columnExists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM pragma_table_info('Sales') WHERE name = {0}", "TicketNumber")
            .SingleAsync(cancellationToken);
        if (columnExists == 0)
            return;

        await db.Database.ExecuteSqlRawAsync("DROP INDEX IF EXISTS \"IX_Sales_TicketNumber\"", cancellationToken);

        var rows = await db.Sales.AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.TicketNumber })
            .ToListAsync(cancellationToken);
        var used = new HashSet<long>();
        var next = rows.Where(x => x.TicketNumber > 0).Select(x => x.TicketNumber).DefaultIfEmpty(0).Max();

        foreach (var row in rows)
        {
            var ticket = row.TicketNumber;
            if (ticket <= 0 || !used.Add(ticket))
            {
                do { next++; } while (!used.Add(next));
                ticket = next;
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Sales\" SET \"TicketNumber\" = {ticket} WHERE \"Id\" = {row.Id}",
                    cancellationToken);
            }
        }

        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Sales_TicketNumber" ON "Sales" ("TicketNumber");
            """, cancellationToken);
    }

    private static async Task SyncFolioSettingAsync(LocalPosDbContext db, CancellationToken cancellationToken)
    {
        var tableExists = await db.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*) AS Value FROM sqlite_master WHERE type = 'table' AND name = {0}", "Sales")
            .SingleAsync(cancellationToken);
        if (tableExists == 0)
            return;

        var maxTicket = await db.Sales.Select(x => (long?)x.TicketNumber).MaxAsync(cancellationToken) ?? 0;
        var nextFolio = (maxTicket + 1).ToString(CultureInfo.InvariantCulture);
        var setting = await db.Settings.SingleOrDefaultAsync(x => x.Key == "folio_actual", cancellationToken);
        if (setting is null)
        {
            db.Settings.Add(new LocalSetting { Key = "folio_actual", Value = nextFolio });
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!long.TryParse(setting.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var configured)
            || configured <= maxTicket)
        {
            setting.Value = nextFolio;
            setting.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<long> AllocateTicketNumberAsync(
        LocalPosDbContext db, long? preferredTicket, CancellationToken cancellationToken)
    {
        var maxTicket = await db.Sales.Select(x => (long?)x.TicketNumber).MaxAsync(cancellationToken) ?? 0;
        var nextTicket = maxTicket + 1;
        var configuredFolio = await db.Settings.AsNoTracking()
            .Where(x => x.Key == "folio_actual")
            .Select(x => x.Value)
            .SingleOrDefaultAsync(cancellationToken);
        if (long.TryParse(configuredFolio, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedFolio)
            && requestedFolio > nextTicket)
            nextTicket = requestedFolio;

        if (preferredTicket is > 0)
        {
            var preferred = preferredTicket.Value;
            var exists = await db.Sales.AsNoTracking()
                .AnyAsync(x => x.TicketNumber == preferred, cancellationToken);
            if (!exists)
                return preferred;
        }

        return nextTicket;
    }

    private static bool IsSqliteUniqueConstraint(Exception ex) =>
        ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqlite &&
        sqlite.SqliteExtendedErrorCode == 2067 /* SQLITE_CONSTRAINT_UNIQUE */;

    public async Task<IReadOnlyList<PosProduct>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Products.AsNoTracking().Where(x => x.Active).OrderBy(x => x.Name).ToListAsync(cancellationToken);
        var byId = rows.ToDictionary(x => x.Id);
        return rows.Select(x =>
        {
            var components = PromotionCatalog.Parse(x.PromotionComponentsJson);
            var stock = components.Count == 0
                ? x.Stock
                : PromotionCatalog.AvailableKits(components, id => byId.TryGetValue(id, out var p) ? p.Stock : 0m);
            return new PosProduct(x.Id, x.Code, x.Name, x.Category, x.Price, stock, x.Unit, x.Accent, x.CentralProductId,
                x.Cost, x.WholesalePrice, x.MinStock, x.MaxStock, x.SaleType, x.Department, x.IsFavorite,
                x.PromotionComponentsJson ?? string.Empty);
        }).ToList();
    }

    public async Task<ProductWriteResult> CreatePromotionAsync(
        string code, string name, decimal price, decimal wholesalePrice,
        IReadOnlyList<PromotionComponentInput> components, string userName,
        CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(name))
            return ProductWriteResult.Failed("El código y el nombre de la promoción son obligatorios.");
        if (price < 0 || wholesalePrice < 0)
            return ProductWriteResult.Failed("El precio de la promoción no puede ser negativo.");
        if (components is null || components.Count == 0)
            return ProductWriteResult.Failed("Agrega al menos un producto a la promoción.");

        var normalized = new List<PromotionComponentInput>();
        foreach (var component in components)
        {
            if (component.ProductId <= 0 || component.Quantity <= 0)
                return ProductWriteResult.Failed("Cada componente debe tener producto y cantidad mayores a cero.");
            var existing = normalized.FirstOrDefault(x => x.ProductId == component.ProductId);
            if (existing is null)
                normalized.Add(new PromotionComponentInput(component.ProductId, component.Quantity));
            else
            {
                normalized.Remove(existing);
                normalized.Add(existing with { Quantity = existing.Quantity + component.Quantity });
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.Products.AnyAsync(x => x.Active && x.Code.ToLower() == code.ToLower(), cancellationToken))
            return ProductWriteResult.Failed($"Ya existe un producto con el código {code}.");

        var ids = normalized.Select(x => x.ProductId).ToArray();
        var parts = await db.Products.Where(x => ids.Contains(x.Id) && x.Active).ToListAsync(cancellationToken);
        if (parts.Count != ids.Length)
            return ProductWriteResult.Failed("Uno o más productos de la promoción no están disponibles.");
        if (parts.Any(x => PromotionCatalog.Parse(x.PromotionComponentsJson).Count > 0))
            return ProductWriteResult.Failed("No se puede incluir otra promoción dentro de una promoción.");

        var cost = normalized.Sum(c =>
        {
            var part = parts.Single(p => p.Id == c.ProductId);
            return part.Cost * c.Quantity;
        });
        var stock = PromotionCatalog.AvailableKits(normalized, id => parts.Single(p => p.Id == id).Stock);
        var json = PromotionCatalog.Serialize(normalized);

        var inactivePromo = await db.Products.FirstOrDefaultAsync(
            x => !x.Active && x.Code.ToLower() == code.ToLower(), cancellationToken);
        if (inactivePromo is not null)
        {
            inactivePromo.Active = true;
            inactivePromo.Name = name;
            inactivePromo.Category = "promos";
            inactivePromo.Cost = cost;
            inactivePromo.Price = price;
            inactivePromo.WholesalePrice = wholesalePrice;
            inactivePromo.Stock = stock;
            inactivePromo.MinStock = 0;
            inactivePromo.MaxStock = 0;
            inactivePromo.Unit = "un.";
            inactivePromo.SaleType = "Promoción";
            inactivePromo.Department = "promos";
            inactivePromo.PromotionComponentsJson = json;
            inactivePromo.IsFavorite = false;
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Promoción {Code} reactivada por {UserName} con {Count} componentes", code, userName, normalized.Count);
            return ProductWriteResult.Successful(inactivePromo.Id, "Promoción guardada correctamente.");
        }

        var product = new LocalProduct
        {
            Code = code,
            Name = name,
            Category = "promos",
            Cost = cost,
            Price = price,
            WholesalePrice = wholesalePrice,
            Stock = stock,
            MinStock = 0,
            MaxStock = 0,
            Unit = "un.",
            SaleType = "Promoción",
            Department = "promos",
            PromotionComponentsJson = json,
            Active = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Promoción {Code} creada por {UserName} con {Count} componentes", code, userName, normalized.Count);
        return ProductWriteResult.Successful(product.Id, "Promoción guardada correctamente.");
    }

    public async Task<ProductWriteResult> UpdatePromotionAsync(
        int productId, string name, decimal price, decimal wholesalePrice,
        IReadOnlyList<PromotionComponentInput> components, string userName,
        CancellationToken cancellationToken = default)
    {
        name = name.Trim();
        if (productId <= 0)
            return ProductWriteResult.Failed("Selecciona una promoción para modificar.");
        if (string.IsNullOrWhiteSpace(name))
            return ProductWriteResult.Failed("El nombre de la promoción es obligatorio.");
        if (price < 0 || wholesalePrice < 0)
            return ProductWriteResult.Failed("El precio de la promoción no puede ser negativo.");
        if (components is null || components.Count == 0)
            return ProductWriteResult.Failed("Agrega al menos un producto a la promoción.");

        var normalized = new List<PromotionComponentInput>();
        foreach (var component in components)
        {
            if (component.ProductId <= 0 || component.Quantity <= 0)
                return ProductWriteResult.Failed("Cada componente debe tener producto y cantidad mayores a cero.");
            var existing = normalized.FirstOrDefault(x => x.ProductId == component.ProductId);
            if (existing is null)
                normalized.Add(new PromotionComponentInput(component.ProductId, component.Quantity));
            else
            {
                normalized.Remove(existing);
                normalized.Add(existing with { Quantity = existing.Quantity + component.Quantity });
            }
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId && x.Active, cancellationToken);
        if (product is null)
            return ProductWriteResult.Failed("La promoción no está disponible.");
        var isPromo = PromotionCatalog.Parse(product.PromotionComponentsJson).Count > 0
            || product.SaleType.Equals("Promoción", StringComparison.OrdinalIgnoreCase);
        if (!isPromo)
            return ProductWriteResult.Failed("El producto seleccionado no es una promoción.");

        var ids = normalized.Select(x => x.ProductId).ToArray();
        var parts = await db.Products.Where(x => ids.Contains(x.Id) && x.Active).ToListAsync(cancellationToken);
        if (parts.Count != ids.Length)
            return ProductWriteResult.Failed("Uno o más productos de la promoción no están disponibles.");
        if (parts.Any(x => x.Id != productId && PromotionCatalog.Parse(x.PromotionComponentsJson).Count > 0))
            return ProductWriteResult.Failed("No se puede incluir otra promoción dentro de una promoción.");

        var cost = normalized.Sum(c =>
        {
            var part = parts.Single(p => p.Id == c.ProductId);
            return part.Cost * c.Quantity;
        });
        var stock = PromotionCatalog.AvailableKits(normalized, id => parts.Single(p => p.Id == id).Stock);
        var json = PromotionCatalog.Serialize(normalized);

        product.Name = name;
        product.Category = "promos";
        product.Cost = cost;
        product.Price = price;
        product.WholesalePrice = wholesalePrice;
        product.Stock = stock;
        product.SaleType = "Promoción";
        product.Department = "promos";
        product.PromotionComponentsJson = json;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Promoción {Code} actualizada por {UserName} con {Count} componentes", product.Code, userName, normalized.Count);
        return ProductWriteResult.Successful(product.Id, "Promoción actualizada correctamente.");
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
        var existing = await db.Products.FirstOrDefaultAsync(
            x => x.Code.ToLower() == code.ToLower(), cancellationToken);
        if (existing is not null && existing.Active)
            return ProductWriteResult.Failed($"Ya existe un producto con el código {code}.");

        if (existing is not null && !existing.Active)
        {
            // Reutiliza la fila dada de baja (libera el código sin que SyncCentral la recree).
            existing.Active = true;
            existing.Name = name;
            existing.Category = string.IsNullOrWhiteSpace(category) ? "General" : category.Trim();
            existing.Cost = cost;
            existing.Price = price;
            existing.WholesalePrice = wholesalePrice;
            existing.Stock = stock;
            existing.MinStock = minStock;
            existing.MaxStock = maxStock;
            existing.Unit = string.IsNullOrWhiteSpace(unit) ? "un." : unit.Trim();
            existing.SaleType = string.IsNullOrWhiteSpace(saleType) ? "Unidad" : saleType.Trim();
            existing.Department = string.IsNullOrWhiteSpace(department) ? "General" : department.Trim();
            existing.IsFavorite = false;
            existing.PromotionComponentsJson = string.Empty;
            if (stock != 0)
                db.InventoryMovements.Add(new LocalInventoryMovement
                {
                    ProductId = existing.Id,
                    ProductCode = code,
                    ProductName = name,
                    Quantity = stock,
                    StockBefore = 0,
                    StockAfter = stock,
                    Type = "ALTA_PRODUCTO",
                    Reference = "Reactivación de producto",
                    UserName = userName
                });
            await db.SaveChangesAsync(cancellationToken);
            return ProductWriteResult.Successful(existing.Id, "Producto guardado correctamente.");
        }

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
        // Baja lógica: SyncCentral no debe recrear ni reactivar el producto eliminado.
        product.Active = false;
        product.IsFavorite = false;
        await db.SaveChangesAsync(cancellationToken);
        return ProductWriteResult.Successful(productId, "Producto eliminado del catálogo.");
    }

    public async Task<ProductWriteResult> ToggleFavoriteAsync(int productId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var product = await db.Products.SingleOrDefaultAsync(x => x.Id == productId, cancellationToken);
        if (product is null || !product.Active)
            return ProductWriteResult.Failed("Producto no encontrado.");
        product.IsFavorite = !product.IsFavorite;
        await db.SaveChangesAsync(cancellationToken);
        return ProductWriteResult.Successful(product.Id, product.IsFavorite ? "Producto marcado como favorito." : "Favorito eliminado.");
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
                product = new LocalProduct
                {
                    Code = central.CodigoBarras,
                    Active = true,
                    Name = central.Nombre,
                    Cost = central.Costo,
                    Price = central.Precio,
                    WholesalePrice = central.PrecioMayoreo,
                    Stock = central.Stock,
                    MinStock = central.InvMinimo,
                    MaxStock = central.InvMaximo,
                    Unit = string.IsNullOrWhiteSpace(central.TipoVenta) ? "un." : central.TipoVenta,
                    Category = string.IsNullOrWhiteSpace(central.Departamento) ? "General" : central.Departamento,
                    Department = string.IsNullOrWhiteSpace(central.Departamento) ? "General" : central.Departamento,
                    CentralProductId = central.Id
                };
                db.Products.Add(product);
                byCode[central.CodigoBarras] = product;
                continue;
            }

            // Productos dados de baja localmente no se reactivan ni se recrean por sync.
            if (!product.Active)
                continue;

            product.CentralProductId = central.Id;
            // Solo alinear stock entre cajas. Nombre/precios/departamento se editan en el POS
            // y se publican con upsert; no revertir esas ediciones en cada sync.
            // Promociones: el stock visible es kits según componentes; no pisar con el valor central.
            if (PromotionCatalog.Parse(product.PromotionComponentsJson).Count == 0)
                product.Stock = central.Stock;
        }

        // Recalcular kits de promoción tras alinear componentes.
        var byId = allProducts.ToDictionary(x => x.Id);
        foreach (var product in allProducts)
        {
            if (!product.Active)
                continue;
            var components = PromotionCatalog.Parse(product.PromotionComponentsJson);
            if (components.Count == 0)
                continue;
            product.Stock = PromotionCatalog.AvailableKits(
                components, id => byId.TryGetValue(id, out var p) ? p.Stock : 0m);
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

                var promoComponents = PromotionCatalog.Parse(product.PromotionComponentsJson);
                if (promoComponents.Count > 0)
                {
                    if (item.Quantity <= 0)
                        return SaleResult.Failed($"La cantidad de '{product.Name}' debe ser mayor a cero.");

                    foreach (var component in promoComponents)
                    {
                        if (!products.TryGetValue(component.ProductId, out var part) || !part.Active)
                        {
                            // Cargar componente si no venía en el carrito
                            part = await db.Products.SingleOrDefaultAsync(x => x.Id == component.ProductId && x.Active, cancellationToken);
                            if (part is null)
                                return SaleResult.Failed($"Falta un producto de la promoción '{product.Name}'.");
                            products[part.Id] = part;
                        }

                        var need = component.Quantity * item.Quantity;
                        if (enforceInventory && need > part.Stock)
                            return SaleResult.Failed($"Stock insuficiente de '{part.Name}' para la promoción '{product.Name}'.");

                        part.Stock -= need;
                        db.InventoryMovements.Add(new LocalInventoryMovement
                        {
                            ProductId = part.Id,
                            ProductCode = part.Code,
                            ProductName = part.Name,
                            Quantity = -need,
                            StockBefore = part.Stock + need,
                            StockAfter = part.Stock,
                            Type = personalConsumption ? "CONSUMO_PERSONAL" : "VENTA_PROMO",
                            Reference = $"Promoción {product.Code}",
                            UserName = userName
                        });
                    }

                    // Stock de la promoción = kits disponibles tras descontar componentes
                    product.Stock = PromotionCatalog.AvailableKits(
                        promoComponents,
                        id => products.TryGetValue(id, out var p) ? p.Stock : 0m);
                }
                else
                {
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
                Total = lineTotal,
                UnitCost = isCommon ? 0 : product!.Cost,
                Department = isCommon
                    ? "General"
                    : (string.IsNullOrWhiteSpace(product!.Department) ? "General" : product.Department.Trim())
            });
        }

        var subtotal = lines.Sum(x => Math.Round(x.ListUnitPrice * x.Quantity, 2, MidpointRounding.AwayFromZero));
        var discount = Math.Max(0m, subtotal - lines.Sum(x => x.Total));
        // Consumo personal registra el valor de mercadería (reportes/cierre) pero no mueve efectivo.
        var total = lines.Sum(x => x.Total);
        if (!personalConsumption && paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
            receivedAmount = 0;
        else if (!personalConsumption && receivedAmount <= 0)
        {
            // Transferencia con monto 0 = venta diferida (ex-crédito). Otros métodos asumen pago completo.
            if (!paymentMethod.Equals("Transferencia", StringComparison.OrdinalIgnoreCase))
                receivedAmount = total;
        }
        if (!personalConsumption &&
            !paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase) &&
            !(paymentMethod.Equals("Transferencia", StringComparison.OrdinalIgnoreCase) && receivedAmount <= 0) &&
            receivedAmount < total)
            return SaleResult.Failed("El monto recibido es insuficiente.");

        var session = await db.CashSessions.SingleOrDefaultAsync(x => x.Id == cashSessionId && x.Open, cancellationToken)
              ?? await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (!personalConsumption && session is null)
        {
            session = new LocalCashSession { UserName = userName };
            db.CashSessions.Add(session);
            await db.SaveChangesAsync(cancellationToken);
        }

        var ticketNumber = await AllocateTicketNumberAsync(db, ticketNumberOverride, cancellationToken);
        if (ticketNumberOverride is > 0 && ticketNumber != ticketNumberOverride.Value)
        {
            logger.LogWarning(
                "Ticket central {CentralTicket} ya existe localmente; se usará folio {LocalTicket}",
                ticketNumberOverride.Value, ticketNumber);
        }

        var cashToRegister = personalConsumption
            ? 0m
            : cashSessionAmount >= 0
                ? cashSessionAmount
                : paymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase) ? total : 0m;
        // Efectivo: nunca registrar el monto recibido (incluye vuelto); solo el neto de la venta.
        if (!personalConsumption &&
            paymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase) && cashToRegister > total)
            cashToRegister = total;
        if (!personalConsumption &&
            paymentMethod.Equals("Mixto", StringComparison.OrdinalIgnoreCase) && cashToRegister > total)
            cashToRegister = total;
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
        LocalCashMovement? cashMovement = null;
        if (session is not null && cashToRegister > 0)
        {
            session.TotalSales += cashToRegister;
            cashMovement = new LocalCashMovement
            {
                CashSessionId = session.Id, Type = "VENTA", Amount = cashToRegister,
                Description = $"Venta Ticket #{ticketNumber}"
            };
            db.CashMovements.Add(cashMovement);
        }

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                break;
            }
            catch (DbUpdateException ex) when (IsSqliteUniqueConstraint(ex) && attempt < 3)
            {
                ticketNumber = await AllocateTicketNumberAsync(db, preferredTicket: null, cancellationToken);
                sale.TicketNumber = ticketNumber;
                if (cashMovement is not null)
                    cashMovement.Description = $"Venta Ticket #{ticketNumber}";
                logger.LogWarning(ex, "Colisión de TicketNumber; reintento con folio {TicketNumber}", ticketNumber);
            }
        }

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
        var existingImport = await db.Products.FirstOrDefaultAsync(x => x.Code == code.Trim(), cancellationToken);
        if (existingImport is not null && existingImport.Active)
            return false;
        var normalizedDepartment = string.IsNullOrWhiteSpace(department) ? "General" : department.Trim();
        var normalizedSaleType = string.IsNullOrWhiteSpace(saleType) ? "Unidad" : saleType.Trim();
        if (existingImport is not null && !existingImport.Active)
        {
            existingImport.Active = true;
            existingImport.Name = string.IsNullOrWhiteSpace(name) ? $"Producto {code.Trim()}" : name.Trim();
            existingImport.Category = normalizedDepartment;
            existingImport.Department = normalizedDepartment;
            existingImport.Cost = cost ?? 0;
            existingImport.Price = price ?? 0;
            existingImport.WholesalePrice = wholesalePrice ?? 0;
            existingImport.Stock = stock;
            existingImport.MinStock = minStock ?? 0;
            existingImport.MaxStock = maxStock ?? 0;
            existingImport.SaleType = normalizedSaleType;
            existingImport.Unit = normalizedSaleType;
            existingImport.IsFavorite = false;
            existingImport.PromotionComponentsJson = string.Empty;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
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

    /// <summary>
    /// Recalcula TotalSales del turno abierto (neto, sin vuelto) y devuelve la sesión actualizada.
    /// </summary>
    public async Task<LocalCashSession?> ReconcileAndGetOpenCashSessionAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var session = await db.CashSessions.FirstOrDefaultAsync(x => x.Open, cancellationToken);
        if (session is null)
            return null;
        await ReconcileSessionCashSalesAsync(db, session, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return session;
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
        // Recalcula el efectivo de ventas desde las ventas del turno (corrige vuelto mal registrado).
        await ReconcileSessionCashSalesAsync(db, session, cancellationToken);
        var expected = session.OpeningAmount + session.TotalSales + session.TotalEntries - session.TotalExits;
        session.ClosingAmount = closingAmount;
        session.Difference = closingAmount - expected;
        session.ClosedAtUtc = DateTime.UtcNow;
        session.Open = false;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Alinea TotalSales y CashSessionAmount de efectivo al neto de cada venta
    /// (Total), para que la cuadratura no incluya vuelto.
    /// </summary>
    internal static async Task ReconcileSessionCashSalesAsync(
        LocalPosDbContext db, LocalCashSession session, CancellationToken cancellationToken)
    {
        var sales = await db.Sales
            .Where(x => x.CashSessionId == session.Id && !x.Cancelled && !x.PersonalConsumption)
            .ToListAsync(cancellationToken);
        decimal cashSales = 0m;
        foreach (var sale in sales)
        {
            var impact = ComputeSaleCashDrawerImpact(sale);
            if (sale.PaymentMethod.Equals("Efectivo", StringComparison.OrdinalIgnoreCase) &&
                sale.CashSessionAmount != impact)
                sale.CashSessionAmount = impact;
            cashSales += impact;
        }

        session.TotalSales = cashSales;
    }

    /// <summary>Impacto neto en el cajón de efectivo de una venta (sin vuelto).</summary>
    internal static decimal ComputeSaleCashDrawerImpact(LocalSale sale)
    {
        if (sale.PersonalConsumption || sale.Cancelled)
            return 0m;
        var method = sale.PaymentMethod?.Trim() ?? string.Empty;
        if (method.Equals("Efectivo", StringComparison.OrdinalIgnoreCase))
            return Math.Max(0m, sale.Total);
        if (method.Equals("Mixto", StringComparison.OrdinalIgnoreCase))
            return Math.Clamp(sale.CashSessionAmount, 0m, Math.Max(0m, sale.Total));
        return 0m;
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
    string SaleType = "Unidad", string Department = "General", bool IsFavorite = false,
    string PromotionComponentsJson = "");
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

public sealed record PromotionComponentInput(int ProductId, decimal Quantity);

public sealed record PromotionCartDetail(string Code, string Name, decimal QuantityPerPromo, decimal Price, decimal Stock);

public static class PromotionCatalog
{
    public static IReadOnlyList<PromotionComponentInput> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Array.Empty<PromotionComponentInput>();
        try
        {
            var list = JsonSerializer.Deserialize<List<PromotionComponentInput>>(json);
            return list?.Where(x => x.ProductId > 0 && x.Quantity > 0).ToArray()
                   ?? Array.Empty<PromotionComponentInput>();
        }
        catch
        {
            return Array.Empty<PromotionComponentInput>();
        }
    }

    public static string Serialize(IEnumerable<PromotionComponentInput> components) =>
        JsonSerializer.Serialize(components.Select(x => new PromotionComponentInput(x.ProductId, x.Quantity)).ToArray());

    public static decimal AvailableKits(
        IReadOnlyList<PromotionComponentInput> components,
        Func<int, decimal> stockByProductId)
    {
        if (components.Count == 0)
            return 0m;
        decimal? kits = null;
        foreach (var component in components)
        {
            if (component.Quantity <= 0)
                return 0m;
            var available = Math.Floor(stockByProductId(component.ProductId) / component.Quantity);
            kits = kits is null ? available : Math.Min(kits.Value, available);
        }
        return kits ?? 0m;
    }

    public static bool IsPromotion(PosProduct product) =>
        PromotionCatalog.Parse(product.PromotionComponentsJson).Count > 0
        || product.SaleType.Equals("Promoción", StringComparison.OrdinalIgnoreCase);
}
