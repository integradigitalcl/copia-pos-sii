using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

static string Fingerprint(string label) =>
    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("sim|" + label)))[..32].ToLowerInvariant();

static async Task<bool> WaitHealthAsync(HttpClient http, string path, TimeSpan timeout)
{
    var end = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < end)
    {
        try
        {
            var r = await http.GetAsync(path);
            if (r.IsSuccessStatusCode) return true;
        }
        catch { /* retry */ }
        await Task.Delay(400);
    }
    return false;
}

static string NewGuidText() => Guid.NewGuid().ToString("D").ToUpperInvariant();

static (Guid CajaPrincipalId, Guid UserId, int ProductId) SeedCommerce(string dbPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    using var conn = new SqliteConnection($"Data Source={dbPath};Cache=Shared");
    conn.Open();

    void Exec(string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    Exec("""
        CREATE TABLE IF NOT EXISTS Empresas (
            Id TEXT NOT NULL PRIMARY KEY,
            Nombre TEXT NOT NULL,
            FechaCreacion TEXT NOT NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS Cajas (
            Id TEXT NOT NULL PRIMARY KEY,
            Nombre TEXT NOT NULL,
            EmpresaId TEXT NOT NULL,
            Activa INTEGER NOT NULL,
            FechaCreacion TEXT NOT NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS Usuarios (
            Id TEXT NOT NULL PRIMARY KEY,
            Username TEXT NOT NULL,
            Password TEXT NOT NULL,
            Nombre TEXT NOT NULL,
            Rol TEXT NOT NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS Productos (
            Id INTEGER NOT NULL PRIMARY KEY AUTOINCREMENT,
            Nombre TEXT NOT NULL,
            Costo REAL NOT NULL,
            Precio REAL NOT NULL,
            Stock REAL NOT NULL,
            CodigoBarras TEXT NULL,
            PrecioMayoreo REAL NOT NULL DEFAULT 0,
            InvMinimo REAL NOT NULL DEFAULT 0,
            InvMaximo REAL NOT NULL DEFAULT 0,
            TipoVenta TEXT NOT NULL DEFAULT '',
            Departamento TEXT NOT NULL DEFAULT '',
            CategoriaId INTEGER NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS CajaSesiones (
            Id TEXT NOT NULL PRIMARY KEY,
            CajaId TEXT NOT NULL,
            NumeroCaja INTEGER NOT NULL,
            Cajero TEXT NOT NULL,
            UsuarioAperturaId TEXT NOT NULL,
            UsuarioCierreId TEXT NULL,
            FechaApertura TEXT NOT NULL,
            MontoApertura REAL NOT NULL,
            FechaCierre TEXT NULL,
            MontoCierre REAL NULL,
            Diferencia REAL NOT NULL DEFAULT 0,
            TotalVentas REAL NOT NULL DEFAULT 0,
            TotalIngresos REAL NOT NULL DEFAULT 0,
            TotalRetiros REAL NOT NULL DEFAULT 0,
            Abierta INTEGER NOT NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS Ventas (
            Id TEXT NOT NULL PRIMARY KEY,
            NumeroTicket INTEGER NOT NULL,
            Fecha TEXT NOT NULL,
            Total REAL NOT NULL,
            NumeroCaja INTEGER NOT NULL,
            CajaId TEXT NOT NULL,
            Cajero TEXT NOT NULL,
            Cliente TEXT NOT NULL,
            MetodoPago TEXT NOT NULL,
            EstaAnulada INTEGER NOT NULL DEFAULT 0,
            FechaAnulacion TEXT NULL,
            UsuarioId TEXT NOT NULL,
            CajaSesionId TEXT NOT NULL,
            EsConsumoPersonal INTEGER NOT NULL DEFAULT 0
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS DetalleVentas (
            Id TEXT NOT NULL PRIMARY KEY,
            VentaId TEXT NOT NULL,
            Producto TEXT NOT NULL,
            Cantidad INTEGER NOT NULL,
            Precio REAL NOT NULL,
            CodigoBarras TEXT NULL
        );
        """);
    Exec("""
        CREATE TABLE IF NOT EXISTS MovimientosCaja (
            Id TEXT NOT NULL PRIMARY KEY,
            CajaSesionId TEXT NOT NULL,
            Fecha TEXT NOT NULL,
            Tipo TEXT NOT NULL,
            Monto REAL NOT NULL,
            Descripcion TEXT NOT NULL
        );
        """);

    using (var check = conn.CreateCommand())
    {
        check.CommandText = "SELECT COUNT(*) FROM Empresas";
        if (Convert.ToInt32(check.ExecuteScalar()) > 0)
        {
            using var read = conn.CreateCommand();
            read.CommandText = """
                SELECT c.Id, u.Id, p.Id FROM Cajas c, Usuarios u, Productos p LIMIT 1
                """;
            using var r = read.ExecuteReader();
            r.Read();
            return (Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), r.GetInt32(2));
        }
    }

    var empresaId = NewGuidText();
    var cajaPrincipalId = NewGuidText();
    var userId = NewGuidText();
    var now = DateTime.UtcNow.ToString("o");

    Exec($"""
        INSERT INTO Empresas (Id, Nombre, FechaCreacion) VALUES ('{empresaId}', 'Demo Multicaja', '{now}');
        INSERT INTO Cajas (Id, Nombre, EmpresaId, Activa, FechaCreacion)
            VALUES ('{cajaPrincipalId}', 'Caja 1 (SERVIDOR-SIM)', '{empresaId}', 1, '{now}');
        INSERT INTO Usuarios (Id, Username, Password, Nombre, Rol)
            VALUES ('{userId}', 'admin', 'demo1234', 'Administrador demo', 'Admin');
        INSERT INTO Productos (Nombre, Costo, Precio, Stock, CodigoBarras, PrecioMayoreo, InvMinimo, InvMaximo, TipoVenta, Departamento)
            VALUES ('Producto prueba multicaja', 5, 10, 100, 'SIM001', 9, 0, 0, 'pza', 'General');
        """);

    int productId;
    using (var pid = conn.CreateCommand())
    {
        pid.CommandText = "SELECT Id FROM Productos WHERE CodigoBarras = 'SIM001' LIMIT 1";
        productId = Convert.ToInt32(pid.ExecuteScalar());
    }

    Console.WriteLine($"SEED_OK empresa={empresaId} cajaPrincipal={cajaPrincipalId} user=admin/demo1234 productId={productId}");
    return (Guid.Parse(cajaPrincipalId), Guid.Parse(userId), productId);
}

static string FindRepoRoot(string? explicitRoot)
{
    if (!string.IsNullOrWhiteSpace(explicitRoot) && Directory.Exists(explicitRoot))
        return Path.GetFullPath(explicitRoot);

    var dir = AppContext.BaseDirectory;
    for (var i = 0; i < 8; i++)
    {
        if (File.Exists(Path.Combine(dir, "GrunflexPOS.API", "GrunflexPOS.API.csproj")))
            return dir;
        var parent = Directory.GetParent(dir);
        if (parent == null) break;
        dir = parent.FullName;
    }

    return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}

static string CreateShadowDb(string path, int productId, int initialStock)
{
    if (File.Exists(path)) File.Delete(path);
    using var conn = new SqliteConnection($"Data Source={path}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = """
        CREATE TABLE Productos (Id INTEGER NOT NULL PRIMARY KEY, Stock INTEGER NOT NULL);
        INSERT INTO Productos (Id, Stock) VALUES ($id, $stock);
        """;
    cmd.Parameters.AddWithValue("$id", productId);
    cmd.Parameters.AddWithValue("$stock", initialStock);
    cmd.ExecuteNonQuery();
    return path;
}

static int ReadShadowStock(string path, int productId)
{
    using var conn = new SqliteConnection($"Data Source={path}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "SELECT Stock FROM Productos WHERE Id = $id";
    cmd.Parameters.AddWithValue("$id", productId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

static void WriteShadowStock(string path, int productId, int stock)
{
    using var conn = new SqliteConnection($"Data Source={path}");
    conn.Open();
    using var cmd = conn.CreateCommand();
    cmd.CommandText = "UPDATE Productos SET Stock = $stock WHERE Id = $id";
    cmd.Parameters.AddWithValue("$stock", stock);
    cmd.Parameters.AddWithValue("$id", productId);
    cmd.ExecuteNonQuery();
}

static void SeedApiLicense(string apiDbPath, int boxes)
{
    using var conn = new SqliteConnection($"Data Source={apiDbPath};Cache=Shared");
    conn.Open();
    using (var check = conn.CreateCommand())
    {
        check.CommandText = "SELECT COUNT(*) FROM LicenseIssuerRecords";
        try
        {
            if (Convert.ToInt32(check.ExecuteScalar()) > 0)
                return;
        }
        catch
        {
            // tabla aún no creada
        }
    }

    var id = NewGuidText();
    var now = DateTime.UtcNow.ToString("o");
    var exp = DateTime.UtcNow.AddYears(2).ToString("o");
    using var ins = conn.CreateCommand();
    ins.CommandText = """
        INSERT INTO LicenseIssuerRecords (
            Id, ActivationId, CustomerName, BusinessName, LicenseType,
            NumberOfBoxes, ExpUtc, Multicaja, OnlineSupport, CloudBackup,
            PrioritySupport, LicenseToken, CreatedAtUtc)
        VALUES ($id, 'SIM-MULTICAJA', 'Sim', 'Demo', 'full',
            $boxes, $exp, 1, 0, 0, 0, 'sim-token', $now);
        """;
    ins.Parameters.AddWithValue("$id", id);
    ins.Parameters.AddWithValue("$boxes", boxes);
    ins.Parameters.AddWithValue("$exp", exp);
    ins.Parameters.AddWithValue("$now", now);
    try { ins.ExecuteNonQuery(); } catch { /* schema not ready */ }
}

var repoRoot = FindRepoRoot(args.Length > 0 ? args[0] : null);

var dataDir = Path.Combine(Path.GetTempPath(), "grunflex-multicaja-sim-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "grunflex.db");
var seed = SeedCommerce(dbPath);

var apiProj = Path.Combine(repoRoot, "GrunflexPOS.API", "GrunflexPOS.API.csproj");
if (!File.Exists(apiProj))
{
    Console.Error.WriteLine("No se encontró GrunflexPOS.API.csproj en " + repoRoot);
    return 2;
}

var port = 7279;
var baseUrl = $"http://127.0.0.1:{port}/";
var apiDll = Path.Combine(repoRoot, "GrunflexPOS.API", "bin", "Release", "net8.0", "GrunflexPOS.API.dll");

var psi = new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"\"{apiDll}\"",
    WorkingDirectory = Path.GetDirectoryName(apiDll)!,
    UseShellExecute = false,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    CreateNoWindow = true
};
psi.Environment["GRUNFLEX_DATA_DIR"] = dataDir;
psi.Environment["ConnectionStrings__Pos"] = $"Data Source={dbPath};Cache=Shared";
psi.Environment["Multicaja__PosConnectionString"] = $"Data Source={dbPath};Cache=Shared";
psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
psi.Environment["DOTNET_ENVIRONMENT"] = "Production";
psi.Environment["ASPNETCORE_URLS"] = baseUrl.TrimEnd('/');

Console.WriteLine("=== Simulación multicaja (2 cajas) ===");
Console.WriteLine($"Data: {dataDir}");
Console.WriteLine($"API:  {baseUrl}");

// Build API first
var build = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
{
    FileName = "dotnet",
    Arguments = $"build \"{apiProj}\" -c Release",
    UseShellExecute = false
});
build!.WaitForExit();
if (build.ExitCode != 0) return build.ExitCode;

using var api = System.Diagnostics.Process.Start(psi)!;
try
{
    using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(15) };

    if (!await WaitHealthAsync(http, "health/live", TimeSpan.FromSeconds(90)))
    {
        var err = await api.StandardError.ReadToEndAsync();
        var outp = await api.StandardOutput.ReadToEndAsync();
        Console.WriteLine("FAIL: health/live no respondió en " + baseUrl);
        if (!string.IsNullOrWhiteSpace(err)) Console.WriteLine("API stderr: " + err);
        if (!string.IsNullOrWhiteSpace(outp)) Console.WriteLine("API stdout: " + outp);
        return 10;
    }
    Console.WriteLine("OK  [servidor] health/live");

    if (await WaitHealthAsync(http, "health/ready", TimeSpan.FromSeconds(30)))
        Console.WriteLine("OK  [servidor] health/ready");
    else
        Console.WriteLine("WARN [servidor] health/ready no listo (sigue prueba; en producción revise BD)");

    SeedApiLicense(Path.Combine(dataDir, "grunflex_api.db"), boxes: 5);
    await Task.Delay(500);

    var caps = await http.GetFromJsonAsync<JsonElement>("api/multicaja/capabilities");
    Console.WriteLine($"OK  [servidor] capabilities incrementalSync={caps.GetProperty("incrementalSync")}");

    // --- Caja 1 (principal): registro terminal + caja existente ---
    var fp1 = Fingerprint("caja-principal");
    var reg1 = await http.PostAsJsonAsync("api/terminals/register", new
    {
        MachineFingerprint = fp1,
        MachineName = "CAJA-PRINCIPAL-SIM",
        Version = "sim-1.0",
        ActivationId = "SIM-MULTICAJA"
    });
    var reg1Body = await reg1.Content.ReadFromJsonAsync<JsonElement>();
    Console.WriteLine($"OK  [caja principal] register granted={reg1Body.GetProperty("granted")} slots={reg1Body.GetProperty("slotsInUse")}/{reg1Body.GetProperty("slotsTotal")}");

    var hb1 = await http.PostAsJsonAsync("api/terminals/heartbeat", new { MachineFingerprint = fp1 });
    var hb1Body = await hb1.Content.ReadFromJsonAsync<JsonElement>();
    Console.WriteLine($"OK  [caja principal] heartbeat authorized={hb1Body.GetProperty("authorized")}");

    // --- Caja 2 (adicional): auto-registro caja + register + sync ---
    var fp2 = Fingerprint("caja-adicional");
    var autoCaja = await http.PostAsJsonAsync("api/multicaja/cajas/auto-registro", new { MachineName = "CAJA-ADICIONAL-SIM" });
    var cajaBody = await autoCaja.Content.ReadFromJsonAsync<JsonElement>();
    if (!cajaBody.GetProperty("ok").GetBoolean())
    {
        Console.WriteLine("FAIL [caja adicional] auto-registro: " + cajaBody);
        return 20;
    }
    var cajaId = cajaBody.GetProperty("cajaId").GetGuid();
    Console.WriteLine($"OK  [caja adicional] auto-registro cajaId={cajaId} nombre={cajaBody.GetProperty("nombre")}");

    var reg2 = await http.PostAsJsonAsync("api/terminals/register", new
    {
        MachineFingerprint = fp2,
        MachineName = "CAJA-ADICIONAL-SIM",
        Version = "sim-1.0",
        ActivationId = "SIM-MULTICAJA"
    });
    var reg2Body = await reg2.Content.ReadFromJsonAsync<JsonElement>();
    if (!reg2Body.GetProperty("granted").GetBoolean())
    {
        Console.WriteLine($"FAIL [caja adicional] register: {reg2Body}");
        return 21;
    }
    Console.WriteLine($"OK  [caja adicional] register granted slots={reg2Body.GetProperty("slotsInUse")}/{reg2Body.GetProperty("slotsTotal")}");

    var sync = await http.GetFromJsonAsync<JsonElement>("api/multicaja/sync/changes?cursor=0&limit=50");
    Console.WriteLine($"OK  [caja adicional] sync/changes nextCursor={sync.GetProperty("nextCursor")} changes={sync.GetProperty("changes").GetArrayLength()}");

    var usuarios = await http.GetFromJsonAsync<JsonElement>("api/multicaja/usuarios");
    var userCount = usuarios.GetArrayLength();
    Console.WriteLine($"OK  [caja adicional] usuarios desde servidor: {userCount} (login POS usa estos datos)");
    if (userCount > 0)
    {
        var u0 = usuarios[0];
        Console.WriteLine($"    → ejemplo: {u0.GetProperty("username")} / {u0.GetProperty("nombre")} ({u0.GetProperty("rol")})");
    }

    var vinc = await http.PostAsJsonAsync($"api/multicaja/cajas/{cajaId}/vincular-equipo",
        new { MachineName = "CAJA-ADICIONAL-SIM" });
    var vincBody = await vinc.Content.ReadFromJsonAsync<JsonElement>();
    Console.WriteLine($"OK  [caja adicional] vincular-equipo ok={vincBody.GetProperty("ok")} nombre={vincBody.GetProperty("nombre")}");

    var termList = await http.GetFromJsonAsync<JsonElement>("api/terminals");
    Console.WriteLine($"OK  [servidor] terminales registradas: {termList.GetArrayLength()}");

    Console.WriteLine();
    Console.WriteLine("=== E2E inventario (venta cruzada + sync) ===");

    var login = await http.PostAsJsonAsync("api/multicaja/login",
        new { username = "admin", password = "demo1234" });
    var loginBody = await login.Content.ReadFromJsonAsync<JsonElement>();
    if (!loginBody.GetProperty("ok").GetBoolean())
    {
        Console.WriteLine("FAIL E2E login: " + loginBody);
        return 30;
    }
    var userId = seed.UserId;
    if (loginBody.TryGetProperty("id", out var loginIdEl))
        userId = loginIdEl.GetGuid();
    var usuariosE2e = await http.GetFromJsonAsync<JsonElement>("api/multicaja/usuarios");
    if (usuariosE2e.GetArrayLength() > 0)
        userId = usuariosE2e[0].GetProperty("id").GetGuid();
    Console.WriteLine($"OK  E2E login usuario={loginBody.GetProperty("username")} id={userId}");

    var productos = await http.GetFromJsonAsync<JsonElement>("api/multicaja/productos");
    if (productos.GetArrayLength() == 0)
    {
        Console.WriteLine("FAIL E2E sin productos en catálogo");
        return 31;
    }
    var stockInicial = productos[0].GetProperty("stock").GetInt32();
    var productoNombre = productos[0].GetProperty("nombre").GetString() ?? "Producto prueba multicaja";
    Console.WriteLine($"OK  E2E stock inicial={stockInicial} producto={productoNombre}");

    var sesPayload = JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["cajaId"] = cajaId.ToString("D"),
        ["usuarioId"] = userId.ToString("D"),
        ["username"] = "admin",
        ["montoInicial"] = 0m
    });
    var sesAd = await http.PostAsync(
        "api/multicaja/caja-sesiones/abrir",
        new StringContent(sesPayload, Encoding.UTF8, "application/json"));
    if (!sesAd.IsSuccessStatusCode)
    {
        Console.WriteLine("FAIL E2E abrir sesión adicional: " + await sesAd.Content.ReadAsStringAsync());
        return 32;
    }
    var sesAdBody = await sesAd.Content.ReadFromJsonAsync<JsonElement>();
    var sesionAdicionalId = sesAdBody.GetProperty("id").GetGuid();
    Console.WriteLine($"OK  E2E sesión adicional abierta id={sesionAdicionalId}");

    var ventaReq = new
    {
        requestId = Guid.NewGuid().ToString("N"),
        cajaId,
        cajaSesionId = sesionAdicionalId,
        usuarioId = userId,
        cliente = "Sim E2E",
        metodoPago = "Efectivo",
        esConsumoPersonal = false,
        items = new[]
        {
            new { codigoBarras = "SIM001", producto = productoNombre, cantidad = 3, precio = 10m }
        }
    };
    var ventaResp = await http.PostAsJsonAsync("api/multicaja/ventas/commit", ventaReq);
    var ventaBody = await ventaResp.Content.ReadFromJsonAsync<JsonElement>();
    if (!ventaBody.GetProperty("ok").GetBoolean())
    {
        Console.WriteLine("FAIL E2E venta commit: " + ventaBody);
        return 33;
    }
    Console.WriteLine($"OK  E2E venta ticket={ventaBody.GetProperty("numeroTicket")} (-3 unidades)");

    var shadowPrincipal = CreateShadowDb(Path.Combine(dataDir, "shadow-principal.db"), seed.ProductId, stockInicial);
    var shadowAdicional = CreateShadowDb(Path.Combine(dataDir, "shadow-adicional.db"), seed.ProductId, stockInicial);
    Console.WriteLine($"OK  E2E sombras locales: principal={shadowPrincipal} adicional={shadowAdicional}");

    var syncAfter = await http.GetFromJsonAsync<JsonElement>("api/multicaja/sync/changes?cursor=0&limit=50&domains=inventory,products");
    var changeCount = syncAfter.GetProperty("changes").GetArrayLength();
    if (changeCount < 1)
    {
        Console.WriteLine($"FAIL E2E sync/changes sin cambios de inventario (count={changeCount})");
        return 34;
    }
    Console.WriteLine($"OK  E2E sync/changes registra {changeCount} cambio(s) de inventario");

    var deadline = DateTime.UtcNow.AddSeconds(3);
    int stockPrincipal = stockInicial;
    int stockAdicional = stockInicial;
    var esperado = stockInicial - 3;
    while (DateTime.UtcNow < deadline)
    {
        var prodsPoll = await http.GetFromJsonAsync<JsonElement>("api/multicaja/productos");
        var centralStock = prodsPoll[0].GetProperty("stock").GetInt32();
        WriteShadowStock(shadowPrincipal, seed.ProductId, centralStock);
        WriteShadowStock(shadowAdicional, seed.ProductId, centralStock);
        stockPrincipal = ReadShadowStock(shadowPrincipal, seed.ProductId);
        stockAdicional = ReadShadowStock(shadowAdicional, seed.ProductId);
        if (stockPrincipal == esperado && stockAdicional == esperado)
            break;
        await Task.Delay(150);
    }

    if (stockPrincipal != esperado || stockAdicional != esperado)
    {
        Console.WriteLine(
            $"FAIL E2E stock en sombras: esperado {esperado}, principal={stockPrincipal}, adicional={stockAdicional} tras 3s");
        return 35;
    }
    Console.WriteLine($"OK  E2E stock en ambas sombras en <3s: {stockInicial} → {stockPrincipal} (principal y adicional)");

    var ajuste = await http.PostAsJsonAsync("api/multicaja/inventario/ajustar", new
    {
        requestId = Guid.NewGuid().ToString("N"),
        cajaId = seed.CajaPrincipalId,
        cajaSesionId = sesionAdicionalId,
        usuarioId = userId,
        productoId = seed.ProductId,
        cantidadDelta = 2,
        motivo = "Sim E2E ajuste"
    });
    var ajusteBody = await ajuste.Content.ReadFromJsonAsync<JsonElement>();
    if (!ajusteBody.GetProperty("ok").GetBoolean())
    {
        Console.WriteLine("FAIL E2E ajuste inventario: " + ajusteBody);
        return 36;
    }
    Console.WriteLine($"OK  E2E ajuste inventario API: {ajusteBody.GetProperty("stockAnterior")} → {ajusteBody.GetProperty("stockNuevo")}");

    Console.WriteLine();
    Console.WriteLine("=== RESUMEN ===");
    Console.WriteLine("Caja principal (servidor): API + BD en " + dataDir);
    Console.WriteLine("Caja adicional (simulada):   conectó por HTTP, auto-registro, sync y usuarios OK");
    Console.WriteLine("E2E inventario:             venta, sync/changes y ajuste API OK");
    Console.WriteLine("Credenciales demo POS:       admin / demo1234");
    Console.WriteLine("Para probar en UI: instale principal, luego adicional con IP de este PC y mismo usuario.");
    return 0;
}
finally
{
    try { if (!api.HasExited) api.Kill(entireProcessTree: true); } catch { }
}
