using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using GrunflexPOS.Web.Services.Licensing;

namespace GrunflexPOS.Web.Services;

public sealed class MulticajaClient(
    HttpClient httpClient,
    IConfiguration configuration,
    LocalPosStore store,
    MulticajaOfflineQueue offlineQueue,
    WebLicenseState licenseState,
    ILogger<MulticajaClient> logger)
{
    private Uri? configuredBaseAddress;

    public int PendingOfflineCount => offlineQueue.PendingCount();

    public async Task<bool> IsEnabledAsync(CancellationToken cancellationToken = default) =>
        string.Equals(
            await store.GetSettingAsync("multicaja_habilitada",
                configuration["Multicaja:Enabled"] ?? "false", cancellationToken),
            "true", StringComparison.OrdinalIgnoreCase);

    public async Task<MulticajaPreparation> PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return MulticajaPreparation.Disabled;

        if (licenseState.RequireLicense && !licenseState.Multicaja)
            return MulticajaPreparation.Failed(
                "Esta función requiere licencia Multicaja activa. Active o renueve su licencia.");

        try
        {
            ConfigureBaseAddress(await store.GetSettingAsync(
                "multicaja_api_url",
                configuration["Multicaja:ApiBaseUrl"] ?? "http://127.0.0.1:7279/"));

            // Reconfirma el registro en cada arranque: si la central se reinstaló, el id guardado ya no sirve.
            var registeredCajaId = await EnsureCajaRegisteredAsync(false, cancellationToken);
            if (registeredCajaId is null)
                return MulticajaPreparation.Failed("No se pudo registrar la caja en la API central.");
            var cajaId = registeredCajaId.Value;

            // La caja principal es dueña del catálogo: lo publica antes de sincronizar de vuelta.
            var role = await store.GetSettingAsync("terminal_role", "server", cancellationToken);
            if (!string.Equals(role, "client", StringComparison.OrdinalIgnoreCase))
                await PushCatalogAsync(cancellationToken);

            var synced = await SyncCatalogAsync(cancellationToken);
            if (!synced)
                return MulticajaPreparation.Failed("No se pudo sincronizar el catálogo.");

            var replay = await ReplayPendingAsync(cancellationToken);
            var status = replay.Succeeded > 0
                ? $"Conectada · {replay.Succeeded} operación(es) sincronizada(s)"
                : replay.Remaining > 0
                    ? $"Conectada · {replay.Remaining} pendiente(s) en cola"
                    : "Conectada a la API central";
            return new MulticajaPreparation(true, true, cajaId, 0, status);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No fue posible preparar la conexión multicaja");
            return MulticajaPreparation.Failed("API central no disponible.");
        }
    }

    public async Task<bool> SyncCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return false;

        try
        {
            ConfigureBaseAddress(await store.GetSettingAsync(
                "multicaja_api_url",
                configuration["Multicaja:ApiBaseUrl"] ?? "http://127.0.0.1:7279/",
                cancellationToken));

            var products = await SendAsync<List<MulticajaProductDto>>(
                HttpMethod.Get, "api/multicaja/productos", null, null, cancellationToken);
            if (!products.Success || products.Value is null)
                return false;

            await store.SyncCentralProductsAsync(products.Value, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogDebug(ex, "No fue posible sincronizar catálogo multicaja");
            return false;
        }
    }

    /// <summary>Publica el catálogo local en la API central para que las ventas encuentren los productos.</summary>
    public async Task<bool> PushCatalogAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return false;

        try
        {
            var products = await store.GetProductsAsync(cancellationToken);
            if (products.Count == 0)
                return true;

            var body = products.Select(p => new
            {
                id = p.CentralProductId ?? 0,
                nombre = p.Name,
                costo = p.Cost,
                precio = p.Price,
                stock = (int)Math.Max(0m, Math.Round(p.Stock, MidpointRounding.AwayFromZero)),
                codigoBarras = p.Code,
                precioMayoreo = p.WholesalePrice,
                invMinimo = (int)Math.Max(0m, Math.Round(p.MinStock, MidpointRounding.AwayFromZero)),
                invMaximo = (int)Math.Max(0m, Math.Round(p.MaxStock, MidpointRounding.AwayFromZero)),
                tipoVenta = p.SaleType,
                departamento = string.IsNullOrWhiteSpace(p.Department) ? p.Category : p.Department
            }).ToArray();

            var response = await SendAsync<MulticajaProductoUpsertResponse>(
                HttpMethod.Post, "api/multicaja/productos/upsert", body, null, cancellationToken);
            if (response.Success && response.Value is { Ok: true })
                return true;

            logger.LogWarning("No se pudo publicar el catálogo local en la API central: {Error}",
                response.Error ?? response.Value?.Error);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogDebug(ex, "No fue posible publicar el catálogo local en la API central");
            return false;
        }
    }

    public async Task<MulticajaLoginResult> LoginAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var cajaId = await EnsureCajaRegisteredAsync(false, cancellationToken);
        if (cajaId is null)
            return MulticajaLoginResult.Failed("La caja no está registrada en la API central.");

        var response = await SendAsync<MulticajaLoginResponse>(
            HttpMethod.Post, "api/multicaja/login",
            new { username, password }, null, cancellationToken);
        if (!response.Success || response.Value is null || !response.Value.Ok)
            return MulticajaLoginResult.Failed(response.Error ?? response.Value?.Error ?? "API central no disponible.");
        var login = response.Value;

        return new(true, login.Id, cajaId.Value, Guid.Empty, login.Username, login.Rol, string.Empty);
    }

    public async Task<bool> VerifyCredentialsAsync(
        string username, string password, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<MulticajaLoginResponse>(
            HttpMethod.Post, "api/multicaja/login",
            new { username, password }, null, cancellationToken);
        return response.Success && response.Value is { Ok: true };
    }

    public async Task<MulticajaSessionResult> OpenOrAttachSessionAsync(
        Guid cajaId, Guid userId, string username, decimal openingAmount = 0,
        CancellationToken cancellationToken = default)
    {
        var existing = await SendAsync<MulticajaCajaSesionDto>(
            HttpMethod.Get, $"api/multicaja/caja-sesiones/abierta?cajaId={cajaId}",
            null, null, cancellationToken);
        if (existing.Success && existing.Value is not null)
            return new(true, existing.Value.Id, string.Empty, cajaId);

        var session = await SendAsync<MulticajaCajaSesionDto>(
            HttpMethod.Post, "api/multicaja/caja-sesiones/abrir",
            new { cajaId, usuarioId = userId, username, montoInicial = openingAmount },
            null, cancellationToken);
        if (session.Success && session.Value is not null)
            return new(true, session.Value.Id, string.Empty, cajaId);

        var error = session.Error ?? ExtractErrorMessage(session.RawJson) ?? "No se pudo abrir la sesión central.";

        // La central no conoce esta caja (por ejemplo, se reinstaló): reintenta con un registro nuevo.
        if (IsMissingCajaError(error))
        {
            var reRegistered = await EnsureCajaRegisteredAsync(true, cancellationToken);
            if (reRegistered is { } newCajaId && newCajaId != cajaId)
            {
                var retry = await SendAsync<MulticajaCajaSesionDto>(
                    HttpMethod.Post, "api/multicaja/caja-sesiones/abrir",
                    new { cajaId = newCajaId, usuarioId = userId, username, montoInicial = openingAmount },
                    null, cancellationToken);
                if (retry.Success && retry.Value is not null)
                    return new(true, retry.Value.Id, string.Empty, newCajaId);
                error = retry.Error ?? ExtractErrorMessage(retry.RawJson) ?? error;
            }
        }

        return new(false, Guid.Empty, error);
    }

    public async Task<bool> UpdateCentralPasswordAsync(
        Guid userId, string username, string role, string displayName,
        string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        var verify = await VerifyCredentialsAsync(username, currentPassword, cancellationToken);
        if (!verify)
            return false;

        var response = await SendAsync<MulticajaUsuarioDto>(
            HttpMethod.Put, $"api/multicaja/usuarios/{userId}",
            new { username, nombre = displayName, rol = role, password = newPassword },
            null, cancellationToken);
        return response.Success && response.Value is not null;
    }

    public async Task<MulticajaSessionResult> OpenSessionAsync(
        Guid cajaId, Guid userId, string username, decimal openingAmount = 0,
        CancellationToken cancellationToken = default) =>
        await OpenOrAttachSessionAsync(cajaId, userId, username, openingAmount, cancellationToken);

    public async Task<MulticajaOperationResult> CommitSaleAsync(
        Guid cajaId, Guid sessionId, Guid userId, IReadOnlyCollection<CartItem> items,
        string paymentMethod, bool personalConsumption, decimal cashPortion = 0,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var terminal = await store.GetSettingAsync("terminal_code",
            configuration["Multicaja:TerminalCode"] ?? Environment.MachineName, cancellationToken);
        var catalog = await store.GetProductsAsync(cancellationToken);
        var byId = catalog.ToDictionary(p => p.Id);
        var body = new
        {
            requestId,
            cajaId,
            cajaSesionId = sessionId,
            usuarioId = userId,
            terminalCode = terminal,
            cliente = "Público en general",
            metodoPago = personalConsumption ? "Consumo personal" : paymentMethod,
            esConsumoPersonal = personalConsumption,
            montoEfectivo = personalConsumption ? 0m : cashPortion,
            items = items.Select(item =>
            {
                var comps = PromotionCatalog.Parse(item.Product.PromotionComponentsJson);
                return new
                {
                    productoId = item.Product.CentralProductId ?? 0,
                    codigoBarras = item.Product.Code,
                    producto = item.Product.Name,
                    cantidad = (int)item.Quantity,
                    precio = item.Quantity == 0 ? 0 : Math.Round(
                        item.EffectiveUnitPrice * (1m - item.DiscountPercentage / 100m), 2),
                    // Permiten que la central cree el producto si aún no lo tiene.
                    costo = item.Product.Cost,
                    stock = (int)Math.Max(0m, Math.Round(item.Product.Stock, MidpointRounding.AwayFromZero)),
                    departamento = string.IsNullOrWhiteSpace(item.Product.Department)
                        ? item.Product.Category
                        : item.Product.Department,
                    tipoVenta = item.Product.SaleType,
                    componentes = comps.Count == 0
                        ? null
                        : comps.Select(c =>
                        {
                            byId.TryGetValue(c.ProductId, out var part);
                            return new
                            {
                                productoId = part?.CentralProductId ?? 0,
                                codigoBarras = part?.Code,
                                producto = part?.Name ?? string.Empty,
                                cantidadPorKit = (int)Math.Max(1m, Math.Round(c.Quantity, MidpointRounding.AwayFromZero))
                            };
                        }).ToArray()
                };
            }).ToArray()
        };
        return await PostOrEnqueueAsync<MulticajaVentaCommitResponse>(
            MulticajaOfflineQueue.VentaCommit, requestId, "api/multicaja/ventas/commit", body,
            response =>
            {
                var result = response.Value!;
                return MulticajaOperationResult.Ok(result.NumeroTicket.ToString());
            },
            response => response.Value?.Error ?? response.Error ?? "La API rechazó la venta.",
            cancellationToken);
    }

    public async Task<MulticajaOperationResult> AdjustInventoryAsync(
        Guid cajaId, Guid sessionId, Guid userId, int productId, int delta,
        string reason, CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var body = new
        {
            requestId, cajaId, cajaSesionId = sessionId, usuarioId = userId,
            productoId = productId, cantidadDelta = delta, motivo = reason
        };
        return await PostOrEnqueueAsync<MulticajaInventoryResponse>(
            MulticajaOfflineQueue.InventarioAjuste, requestId, "api/multicaja/inventario/ajustar", body,
            response => MulticajaOperationResult.Ok(response.Value!.StockNuevo.ToString()),
            response => response.Value?.Error ?? response.Error ?? "La API rechazó el ajuste.",
            cancellationToken);
    }

    public async Task<MulticajaOperationResult> SyncProductStockAsync(
        Guid cajaId, Guid sessionId, Guid userId, int centralProductId, decimal previousStock,
        decimal newStock, string productName, CancellationToken cancellationToken = default)
    {
        var delta = (int)Math.Round(newStock - previousStock, MidpointRounding.AwayFromZero);
        if (delta == 0)
            return MulticajaOperationResult.Ok("Sin cambios de stock.");
        return await AdjustInventoryAsync(
            cajaId, sessionId, userId, centralProductId, delta,
            $"Sync catálogo web: {productName}", cancellationToken);
    }

    public async Task<MulticajaOperationResult> RegisterCashMovementAsync(
        Guid cajaId, Guid sessionId, Guid userId, string type, decimal amount, string description,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var body = new
        {
            requestId, cajaId, cajaSesionId = sessionId, usuarioId = userId,
            tipo = type, monto = amount, descripcion = description
        };
        return await PostOrEnqueueAsync<MulticajaCashMovementResponse>(
            MulticajaOfflineQueue.MovimientoCaja, requestId, "api/multicaja/caja-sesiones/movimiento", body,
            _ => MulticajaOperationResult.Ok($"Movimiento {type} registrado."),
            response => response.Value?.Error ?? response.Error ?? "La API rechazó el movimiento.",
            cancellationToken);
    }

    public async Task<MulticajaOperationResult> CloseCashSessionAsync(
        Guid cajaId, Guid sessionId, Guid userId, decimal countedAmount,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var body = new
        {
            requestId, cajaId, cajaSesionId = sessionId,
            usuarioCierreId = userId, montoContado = countedAmount
        };
        return await PostOrEnqueueAsync<MulticajaCloseResponse>(
            MulticajaOfflineQueue.CierreSesion, requestId, "api/multicaja/caja-sesiones/cerrar", body,
            response => MulticajaOperationResult.Ok($"Cierre registrado. Diferencia: {response.Value!.Diferencia:0.##}"),
            response => response.Value?.Error ?? response.Error ?? "La API rechazó el cierre.",
            cancellationToken);
    }

    public async Task<MulticajaOperationResult> VoidSaleAsync(
        Guid cajaId, Guid sessionId, Guid userId, int ticketNumber,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var terminal = await store.GetSettingAsync("terminal_code",
            configuration["Multicaja:TerminalCode"] ?? Environment.MachineName, cancellationToken);
        var body = new
        {
            requestId, cajaId, cajaSesionId = sessionId, usuarioId = userId,
            terminalCode = terminal, numeroTicket = ticketNumber
        };
        return await PostOrEnqueueAsync<MulticajaVoidResponse>(
            MulticajaOfflineQueue.AnularVenta, requestId, "api/multicaja/ventas/anular", body,
            _ => MulticajaOperationResult.Ok($"Venta #{ticketNumber} anulada en central."),
            response => response.Value?.Error ?? response.Error ?? "La API rechazó la anulación.",
            cancellationToken);
    }

    public async Task<MulticajaOperationResult> RefundLineAsync(
        Guid cajaId, Guid sessionId, Guid userId, int ticketNumber,
        string code, string productName, decimal price, int quantity,
        CancellationToken cancellationToken = default)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var terminal = await store.GetSettingAsync("terminal_code",
            configuration["Multicaja:TerminalCode"] ?? Environment.MachineName, cancellationToken);
        var body = new
        {
            requestId, cajaId, cajaSesionId = sessionId, usuarioId = userId,
            terminalCode = terminal, numeroTicket = ticketNumber,
            codigoBarras = code, producto = productName, precio = price, cantidad = quantity
        };
        return await PostOrEnqueueAsync<MulticajaRefundResponse>(
            MulticajaOfflineQueue.DevolucionLinea, requestId, "api/multicaja/ventas/devolucion-linea", body,
            _ => MulticajaOperationResult.Ok("Devolución registrada en central."),
            response => response.Value?.Error ?? response.Error ?? "La API rechazó la devolución.",
            cancellationToken);
    }

    public async Task<MulticajaReplayResult> ReplayPendingAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return MulticajaReplayResult.Empty;

        var pending = offlineQueue.ListPending();
        if (pending.Count == 0)
            return MulticajaReplayResult.Empty;

        var succeeded = 0;
        var failed = 0;
        foreach (var item in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = await ReplayItemAsync(item, cancellationToken);
            if (ok)
            {
                offlineQueue.MarkDone(item.Id);
                succeeded++;
            }
            else
            {
                failed++;
            }
        }

        return new MulticajaReplayResult(pending.Count, succeeded, failed, offlineQueue.PendingCount());
    }

    private async Task<bool> ReplayItemAsync(MulticajaOfflineQueueItem item, CancellationToken cancellationToken)
    {
        try
        {
            var body = JsonSerializer.Deserialize<JsonElement>(item.PayloadJson);
            MulticajaOperationResult result = item.Kind switch
            {
                MulticajaOfflineQueue.VentaCommit => await ReplayPostAsync<MulticajaVentaCommitResponse>(
                    "api/multicaja/ventas/commit", body, item.Id,
                    response => response.Ok ? MulticajaOperationResult.Ok(response.NumeroTicket.ToString())
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó la venta."),
                    cancellationToken),
                MulticajaOfflineQueue.AnularVenta => await ReplayPostAsync<MulticajaVoidResponse>(
                    "api/multicaja/ventas/anular", body, item.Id,
                    response => response.Ok ? MulticajaOperationResult.Ok("Anulación sincronizada.")
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó la anulación."),
                    cancellationToken),
                MulticajaOfflineQueue.DevolucionLinea => await ReplayPostAsync<MulticajaRefundResponse>(
                    "api/multicaja/ventas/devolucion-linea", body, item.Id,
                    response => response.Ok ? MulticajaOperationResult.Ok("Devolución sincronizada.")
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó la devolución."),
                    cancellationToken),
                MulticajaOfflineQueue.CierreSesion => await ReplayPostAsync<MulticajaCloseResponse>(
                    "api/multicaja/caja-sesiones/cerrar", body, item.Id,
                    response => response.Ok
                        ? MulticajaOperationResult.Ok($"Cierre sincronizado. Diferencia: {response.Diferencia:0.##}")
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó el cierre."),
                    cancellationToken),
                MulticajaOfflineQueue.MovimientoCaja => await ReplayPostAsync<MulticajaCashMovementResponse>(
                    "api/multicaja/caja-sesiones/movimiento", body, item.Id,
                    response => response.Ok ? MulticajaOperationResult.Ok("Movimiento sincronizado.")
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó el movimiento."),
                    cancellationToken),
                MulticajaOfflineQueue.InventarioAjuste => await ReplayPostAsync<MulticajaInventoryResponse>(
                    "api/multicaja/inventario/ajustar", body, item.Id,
                    response => response.Ok ? MulticajaOperationResult.Ok(response.StockNuevo.ToString())
                        : MulticajaOperationResult.Failed(response.Error ?? "La API rechazó el ajuste."),
                    cancellationToken),
                _ => MulticajaOperationResult.Failed($"Tipo de cola desconocido: {item.Kind}")
            };

            if (result.Success)
                return true;

            offlineQueue.MarkFailed(item.Id, result.Message, permanent: !IsOfflineMessage(result.Message));
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            offlineQueue.MarkFailed(item.Id, ex.Message);
            return false;
        }
    }

    private async Task<MulticajaOperationResult> ReplayPostAsync<T>(
        string path, JsonElement body, string requestId,
        Func<T, MulticajaOperationResult> map,
        CancellationToken cancellationToken) where T : class
    {
        var payload = JsonSerializer.Deserialize<object>(body.GetRawText());
        var response = await SendAsync<T>(HttpMethod.Post, path, payload, requestId, cancellationToken);
        if (!response.Success || response.Value is null)
            return MulticajaOperationResult.Failed(response.Error ?? "API central no disponible.");
        return map(response.Value);
    }

    private async Task<MulticajaOperationResult> PostOrEnqueueAsync<T>(
        string kind, string requestId, string path, object body,
        Func<ApiResult<T>, MulticajaOperationResult> onSuccess,
        Func<ApiResult<T>, string> onFailure,
        CancellationToken cancellationToken) where T : class
    {
        var response = await SendAsync<T>(HttpMethod.Post, path, body, requestId, cancellationToken);
        if (response.Success && response.Value is not null && IsOkResponse(response.Value))
            return onSuccess(response);

        if (ShouldEnqueue(response) && offlineQueue.TryEnqueue(kind, body, requestId))
            return MulticajaOperationResult.Enqueued($"Operación encolada para sincronizar cuando vuelva la API ({kind}).");

        return MulticajaOperationResult.Failed(onFailure(response));
    }

    private static bool IsOkResponse<T>(T value) => value switch
    {
        MulticajaVentaCommitResponse sale => sale.Ok,
        MulticajaInventoryResponse inventory => inventory.Ok,
        MulticajaCashMovementResponse cash => cash.Ok,
        MulticajaCloseResponse close => close.Ok,
        MulticajaVoidResponse cancel => cancel.Ok,
        MulticajaRefundResponse refund => refund.Ok,
        _ => true
    };

    private static bool ShouldEnqueue<T>(ApiResult<T> response)
    {
        // Un rechazo de negocio (la API respondió Ok=false) no se reintenta: encolarlo duplicaría la operación.
        if (IsBusinessRejection(response.Value))
            return false;

        // 4xx es un error del cliente: reintentarlo no cambia el resultado.
        if (response.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
            return false;

        if (response.StatusCode is not null && response.StatusCode < HttpStatusCode.InternalServerError)
            return response.Error?.Contains("no disponible", StringComparison.OrdinalIgnoreCase) == true;

        // Sin respuesta o 5xx: la central no está disponible, se encola.
        return true;
    }

    private static bool IsBusinessRejection<T>(T? value) => value switch
    {
        MulticajaVentaCommitResponse sale => !sale.Ok,
        MulticajaInventoryResponse inventory => !inventory.Ok,
        MulticajaCashMovementResponse cash => !cash.Ok,
        MulticajaCloseResponse close => !close.Ok,
        MulticajaVoidResponse cancel => !cancel.Ok,
        MulticajaRefundResponse refund => !refund.Ok,
        _ => false
    };

    private static bool IsMissingCajaError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("Caja no existe", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("Caja no encontrada", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("no está registrada", StringComparison.OrdinalIgnoreCase));

    /// <summary>Registra la caja en la central y guarda el id devuelto. Con force ignora el id local.</summary>
    private async Task<Guid?> EnsureCajaRegisteredAsync(bool force, CancellationToken cancellationToken)
    {
        var registration = await SendAsync<MulticajaCajaAutoRegistroResponse>(
            HttpMethod.Post, "api/multicaja/cajas/auto-registro",
            new { machineName = Environment.MachineName }, null, cancellationToken);

        if (!registration.Success || registration.Value is null || !registration.Value.Ok)
        {
            logger.LogWarning("Auto-registro de caja falló: {Error}",
                registration.Error ?? registration.Value?.Error);
            return force ? null : await GetCajaIdAsync(cancellationToken);
        }

        var cajaId = registration.Value.CajaId;
        if (cajaId == Guid.Empty)
            return await GetCajaIdAsync(cancellationToken);

        await store.SetSettingAsync("multicaja_caja_id", cajaId.ToString(), cancellationToken);
        return cajaId;
    }

    private static bool IsOfflineMessage(string message) =>
        message.Contains("no disponible", StringComparison.OrdinalIgnoreCase);

    private async Task<Guid?> GetCajaIdAsync(CancellationToken cancellationToken)
    {
        var value = await store.GetSettingAsync(
            "multicaja_caja_id", configuration["Multicaja:CajaId"] ?? string.Empty, cancellationToken);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private async Task<ApiResult<MulticajaCajaSesionDto>> OpenOrGetSessionAsync(
        Guid cajaId, Guid userId, string username, CancellationToken cancellationToken) =>
        await OpenOrAttachSessionInternalAsync(cajaId, userId, username, 0, cancellationToken);

    private async Task<ApiResult<MulticajaCajaSesionDto>> OpenOrAttachSessionInternalAsync(
        Guid cajaId, Guid userId, string username, decimal openingAmount, CancellationToken cancellationToken)
    {
        var existing = await SendAsync<MulticajaCajaSesionDto>(
            HttpMethod.Get, $"api/multicaja/caja-sesiones/abierta?cajaId={cajaId}",
            null, null, cancellationToken);
        if (existing.Success && existing.Value is not null)
            return existing;
        if (existing.StatusCode != HttpStatusCode.NotFound)
            return existing;
        return await SendAsync<MulticajaCajaSesionDto>(
            HttpMethod.Post, "api/multicaja/caja-sesiones/abrir",
            new { cajaId, usuarioId = userId, username, montoInicial = openingAmount },
            null, cancellationToken);
    }

    private static string? ExtractErrorMessage(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString();
        }
        catch { }
        return null;
    }

    private async Task<ApiResult<T>> SendAsync<T>(
        HttpMethod method, string path, object? body, string? requestId,
        CancellationToken cancellationToken)
    {
        try
        {
            ConfigureBaseAddress(await store.GetSettingAsync(
                "multicaja_api_url",
                configuration["Multicaja:ApiBaseUrl"] ?? "http://127.0.0.1:7279/",
                cancellationToken));
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            var key = await store.GetSettingAsync(
                "multicaja_shared_secret", configuration["Multicaja:SharedSecret"] ?? string.Empty, cancellationToken);
            var terminal = await store.GetSettingAsync(
                "terminal_code", configuration["Multicaja:TerminalCode"] ?? Environment.MachineName, cancellationToken);
            AddHeaders(request, requestId, key, terminal);
            using var response = await httpClient.SendAsync(request, cancellationToken);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var value = string.IsNullOrWhiteSpace(json)
                ? default
                : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            var error = response.IsSuccessStatusCode ? null : ExtractErrorMessage(json);
            return new(response.IsSuccessStatusCode, value, response.StatusCode, error, json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            logger.LogDebug(ex, "Fallo en llamada multicaja {Method} {Path}", method, path);
            return new(false, default, null, "API central no disponible.");
        }
    }

    private void ConfigureBaseAddress(string rawUrl)
    {
        var url = (string.IsNullOrWhiteSpace(rawUrl) ? "http://127.0.0.1:7279/" : rawUrl).TrimEnd('/') + "/";
        var address = new Uri(url, UriKind.Absolute);
        if (configuredBaseAddress == address)
            return;
        httpClient.BaseAddress = address;
        configuredBaseAddress = address;
    }

    private static void AddHeaders(HttpRequestMessage request, string? requestId, string key, string terminal)
    {
        request.Headers.TryAddWithoutValidation("X-Grunflex-Multicaja-Key", key);
        if (!string.IsNullOrWhiteSpace(requestId))
            request.Headers.TryAddWithoutValidation("X-Grunflex-Request-Id", requestId);
        request.Headers.TryAddWithoutValidation("X-Grunflex-Terminal", terminal);
    }
}

public sealed record MulticajaPreparation(bool Enabled, bool Connected, Guid? CajaId, int Products, string Status)
{
    public static MulticajaPreparation Disabled => new(false, false, null, 0, "Multicaja desactivada");
    public static MulticajaPreparation Ready(Guid id, int products) =>
        new(true, true, id, products, "Conectada a la API central");
    public static MulticajaPreparation Failed(string error) => new(true, false, null, 0, error);
}

public sealed record MulticajaLoginResult(
    bool Success, Guid UserId, Guid CajaId, Guid SessionId, string Username, string Role, string Error)
{
    public static MulticajaLoginResult Failed(string error) =>
        new(false, Guid.Empty, Guid.Empty, Guid.Empty, string.Empty, string.Empty, error);
}

public sealed record MulticajaSessionResult(bool Success, Guid SessionId, string Error, Guid? CajaId = null);

public sealed record MulticajaProductoUpsertResponse
{
    public bool Ok { get; init; }
    public int Upserted { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaOperationResult(bool Success, string Message, bool Queued = false)
{
    public static MulticajaOperationResult Ok(string message) => new(true, message);
    public static MulticajaOperationResult Failed(string message) => new(false, message);
    public static MulticajaOperationResult Enqueued(string message) => new(true, message, true);
}

public sealed record MulticajaProductDto
{
    public int Id { get; init; }
    public string Nombre { get; init; } = string.Empty;
    public decimal Costo { get; init; }
    public decimal Precio { get; init; }
    public int Stock { get; init; }
    public string CodigoBarras { get; init; } = string.Empty;
    public decimal PrecioMayoreo { get; init; }
    public int InvMinimo { get; init; }
    public int InvMaximo { get; init; }
    public string TipoVenta { get; init; } = string.Empty;
    public string Departamento { get; init; } = string.Empty;
}

public sealed record MulticajaUsuarioDto
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public string Rol { get; init; } = string.Empty;
}

public sealed record MulticajaLoginResponse
{
    public bool Ok { get; init; }
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Nombre { get; init; } = string.Empty;
    public string Rol { get; init; } = string.Empty;
    public string? Error { get; init; }
}

public sealed record MulticajaCajaAutoRegistroResponse
{
    public bool Ok { get; init; }
    public Guid CajaId { get; init; }
    public string Nombre { get; init; } = string.Empty;
    public string? Error { get; init; }
}

public sealed record MulticajaCajaSesionDto
{
    public Guid Id { get; init; }
    public bool Abierta { get; init; }
}

public sealed record MulticajaVentaCommitResponse
{
    public bool Ok { get; init; }
    public int NumeroTicket { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaInventoryResponse
{
    public bool Ok { get; init; }
    public int StockNuevo { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaCashMovementResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaCloseResponse
{
    public bool Ok { get; init; }
    public decimal Diferencia { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaVoidResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

public sealed record MulticajaRefundResponse
{
    public bool Ok { get; init; }
    public string? Error { get; init; }
}

internal sealed record ApiResult<T>(
    bool Success, T? Value, HttpStatusCode? StatusCode, string? Error, string? RawJson = null);
