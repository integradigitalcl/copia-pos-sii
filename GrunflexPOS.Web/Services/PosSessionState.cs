using System.Globalization;
using GrunflexPOS.Web.Data;

using GrunflexPOS.Web.Services.Licensing;

namespace GrunflexPOS.Web.Services;

public sealed class PosSessionState(LocalPosStore store, HardwareBridgeClient hardware,
    BoletaPdfService boletaPdf, MulticajaClient multicaja,
    WebLicenseState licenseState, LicensingCloudClient licensingCloud, PosEmailService emailService,
    InvoiceEmissionService invoiceEmission, ILogger<PosSessionState> logger) : IDisposable
{
    private readonly List<CartLine> _cart = [];
    private Func<Func<Task>, Task>? _uiDispatcher;
    private CartLine? _selectedLine;
    private CartItem[]? _lastTicketItems;
    private long _lastTicketNumber;
    private decimal _lastTicketTotal;
    private CancellationTokenSource? _saleMessageCancellation;
    private CancellationTokenSource? _scannerCts;
    private Timer? _shiftCutoffTimer;
    private Timer? _licenseMonitorTimer;
    private Timer? _catalogSyncTimer;
    private int _licenseMonitorRunning;
    private int _shiftCutoffCheckRunning;
    private int _scannerSyncRunning;
    private int _catalogSyncRunning;
    private int _saleInProgress;
    private int _catalogUiDeferred;
    private IReadOnlyList<PosProduct>? _pendingCatalogProducts;
    private readonly Dictionary<int, decimal> _stockByProductId = new();
    private long _lastStockUiNotifyTicks;
    private CancellationTokenSource? _stockUiNotifyCts;
    private const int CatalogSyncIntervalSeconds = 5;

    public event Action? Changed;
    public bool IsLoading { get; private set; } = true;
    public string? StartupError { get; private set; }
    public bool IsAuthenticated { get; private set; }
    public string UserName { get; private set; } = string.Empty;
    public string Role { get; private set; } = string.Empty;
    public string Permissions { get; private set; } = string.Empty;
    public int? CurrentUserId { get; private set; }
    public string CashRegister { get; private set; } = "Caja 1";
    public bool UseInventory { get; private set; } = true;
    public bool OfferCredit { get; private set; } = true;
    public bool AllowCommonProduct { get; private set; } = true;
    public bool EnforceCashMinimum { get; private set; } = true;
    public bool DrawerEnabled { get; private set; }
    public bool WholesaleMode { get; private set; }
    public CartLine? SelectedLine => _selectedLine;
    public bool IsClosingCashDialogOpen { get; private set; }
    public string ClosingAmountText { get; private set; } = string.Empty;
    public string CorteMode { get; private set; } = "Ajuste";
    public string CorteMessage { get; private set; } = string.Empty;
    public bool CanAccessVentas => PosPermissions.CanAccessVentas(Permissions, Role);
    public bool CanAccessProducts => PosPermissions.CanAccessProducts(Permissions, Role);
    public bool CanAccessInventory => PosPermissions.CanAccessInventory(Permissions, Role);
    public bool CanAccessReports => PosPermissions.CanAccessReports(Permissions, Role);
    public bool CanCancelSales => PosPermissions.CanCancelSales(Permissions, Role);
    public bool CanApplyDiscount => PosPermissions.CanApplyDiscount(Permissions, Role);
    public bool CanManageConfiguration => PosPermissions.CanManageConfiguration(Permissions, Role);
    public bool CanManageUsers => PosPermissions.CanManageUsers(Permissions, Role);
    public string CashStatus { get; private set; } = "Cerrada";
    public decimal CashBalance { get; private set; }
    public long? OpenCashSessionId { get; private set; }
    public string ShiftStartTimeText { get; private set; } = "00:00";
    public string ShiftEndTimeText { get; private set; } = "23:59";
    public string ShiftScheduleNotice { get; private set; } = string.Empty;
    public bool IsOpeningCashDialogOpen { get; private set; }
    public string OpeningAmountText { get; private set; } = "0";
    public DateTime LastSaleAt { get; private set; } = DateTime.Now.AddMinutes(-12);
    public string? LastSaleMessage { get; private set; }
    public string? ErrorMessage { get; private set; }
    public string HardwareStatus { get; private set; } = "No verificado";
    public string ScannerStatus { get; private set; } = "Desconectado";
    public string ScaleStatus { get; private set; } = "Desactivada";
    public bool ScaleEnabled { get; private set; }
    public bool MulticajaConfigured { get; private set; }
    public bool MulticajaConnected { get; private set; }
    public bool MulticajaEnabled => MulticajaConfigured;
    public string MulticajaStatus { get; private set; } = "Desactivada";
    public string MulticajaConnectionLabel =>
        !MulticajaConfigured
            ? "Multicaja off"
            : MulticajaConnected
                ? multicaja.PendingOfflineCount > 0
                    ? $"Central · {multicaja.PendingOfflineCount} pend."
                    : "Central conectada"
                : "Central sin conexión";
    public string MulticajaConnectionCss =>
        !MulticajaConfigured
            ? "is-muted"
            : MulticajaConnected
                ? multicaja.PendingOfflineCount > 0 ? "is-warning" : "is-online"
                : "is-offline";
    public bool IsCheckoutOpen { get; private set; }
    public string SelectedPaymentMethod { get; private set; } = "Efectivo";
    public string ReceivedAmountText { get; private set; } = string.Empty;
    public string MixedCashText { get; private set; } = string.Empty;
    public string MixedCardText { get; private set; } = string.Empty;
    public bool CheckoutPrintTicket { get; private set; } = true;
    public bool InvoiceEnabled { get; private set; }
    public string InvoiceDocumentType { get; private set; } = "boleta";
    public bool CheckoutEmitInvoice { get; private set; } = true;
    public string CheckoutBuyerRut { get; private set; } = string.Empty;
    public string CheckoutBuyerName { get; private set; } = string.Empty;
    public string CheckoutBuyerEmail { get; private set; } = string.Empty;
    public string InvoiceStatus { get; private set; } = string.Empty;
    public decimal ReceivedAmount => ParseMoney(ReceivedAmountText);
    public decimal MixedReceivedAmount => ParseMoney(MixedCashText) + ParseMoney(MixedCardText);
    public decimal Change => Math.Max(0, (SelectedPaymentMethod == "Mixto" ? MixedReceivedAmount : ReceivedAmount) - Total);
    public IReadOnlyList<PosProduct> Products { get; private set; } = [];
    public IReadOnlyList<RecentSale> RecentSales { get; private set; } = [];
    public PosDashboard Dashboard { get; private set; } = new(0, 0, 0, 0);
    public IReadOnlyList<CartLine> Cart => _cart;
    public decimal Subtotal => _cart.Sum(line => line.Subtotal);
    public decimal DiscountAmount => _cart.Sum(line => line.ListSubtotal - line.Subtotal);
    public decimal Total => Subtotal;
    public int ItemCount => (int)_cart.Sum(line => line.Quantity);

    public void BindUiDispatcher(Func<Func<Task>, Task> dispatcher) => _uiDispatcher = dispatcher;

    private Task RunOnUiAsync(Func<Task> action) =>
        _uiDispatcher is null
            ? SafeUiActionAsync(action)
            : _uiDispatcher(() => SafeUiActionAsync(action));

    private static async Task SafeUiActionAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception)
        {
            // Evita que errores en callbacks de fondo derriben el circuito Blazor.
        }
    }

    public decimal GetEffectiveStock(PosProduct product) =>
        _stockByProductId.TryGetValue(product.Id, out var stock) ? stock : product.Stock;

    private PosProduct WithLiveStock(PosProduct product)
    {
        var stock = GetEffectiveStock(product);
        return stock == product.Stock ? product : product with { Stock = stock };
    }

    public async Task InitializeAsync()
    {
        try
        {
            StartupError = null;
            await store.EnsureCreatedAsync();
            await licenseState.RefreshAsync();
            ShiftStartTimeText = await store.GetSettingAsync("corte_hora_inicio", "00:00");
            ShiftEndTimeText = await store.GetSettingAsync("corte_hora_cierre", "23:59");
            NormalizeShiftSchedule();
            await ReloadSettingsAsync();
            MulticajaConfigured = await IsMulticajaConfiguredAsync();
            var preparation = await multicaja.PrepareAsync();
            ApplyMulticajaPreparation(preparation);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error al inicializar el POS Web");
            StartupError =
                "No se pudo iniciar el POS. Revise C:\\ProgramData\\GrunflexPOS\\logs\\ " +
                "o reinicie desde el acceso directo de Grunflex POS Web.";
        }
        finally
        {
            IsLoading = false;
            NotifyChanged();
        }
    }

    public async Task RetryStartupAsync()
    {
        IsLoading = true;
        StartupError = null;
        NotifyChanged();
        await InitializeAsync();
    }

    public bool LicenseActivationRequired => licenseState.ActivationRequired;
    public string LicenseBlockReason => licenseState.BlockReason ?? "Licencia requerida.";
    public bool LicenseOnlineSupport => licenseState.OnlineSupport;
    public bool RequiresPasswordChange { get; private set; }

    public async Task<bool> LoginAsync(string userName, string password)
    {
        if (licenseState.ActivationRequired)
        {
            ErrorMessage = LicenseBlockReason;
            NotifyChanged();
            return false;
        }

        if (MulticajaConfigured)
        {
            var central = await multicaja.LoginAsync(userName, password);
            if (!central.Success)
            {
                if (IsMulticajaInfrastructureError(central.Error) && !await IsMulticajaClientAsync())
                {
                    var localFallback = await store.AuthenticateUserAsync(userName, password)
                        ?? await store.TryAuthenticateViaInitialCredentialsAsync(userName, password);
                    if (localFallback is not null)
                        return await CompleteLocalLoginAsync(localFallback);
                }

                ErrorMessage = await IsMulticajaClientAsync()
                    ? await FormatClientApiError(central.Error)
                    : central.Error ?? "Usuario o contraseña incorrectos.";
                MulticajaStatus = ErrorMessage;
                NotifyChanged();
                return false;
            }

            UserName = central.Username;
            Role = central.Role;
            Permissions = "all";
            CurrentUserId = null;
            CentralUserId = central.UserId;
            CentralCajaId = central.CajaId;
            CentralSessionId = central.SessionId == Guid.Empty ? null : central.SessionId;
            RequiresPasswordChange = WebAuthPolicy.HasInitialCredentialsFile();
            IsAuthenticated = true;
            await RefreshAsync();
            if (CentralCajaId is not null && CentralUserId is not null)
            {
                var attach = await multicaja.OpenOrAttachSessionAsync(
                    CentralCajaId.Value, CentralUserId.Value, UserName, 0);
                if (!attach.Success)
                {
                    ErrorMessage = attach.Error ?? "No se pudo abrir la sesión en la caja principal.";
                    MulticajaStatus = ErrorMessage;
                    IsAuthenticated = false;
                    CentralUserId = null;
                    CentralCajaId = null;
                    CentralSessionId = null;
                    NotifyChanged();
                    return false;
                }
                CentralSessionId = attach.SessionId;
            }
            if (CashStatus == "Cerrada")
                BeginOpeningCash();
            StartShiftCutoffTimer();
            StartLicenseMonitor();
            StartCatalogSyncMonitor();
            _ = SyncScannerAsync();
            NotifyChanged();
            return true;
        }

        var user = await store.AuthenticateUserAsync(userName, password)
            ?? await store.TryAuthenticateViaInitialCredentialsAsync(userName, password);
        if (user is null)
        {
            ErrorMessage = "Usuario o contraseña incorrectos.";
            NotifyChanged();
            return false;
        }

        return await CompleteLocalLoginAsync(user);
    }

    private static bool IsMulticajaInfrastructureError(string? error) =>
        !string.IsNullOrWhiteSpace(error) &&
        (error.Contains("no disponible", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("no está registrada", StringComparison.OrdinalIgnoreCase) ||
         error.Contains("no se pudo registrar", StringComparison.OrdinalIgnoreCase));

    private async Task<bool> IsMulticajaClientAsync() =>
        string.Equals(
            await store.GetSettingAsync("terminal_role", "server"),
            "client",
            StringComparison.OrdinalIgnoreCase);

    private async Task<string> FormatClientApiError(string? error)
    {
        var apiUrl = await store.GetSettingAsync("multicaja_api_url", "http://127.0.0.1:7279/");
        return $"No se puede conectar con la caja principal ({apiUrl}). " +
               "Verifique que la API esté activa, el firewall y la IP en Configuración → Cajas.";
    }

    private async Task<bool> EnsureCentralSessionAsync()
    {
        if (!MulticajaEnabled || CentralUserId is null || CentralCajaId is null)
            return false;
        if (CentralSessionId is not null)
            return true;

        var attach = await multicaja.OpenOrAttachSessionAsync(
            CentralCajaId.Value, CentralUserId.Value, UserName, 0);
        if (!attach.Success)
        {
            ErrorMessage = attach.Error ?? "No se pudo abrir la sesión en la caja principal.";
            NotifyChanged();
            return false;
        }

        CentralSessionId = attach.SessionId;
        return true;
    }

    private async Task<bool> CompleteLocalLoginAsync(LocalUserInfo user)
    {
        UserName = user.UserName;
        Role = user.Role;
        Permissions = user.Permissions;
        CurrentUserId = user.Id;
        CentralUserId = null;
        CentralCajaId = null;
        CentralSessionId = null;
        RequiresPasswordChange = user.MustChangePassword || WebAuthPolicy.HasInitialCredentialsFile();
        IsAuthenticated = true;
        await RefreshAsync();
        if (CashStatus == "Cerrada")
            BeginOpeningCash();
        StartShiftCutoffTimer();
        StartLicenseMonitor();
        StartCatalogSyncMonitor();
        _ = SyncScannerAsync();
        NotifyChanged();
        return true;
    }

    public bool Login(string userName, string password) =>
        LoginAsync(userName, password).GetAwaiter().GetResult();

    public void Logout()
    {
        StopLicenseMonitor();
        StopCatalogSyncMonitor();
        StopScannerListener();
        _ = hardware.DisconnectScannerAsync();
        ScannerStatus = "Desconectado";
        IsAuthenticated = false;
        RequiresPasswordChange = false;
        UserName = string.Empty;
        Role = string.Empty;
        Permissions = string.Empty;
        CurrentUserId = null;
        CentralUserId = null;
        CentralCajaId = null;
        CentralSessionId = null;
        WholesaleMode = false;
        _cart.Clear();
        _selectedLine = null;
        IsCheckoutOpen = false;
        IsClosingCashDialogOpen = false;
        IsOpeningCashDialogOpen = false;
        CancelSaleMessageDismissal();
        LastSaleMessage = null;
        NotifyChanged();
    }

    public async Task ReloadSettingsAsync()
    {
        CashRegister = await store.GetSettingAsync("caja_nombre", "Caja 1");
        UseInventory = string.Equals(await store.GetSettingAsync("opt_usar_inventario", "true"), "true",
            StringComparison.OrdinalIgnoreCase);
        OfferCredit = string.Equals(await store.GetSettingAsync("opt_ofrecer_credito", "true"), "true",
            StringComparison.OrdinalIgnoreCase);
        AllowCommonProduct = string.Equals(await store.GetSettingAsync("opt_venta_producto_comun", "true"), "true",
            StringComparison.OrdinalIgnoreCase);
        EnforceCashMinimum = string.Equals(await store.GetSettingAsync("pago_efectivo_no_menor", "true"), "true",
            StringComparison.OrdinalIgnoreCase);
        await store.EnsureDrawerLinkedToPrinterAsync();
        var printer = (await store.GetSettingAsync("impresora_nombre", string.Empty)).Trim();
        var drawerEnabled = await store.GetSettingAsync("cajon_habilitado", string.Empty);
        DrawerEnabled = string.Equals(drawerEnabled, "true", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(drawerEnabled, "false", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(printer));
        await WarmScaleSettingsAsync();
        InvoiceEnabled = string.Equals(await store.GetSettingAsync("facturacion_activa", "false"), "true",
            StringComparison.OrdinalIgnoreCase);
        InvoiceDocumentType = (await store.GetSettingAsync("facturacion_tipo_documento", "boleta")).Trim().ToLowerInvariant();
        if (InvoiceDocumentType is not ("boleta" or "factura"))
            InvoiceDocumentType = "boleta";
        CorteMode = await store.GetSettingAsync("corte_modo", "Ajuste");
        CorteMessage = await store.GetSettingAsync("corte_mensaje", string.Empty);
        NotifyChanged();
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        if (string.IsNullOrWhiteSpace(newPassword) || newPassword.Length < (RequiresPasswordChange ? 8 : 4))
        {
            ShowError(RequiresPasswordChange
                ? "La nueva contraseña debe tener al menos 8 caracteres."
                : "La nueva contraseña debe tener al menos 4 caracteres.");
            return false;
        }

        if (MulticajaEnabled && CentralUserId is Guid centralUserId)
        {
            var ok = await multicaja.UpdateCentralPasswordAsync(
                centralUserId, UserName, Role, UserName, currentPassword, newPassword);
            if (!ok)
            {
                ShowError("La contraseña actual no es correcta o la API rechazó el cambio.");
                return false;
            }
            WebAuthPolicy.ClearInitialCredentialsFiles();
            RequiresPasswordChange = false;
            LastSaleMessage = "Contraseña actualizada correctamente.";
            NotifyChanged();
            return true;
        }

        if (CurrentUserId is null)
        {
            ShowError("La contraseña solo puede cambiarse en usuarios locales.");
            return false;
        }
        var user = await store.AuthenticateUserAsync(UserName, currentPassword);
        if (user is null)
        {
            ShowError("La contraseña actual no es correcta.");
            return false;
        }
        if (!await store.UpdateUserPasswordAsync(CurrentUserId.Value, newPassword))
        {
            ShowError("No se pudo actualizar la contraseña.");
            return false;
        }
        RequiresPasswordChange = false;
        WebAuthPolicy.ClearInitialCredentialsFiles();
        LastSaleMessage = "Contraseña actualizada correctamente.";
        NotifyChanged();
        return true;
    }

    public bool TryGetMulticajaContext(out Guid cajaId, out Guid sessionId, out Guid userId)
    {
        if (CentralUserId is Guid uid && CentralCajaId is Guid cid && CentralSessionId is Guid sid)
        {
            cajaId = cid;
            sessionId = sid;
            userId = uid;
            return true;
        }
        cajaId = sessionId = userId = Guid.Empty;
        return false;
    }

    public bool IsCurrentCashSessionSale(long? cashSessionId) =>
        OpenCashSessionId is long openId && cashSessionId == openId;

    public async Task RefreshAsync()
    {
        if (ShouldDeferCatalogUi())
        {
            Interlocked.Exchange(ref _catalogUiDeferred, 1);
            return;
        }

        if (MulticajaConfigured)
        {
            var preparation = await multicaja.PrepareAsync();
            ApplyMulticajaPreparation(preparation);
            if (MulticajaConnected)
                await multicaja.SyncCatalogAsync();
        }
        var fresh = await store.GetProductsAsync();
        if (_cart.Count > 0)
            ApplyStockSnapshot(fresh, replaceCatalog: false, notifyUi: false);
        else
        {
            Products = fresh;
            UpdateStockMirror(Products);
        }
        Dashboard = await store.GetDashboardAsync();
        RecentSales = await store.GetRecentSalesAsync();
        var session = await store.GetOpenCashSessionAsync();
        if (session is not null && IsShiftCutoffReached(session, DateTime.Now))
        {
            var expectedAmount = session.OpeningAmount + session.TotalSales +
                                 session.TotalEntries - session.TotalExits;
            if (MulticajaEnabled && CentralUserId is not null && CentralCajaId is not null &&
                CentralSessionId is not null)
            {
                var central = await multicaja.CloseCashSessionAsync(
                    CentralCajaId.Value, CentralSessionId.Value, CentralUserId.Value, expectedAmount);
                if (!central.Success)
                {
                    ShiftScheduleNotice = $"No se pudo ejecutar el corte automático: {central.Message}";
                }
                else
                {
                    await store.CloseCashSessionAsync(expectedAmount);
                    CentralSessionId = null;
                    session = null;
                }
            }
            else
            {
                await store.CloseCashSessionAsync(expectedAmount);
                session = null;
            }
        }
        CashStatus = session is null ? "Cerrada" : "Abierta";
        OpenCashSessionId = session?.Id;
        CashBalance = session is null
            ? 0
            : session.OpeningAmount + session.TotalSales + session.TotalEntries - session.TotalExits;
        ErrorMessage = null;
        NotifyChanged();
    }

    public async Task SaveShiftScheduleAsync(string startTime, string endTime)
    {
        if (!TryParseShiftTime(startTime, out var start) || !TryParseShiftTime(endTime, out var end))
        {
            ShiftScheduleNotice = "Selecciona una hora de inicio y una hora de cierre válidas.";
            NotifyChanged();
            return;
        }

        ShiftStartTimeText = FormatShiftTime(start);
        ShiftEndTimeText = FormatShiftTime(end);
        await store.SetSettingAsync("corte_hora_inicio", ShiftStartTimeText);
        await store.SetSettingAsync("corte_hora_cierre", ShiftEndTimeText);
        ShiftScheduleNotice = $"Horario guardado: {ShiftStartTimeText} a {ShiftEndTimeText}.";
        await RefreshAsync();
    }

    public async Task LoadReceiptsAsync(CancellationToken cancellationToken = default)
    {
        RecentSales = await store.GetRecentSalesAsync(100, cancellationToken);
        NotifyChanged();
    }

    public void ClearLastSaleMessage()
    {
        CancelSaleMessageDismissal();
        LastSaleMessage = null;
        NotifyChanged();
    }

    public void ClearError()
    {
        ErrorMessage = null;
        NotifyChanged();
    }

    public void ShowError(string message)
    {
        ErrorMessage = message;
        NotifyChanged();
    }

    public void AddProduct(PosProduct product) =>
        AddProduct(product, quantity: 1m, unitPriceOverride: null);

    public void AddProduct(PosProduct product, decimal quantity, decimal? unitPriceOverride)
    {
        if (quantity <= 0)
        {
            ShowError("La cantidad debe ser mayor a cero.");
            return;
        }

        var unitPrice = unitPriceOverride is > 0 ? unitPriceOverride.Value : GetEffectivePrice(product);
        var merge = unitPriceOverride is null && quantity == 1m;
        var existing = merge
            ? _cart.FirstOrDefault(line => line.Product.Id == product.Id && line.UnitPrice == unitPrice)
            : null;

        if (existing is null)
        {
            var availableStock = GetEffectiveStock(product);
            if (UseInventory && availableStock < quantity)
            {
                ErrorMessage = availableStock <= 0
                    ? $"Producto sin stock: {product.Name}."
                    : $"Stock insuficiente de {product.Name}.";
                NotifyChanged();
                return;
            }

            _cart.Add(new CartLine(WithLiveStock(product), quantity, unitPrice));
            _selectedLine = _cart[^1];
        }
        else if (!UseInventory || existing.Quantity + quantity <= GetEffectiveStock(existing.Product))
        {
            existing.Quantity += quantity;
            _selectedLine = existing;
        }
        else
            ErrorMessage = $"No hay más stock de {product.Name}.";

        CancelSaleMessageDismissal();
        LastSaleMessage = null;
        NotifyChanged();
    }

    public bool TryAddProductByCode(string code)
    {
        code = (code ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(code) || !IsAuthenticated)
            return false;
        if (IsCheckoutOpen || IsClosingCashDialogOpen || IsOpeningCashDialogOpen || RequiresPasswordChange)
            return false;

        if (TryAddScaleLabel(code))
            return true;

        var product = Products.FirstOrDefault(x => x.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
        if (product is null)
        {
            ShowError($"Código no encontrado: {code}");
            return false;
        }

        AddProduct(WithLiveStock(product));
        return true;
    }

    private bool TryAddScaleLabel(string code)
    {
        if (!ScaleEnabled || _scalePrefix is null)
            return false;

        if (!ScaleLabelBarcodeParser.TryParse(
                code, _scalePrefix, _scalePriceLabels, _scaleWeightLabels, out var parsed) || parsed is null)
            return false;

        PosProduct? product = null;
        foreach (var candidate in ScaleLabelBarcodeParser.LookupCandidates(parsed, _scalePrefix))
        {
            product = Products.FirstOrDefault(x => x.Code.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                ?? Products.FirstOrDefault(x => x.Code.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (product is not null)
                break;
        }

        if (product is null)
        {
            ShowError($"Etiqueta de báscula: producto no encontrado ({parsed.ProductLookup}).");
            return true;
        }

        AddProduct(WithLiveStock(product), parsed.QuantityKg ?? 1m, parsed.EmbeddedUnitPrice);
        LastSaleMessage = parsed.Mode == "peso"
            ? $"{product.Name}: {parsed.QuantityKg:0.###} kg"
            : $"{product.Name}: precio etiqueta {FormatCurrency(parsed.EmbeddedUnitPrice ?? 0)}";
        NotifyChanged();
        return true;
    }

    public bool TryAddScaleLabelBarcode(string code) => TryAddScaleLabel(code);

    private string? _scalePrefix;
    private bool _scalePriceLabels;
    private bool _scaleWeightLabels;
    private string _scalePort = string.Empty;
    private string _scaleDriver = "GENERICA";

    public async Task<HardwareResult> ReadScaleIntoSelectedLineAsync()
    {
        if (!ScaleEnabled)
            return HardwareResult.Unavailable("Báscula desactivada en configuración.");
        if (_selectedLine is null)
            return HardwareResult.Unavailable("Seleccione un producto en el carrito.");
        if (string.IsNullOrWhiteSpace(_scalePort))
            await WarmScaleSettingsAsync();
        if (string.IsNullOrWhiteSpace(_scalePort))
            return HardwareResult.Unavailable("Configure el puerto COM de la báscula.");

        var read = await hardware.ReadScaleAsync(_scalePort, _scaleDriver);
        if (!read.Success)
        {
            ScaleStatus = read.Message;
            NotifyChanged();
            return HardwareResult.Unavailable(read.Message);
        }

        if (UseInventory && read.Kilograms > _selectedLine.Product.Stock)
        {
            ScaleStatus = "Peso mayor al stock disponible.";
            NotifyChanged();
            return HardwareResult.Unavailable(ScaleStatus);
        }

        _selectedLine.Quantity = Math.Round(read.Kilograms, 3, MidpointRounding.AwayFromZero);
        ScaleStatus = read.Message;
        LastSaleMessage = $"{_selectedLine.Product.Name}: {_selectedLine.Quantity:0.###} kg";
        NotifyChanged();
        return HardwareResult.Ok(read.Message);
    }

    public async Task<(bool Ok, string Message, decimal Kg)> TestScaleAsync()
    {
        await WarmScaleSettingsAsync();
        if (string.IsNullOrWhiteSpace(_scalePort))
            return (false, "Indique el puerto COM.", 0);

        var read = await hardware.ReadScaleAsync(_scalePort, _scaleDriver);
        ScaleStatus = read.Message;
        NotifyChanged();
        return (read.Success, read.Message, read.Kilograms);
    }

    private async Task WarmScaleSettingsAsync()
    {
        var activa = string.Equals(await store.GetSettingAsync("bascula_activa", string.Empty), "true",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(await store.GetSettingAsync("bascula_habilitada", "false"), "true",
                StringComparison.OrdinalIgnoreCase);
        ScaleEnabled = activa;
        _scalePrefix = await store.GetSettingAsync("bascula_codigo_inicial", "2000");
        if (string.IsNullOrWhiteSpace(_scalePrefix))
            _scalePrefix = "2000";
        _scalePriceLabels = string.Equals(await store.GetSettingAsync("bascula_etiqueta_precio", "false"), "true",
            StringComparison.OrdinalIgnoreCase);
        _scaleWeightLabels = string.Equals(await store.GetSettingAsync("bascula_etiqueta_peso", "false"), "true",
            StringComparison.OrdinalIgnoreCase)
            || string.Equals(await store.GetSettingAsync("bascula_etiquetas", "false"), "true",
                StringComparison.OrdinalIgnoreCase);
        _scalePort = (await store.GetSettingAsync("bascula_puerto", string.Empty)).Trim();
        if (string.IsNullOrWhiteSpace(_scalePort))
        {
            var driverPort = await store.GetSettingAsync("bascula_driver_puerto", string.Empty);
            var match = System.Text.RegularExpressions.Regex.Match(driverPort, @"COM\d+",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (match.Success)
                _scalePort = match.Value.ToUpperInvariant();
        }
        _scaleDriver = await store.GetSettingAsync("bascula_driver", "GENERICA");
        if (string.IsNullOrWhiteSpace(_scaleDriver))
            _scaleDriver = "GENERICA";
        ScaleStatus = ScaleEnabled ? "Lista" : "Desactivada";
    }

    public async Task SyncScannerAsync()
    {
        if (Interlocked.Exchange(ref _scannerSyncRunning, 1) == 1)
            return;

        try
        {
            StopScannerListener();

            var enabled = string.Equals(
                await store.GetSettingAsync("lector_serial_habilitado", "false"),
                "true",
                StringComparison.OrdinalIgnoreCase);
            if (!enabled)
            {
                await hardware.DisconnectScannerAsync();
                ScannerStatus = "Desactivado";
                NotifyChanged();
                return;
            }

            var port = (await store.GetSettingAsync("lector_serial_puerto")).Trim();
            if (string.IsNullOrWhiteSpace(port))
            {
                ScannerStatus = "Sin puerto COM";
                NotifyChanged();
                return;
            }

            var baud = ParseIntSetting(await store.GetSettingAsync("lector_serial_baud", "9600"), 9600);
            var dataBits = ParseIntSetting(
                await FirstNonEmptySettingAsync("lector_serial_databits", "lector_serial_data_bits", "8"),
                8);
            var parity = await FirstNonEmptySettingAsync("lector_serial_parity", "lector_serial_paridad", "None");
            var stopBits = await FirstNonEmptySettingAsync("lector_serial_stopbits", "lector_serial_stop_bits", "One");
            var handshake = await store.GetSettingAsync("lector_serial_handshake", "None");

            var result = await hardware.ConnectScannerAsync(port, baud, dataBits, parity, stopBits, handshake);
            ScannerStatus = result.Message;
            HardwareStatus = result.Success ? "Bridge conectado" : result.Message;
            if (result.Success)
                StartScannerListener();
            NotifyChanged();
        }
        finally
        {
            Interlocked.Exchange(ref _scannerSyncRunning, 0);
        }
    }

    public async Task<HardwareResult> TestScannerConnectionAsync(
        string port, int baudRate, int dataBits, string parity, string stopBits, string handshake)
    {
        var result = await hardware.ConnectScannerAsync(port, baudRate, dataBits, parity, stopBits, handshake);
        ScannerStatus = result.Message;
        if (result.Success)
            StartScannerListener();
        else
            StopScannerListener();
        NotifyChanged();
        return result;
    }

    private void StartScannerListener()
    {
        StopScannerListener();
        var cts = new CancellationTokenSource();
        _scannerCts = cts;
        _ = ListenScannerAsync(cts.Token);
    }

    private void StopScannerListener()
    {
        try { _scannerCts?.Cancel(); } catch { /* ignore */ }
        _scannerCts?.Dispose();
        _scannerCts = null;
    }

    private async Task ListenScannerAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var scannerEvent in hardware.SubscribeScannerEventsAsync(cancellationToken))
            {
                if (string.Equals(scannerEvent.Type, "code", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(scannerEvent.Code))
                {
                    await RunOnUiAsync(() =>
                    {
                        TryAddProductByCode(scannerEvent.Code);
                        return Task.CompletedTask;
                    });
                    continue;
                }

                if (string.Equals(scannerEvent.Type, "connected", StringComparison.OrdinalIgnoreCase))
                {
                    await RunOnUiAsync(() =>
                    {
                        ScannerStatus = $"Conectado ({scannerEvent.Port})";
                        NotifyChanged();
                        return Task.CompletedTask;
                    });
                }
                else if (string.Equals(scannerEvent.Type, "disconnected", StringComparison.OrdinalIgnoreCase))
                {
                    await RunOnUiAsync(() =>
                    {
                        ScannerStatus = "Desconectado";
                        NotifyChanged();
                        return Task.CompletedTask;
                    });
                }
                else if (string.Equals(scannerEvent.Type, "error", StringComparison.OrdinalIgnoreCase))
                {
                    await RunOnUiAsync(() =>
                    {
                        ScannerStatus = string.IsNullOrWhiteSpace(scannerEvent.Error)
                            ? "Error de lector"
                            : scannerEvent.Error;
                        NotifyChanged();
                        return Task.CompletedTask;
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            // listener stopped
        }
        catch (Exception ex)
        {
            await RunOnUiAsync(() =>
            {
                ScannerStatus = "SSE: " + ex.GetBaseException().Message;
                NotifyChanged();
                return Task.CompletedTask;
            });
        }
    }

    private async Task<string> FirstNonEmptySettingAsync(string primary, string fallback, string defaultValue)
    {
        var value = (await store.GetSettingAsync(primary, string.Empty)).Trim();
        if (!string.IsNullOrWhiteSpace(value))
            return value;
        value = (await store.GetSettingAsync(fallback, string.Empty)).Trim();
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value;
    }

    private static int ParseIntSetting(string? raw, int fallback) =>
        int.TryParse((raw ?? string.Empty).Trim(), out var value) && value > 0 ? value : fallback;

    public void AddCommonProduct(string name, decimal price)
    {
        if (!AllowCommonProduct)
        {
            ShowError("La venta de producto común está desactivada en configuración.");
            return;
        }
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name) || price <= 0)
        {
            ShowError("Indica nombre y precio válidos para el producto común.");
            return;
        }
        var product = new PosProduct(-1, "COMUN", name, "Común", price, 9999m, "un.", "#6366F1");
        _cart.Add(new CartLine(product, 1, price));
        _selectedLine = _cart[^1];
        CancelSaleMessageDismissal();
        LastSaleMessage = null;
        NotifyChanged();
    }

    public void ToggleWholesale()
    {
        WholesaleMode = !WholesaleMode;
        foreach (var line in _cart.Where(line => line.Product.Id > 0))
            line.UpdateUnitPrice(GetEffectivePrice(line.Product));
        LastSaleMessage = WholesaleMode ? "Modo mayoreo activado." : "Modo mayoreo desactivado.";
        NotifyChanged();
    }

    public void SelectLine(CartLine line)
    {
        _selectedLine = line;
        NotifyChanged();
    }

    public void ChangeQuantity(CartLine line, int amount)
    {
        _selectedLine = line;
        var next = line.Quantity + amount;
        if (UseInventory && line.Product.Id > 0 && next > GetEffectiveStock(line.Product))
        {
            ErrorMessage = $"Stock insuficiente para {line.Product.Name}.";
            NotifyChanged();
            return;
        }
        line.Quantity = next;
        if (line.Quantity <= 0)
        {
            _cart.Remove(line);
            _selectedLine = _cart.LastOrDefault();
            if (_cart.Count == 0)
                ScheduleStockUiRefresh();
        }
        NotifyChanged();
    }

    public void RemoveLine(CartLine line)
    {
        _cart.Remove(line);
        if (ReferenceEquals(_selectedLine, line))
            _selectedLine = _cart.LastOrDefault();
        if (_cart.Count == 0)
            ScheduleStockUiRefresh();
        NotifyChanged();
    }

    public void DeleteSelectedLine()
    {
        if (_selectedLine is not null)
            RemoveLine(_selectedLine);
    }

    public void ChangeSelectedQuantity(int amount)
    {
        if (_selectedLine is not null)
            ChangeQuantity(_selectedLine, amount);
    }

    public void ClearCart()
    {
        _cart.Clear();
        _selectedLine = null;
        ScheduleStockUiRefresh();
        NotifyChanged();
    }

    public void OpenCheckout()
    {
        if (_cart.Count == 0)
        {
            ShowError("Agrega al menos un producto para cobrar.");
            return;
        }
        IsCheckoutOpen = true;
        ReceivedAmountText = string.Empty;
        MixedCashText = string.Empty;
        MixedCardText = string.Empty;
        SelectedPaymentMethod = "Efectivo";
        CheckoutPrintTicket = true;
        CheckoutEmitInvoice = InvoiceEnabled;
        CheckoutBuyerRut = string.Empty;
        CheckoutBuyerName = string.Empty;
        CheckoutBuyerEmail = string.Empty;
        NotifyChanged();
    }

    public void CloseCheckout()
    {
        IsCheckoutOpen = false;
        NotifyChanged();
    }

    public void SelectPaymentMethod(string method)
    {
        SelectedPaymentMethod = method;
        NotifyChanged();
    }

    public void SetCheckoutEmitInvoice(bool value)
    {
        CheckoutEmitInvoice = value;
        NotifyChanged();
    }

    public void SetCheckoutBuyerRut(string value)
    {
        CheckoutBuyerRut = value;
        NotifyChanged();
    }

    public void SetCheckoutBuyerName(string value)
    {
        CheckoutBuyerName = value;
        NotifyChanged();
    }

    public void SetCheckoutBuyerEmail(string value)
    {
        CheckoutBuyerEmail = value;
        NotifyChanged();
    }

    public void SetReceivedAmount(string value)
    {
        ReceivedAmountText = value;
        NotifyChanged();
    }

    public void SetMixedCash(string value)
    {
        MixedCashText = value;
        MixedCardText = ComputeMixedCompanionAmount(value);
        NotifyChanged();
    }

    public void SetMixedCard(string value)
    {
        MixedCardText = value;
        MixedCashText = ComputeMixedCompanionAmount(value);
        NotifyChanged();
    }

    private string ComputeMixedCompanionAmount(string primaryValue)
    {
        if (string.IsNullOrWhiteSpace(primaryValue))
            return string.Empty;

        var remaining = Math.Max(0, Total - ParseMoney(primaryValue));
        return remaining == 0 ? string.Empty : remaining.ToString("0.##", CultureInfo.InvariantCulture);
    }

    public async Task<bool> CompleteSaleAsync(string? paymentMethod = null, bool? printTicket = null,
        bool personalConsumption = false)
    {
        try
        {
            return await CompleteSaleCoreAsync(paymentMethod, printTicket, personalConsumption);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"No se pudo completar la venta: {ex.GetBaseException().Message}";
            NotifyChanged();
            return false;
        }
    }

    private async Task<bool> CompleteSaleCoreAsync(string? paymentMethod = null, bool? printTicket = null,
        bool personalConsumption = false)
    {
        Interlocked.Increment(ref _saleInProgress);
        try
        {
            return await CompleteSaleCoreInnerAsync(paymentMethod, printTicket, personalConsumption);
        }
        finally
        {
            Interlocked.Decrement(ref _saleInProgress);
            _ = FlushDeferredCatalogUiAsync();
        }
    }

    private async Task<bool> CompleteSaleCoreInnerAsync(string? paymentMethod = null, bool? printTicket = null,
        bool personalConsumption = false)
    {
        paymentMethod ??= SelectedPaymentMethod;
        if (paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase) && !OfferCredit)
        {
            ShowError("El crédito no está habilitado en configuración.");
            return false;
        }

        var cashPortion = 0m;
        var received = paymentMethod == "Mixto" ? MixedReceivedAmount : ReceivedAmount;
        if (!personalConsumption && paymentMethod == "Efectivo" && string.IsNullOrWhiteSpace(ReceivedAmountText))
            received = Total;
        if (!personalConsumption && paymentMethod == "Mixto")
        {
            cashPortion = ParseMoney(MixedCashText);
            var cardPortion = ParseMoney(MixedCardText);
            if (cashPortion + cardPortion <= 0)
            {
                cashPortion = Total;
                received = Total;
            }
            else
            {
                received = cashPortion + cardPortion;
                // Tarjeta/mixto: solo registro contable en reportes (sin pasarela).
            }
        }
        else if (!personalConsumption && paymentMethod.Equals("Crédito", StringComparison.OrdinalIgnoreCase))
        {
            received = 0;
            cashPortion = 0;
        }
        else if (!personalConsumption && paymentMethod == "Efectivo")
        {
            cashPortion = received;
        }

        if (!personalConsumption && EnforceCashMinimum && paymentMethod == "Efectivo" && received < Total)
        {
            ErrorMessage = "El efectivo recibido es menor al total.";
            NotifyChanged();
            return false;
        }
        if (!personalConsumption && paymentMethod == "Mixto" && received < Total)
        {
            ErrorMessage = "La suma de efectivo y tarjeta es menor al total.";
            NotifyChanged();
            return false;
        }

        if (!personalConsumption && paymentMethod.Equals("Tarjeta", StringComparison.OrdinalIgnoreCase))
        {
            // Solo registra el método en la venta/reportes; no llama a pasarela.
            received = Total;
            cashPortion = 0;
        }

        var ticketItems = _cart.Select(x => new CartItem(x.Product, x.Quantity, x.DiscountPercentage, x.UnitPrice)).ToArray();
        long? centralTicket = null;
        if (MulticajaConfigured)
        {
            await EnsureCartStockFreshAsync();
            if (!await EnsureCentralSessionAsync())
            {
                if (string.IsNullOrWhiteSpace(ErrorMessage))
                    ErrorMessage = "La sesión multicaja no está lista. Abra caja o verifique la conexión con la caja principal.";
                NotifyChanged();
                return false;
            }
            var centralResult = await multicaja.CommitSaleAsync(
                CentralCajaId.Value, CentralSessionId.Value, CentralUserId.Value,
                _cart.Select(x => new CartItem(x.Product, x.Quantity, x.DiscountPercentage, x.UnitPrice)).ToArray(),
                paymentMethod, personalConsumption);
            if (!centralResult.Success)
            {
                ErrorMessage = centralResult.Message;
                NotifyChanged();
                return false;
            }
            if (!centralResult.Queued && long.TryParse(centralResult.Message, out var parsedTicket))
                centralTicket = parsedTicket;
        }
        var session = await store.GetOpenCashSessionAsync();
        var result = await store.RecordSaleAsync(
            _cart.Select(x => new CartItem(x.Product, x.Quantity, x.DiscountPercentage, x.UnitPrice)).ToArray(),
            UserName, personalConsumption ? "Consumo personal" : paymentMethod, received,
            personalConsumption, printTicket ?? CheckoutPrintTicket, session?.Id,
            ticketNumberOverride: centralTicket, cashSessionAmount: cashPortion,
            enforceInventory: UseInventory);
        if (!result.Success)
        {
            if (centralTicket is > 0 && TryGetMulticajaContext(out var cajaId, out var sessionId, out var userId))
                await multicaja.VoidSaleAsync(cajaId, sessionId, userId, (int)centralTicket.Value);
            ErrorMessage = result.Message;
            NotifyChanged();
            return false;
        }

        IsCheckoutOpen = false;
        LastSaleAt = DateTime.Now;
        var queuedSuffix = MulticajaEnabled && multicaja.PendingOfflineCount > 0
            ? $" · {multicaja.PendingOfflineCount} operación(es) pendiente(s) de sync"
            : string.Empty;
        LastSaleMessage = $"Ticket #{result.TicketNumber} · {result.Message} · " +
                          $"{(personalConsumption ? "Consumo personal" : paymentMethod)} · {FormatCurrency(result.Total)}" +
                          (result.Change > 0 ? $" · Cambio {FormatCurrency(result.Change)}" : string.Empty) +
                          queuedSuffix;
        _lastTicketItems = ticketItems;
        _lastTicketNumber = result.TicketNumber;
        _lastTicketTotal = result.Total;
        if (!personalConsumption && (printTicket ?? CheckoutPrintTicket))
        {
            var printer = await hardware.GetConfiguredPrinterNameAsync();
            if (string.IsNullOrWhiteSpace(printer))
            {
                try
                {
                    var boletaPath = boletaPdf.Generate(result.TicketNumber, ticketItems, result.Total);
                    HardwareStatus = "Sin impresora configurada";
                    LastSaleMessage += $" · Boleta PDF guardada en {boletaPath}";
                }
                catch (Exception ex)
                {
                    LastSaleMessage += $" · Boleta PDF no generada: {ex.GetBaseException().Message}";
                }
            }
            else
            {
                try
                {
                    var printResult = await hardware.PrintTicketAsync(result.TicketNumber, ticketItems, result.Total);
                    HardwareStatus = printResult.Message;
                    if (!printResult.Success)
                    {
                        var boletaPath = boletaPdf.Generate(result.TicketNumber, ticketItems, result.Total);
                        LastSaleMessage += $" · {printResult.Message}. PDF guardado en {boletaPath}";
                    }
                }
                catch (Exception ex)
                {
                    HardwareStatus = $"Impresión no disponible: {ex.GetBaseException().Message}";
                    try
                    {
                        var boletaPath = boletaPdf.Generate(result.TicketNumber, ticketItems, result.Total);
                        LastSaleMessage += $" · PDF guardado en {boletaPath}";
                    }
                    catch (Exception pdfEx)
                    {
                        LastSaleMessage += $" · Boleta PDF no generada: {pdfEx.GetBaseException().Message}";
                    }
                }
            }
        }
        if (!personalConsumption && DrawerEnabled && (paymentMethod == "Efectivo" || paymentMethod == "Mixto" && cashPortion > 0))
            await hardware.OpenDrawerAsync();
        if (!personalConsumption)
            _ = emailService.TryNotifySaleAsync(result.TicketNumber, ticketItems, result.Total, UserName, paymentMethod);
        if (!personalConsumption && InvoiceEnabled && CheckoutEmitInvoice)
        {
            InvoiceEmissionService.BuyerInfo? buyer = null;
            if (InvoiceDocumentType == "factura" ||
                !string.IsNullOrWhiteSpace(CheckoutBuyerRut) ||
                !string.IsNullOrWhiteSpace(CheckoutBuyerName))
            {
                buyer = new InvoiceEmissionService.BuyerInfo(
                    CheckoutBuyerRut.Trim(), CheckoutBuyerName.Trim(), CheckoutBuyerEmail.Trim());
            }

            var docType = !string.IsNullOrWhiteSpace(CheckoutBuyerRut) && InvoiceDocumentType == "boleta"
                && !string.IsNullOrWhiteSpace(CheckoutBuyerName)
                ? "factura"
                : InvoiceDocumentType;

            try
            {
                var emit = await invoiceEmission.EmitSaleAsync(
                    result.TicketNumber, ticketItems, result.Total, paymentMethod, UserName,
                    documentTypeOverride: docType, buyer: buyer);
                InvoiceStatus = emit.Message;
                LastSaleMessage += emit.Ok
                    ? $" · Facturación OK: {emit.Message}"
                    : $" · Facturación: {emit.Message}";
            }
            catch (Exception ex)
            {
                InvoiceStatus = ex.GetBaseException().Message;
                LastSaleMessage += $" · Facturación: {InvoiceStatus}";
            }
        }
        _cart.Clear();
        _selectedLine = null;
        if (MulticajaConfigured && MulticajaConnected)
            await multicaja.SyncCatalogAsync();
        await RefreshAfterSaleAsync();
        await FlushDeferredCatalogUiAsync();
        ScheduleSaleMessageDismissal();
        NotifyChanged();
        return true;
    }

    private async Task EnsureCartStockFreshAsync()
    {
        if (_cart.Count == 0)
            return;

        if (MulticajaConfigured && MulticajaConnected)
            await multicaja.SyncCatalogAsync();

        var fresh = await store.GetProductsAsync();
        ApplyStockSnapshot(fresh, replaceCatalog: false, notifyUi: false);
    }

    private async Task RefreshAfterSaleAsync()
    {
        var fresh = await store.GetProductsAsync();
        ApplyStockSnapshot(fresh, replaceCatalog: true, notifyUi: false);
        Dashboard = await store.GetDashboardAsync();
        RecentSales = await store.GetRecentSalesAsync();
        var session = await store.GetOpenCashSessionAsync();
        CashStatus = session is null ? "Cerrada" : "Abierta";
        OpenCashSessionId = session?.Id;
        CashBalance = session is null
            ? 0
            : session.OpeningAmount + session.TotalSales + session.TotalEntries - session.TotalExits;
    }

    public async Task ReprintLastTicketAsync()
    {
        if (_lastTicketItems is null)
        {
            ShowError("No hay un ticket anterior disponible para reimprimir.");
            return;
        }

        var printer = await hardware.GetConfiguredPrinterNameAsync();
        if (string.IsNullOrWhiteSpace(printer))
        {
            var boletaPath = boletaPdf.Generate(_lastTicketNumber, _lastTicketItems, _lastTicketTotal);
            LastSaleMessage = $"Sin impresora configurada. PDF guardado en {boletaPath}";
            NotifyChanged();
            return;
        }

        var result = await hardware.PrintTicketAsync(_lastTicketNumber, _lastTicketItems, _lastTicketTotal);
        HardwareStatus = result.Message;
        if (!result.Success)
        {
            var boletaPath = boletaPdf.Generate(_lastTicketNumber, _lastTicketItems, _lastTicketTotal);
            ShowError($"{result.Message}. PDF guardado en {boletaPath}");
        }
        else
        {
            LastSaleMessage = $"Ticket #{_lastTicketNumber} reenviado a la impresora.";
        }

        NotifyChanged();
    }

    public bool TryGetLastTicket(out long ticketNumber, out IReadOnlyList<CartItem> items, out decimal total)
    {
        if (_lastTicketItems is null)
        {
            ticketNumber = 0;
            items = Array.Empty<CartItem>();
            total = 0;
            return false;
        }

        ticketNumber = _lastTicketNumber;
        items = _lastTicketItems;
        total = _lastTicketTotal;
        return true;
    }

    public async Task ToggleCashRegisterAsync()
    {
        if (CashStatus == "Abierta")
        {
            BeginClosingCash();
            return;
        }
        BeginOpeningCash();
    }

    public void BeginClosingCash()
    {
        ClosingAmountText = CashBalance.ToString("0.##", CultureInfo.InvariantCulture);
        IsClosingCashDialogOpen = true;
        if (!string.IsNullOrWhiteSpace(CorteMessage))
            ShiftScheduleNotice = CorteMessage;
        NotifyChanged();
    }

    public void CancelClosingCash()
    {
        IsClosingCashDialogOpen = false;
        NotifyChanged();
    }

    public void SetClosingAmount(string value)
    {
        ClosingAmountText = value;
        NotifyChanged();
    }

    public async Task<bool> ConfirmClosingCashAsync()
    {
        var counted = ParseMoney(ClosingAmountText);
        if (counted < 0)
        {
            ShowError("El monto contado debe ser válido.");
            return false;
        }
        if (MulticajaEnabled && CentralUserId is not null && CentralCajaId is not null && CentralSessionId is not null)
        {
            var central = await multicaja.CloseCashSessionAsync(
                CentralCajaId.Value, CentralSessionId.Value, CentralUserId.Value, counted);
            if (!central.Success)
            {
                ShowError(central.Message);
                return false;
            }
            LastSaleMessage = central.Message;
        }
        if (await store.CloseCashSessionAsync(counted))
        {
            IsClosingCashDialogOpen = false;
            CentralSessionId = null;
            await RefreshAsync();
            return true;
        }
        ShowError("No hay una caja abierta para cerrar.");
        return false;
    }

    private decimal GetEffectivePrice(PosProduct product) =>
        WholesaleMode && product.WholesalePrice > 0 ? product.WholesalePrice : product.Price;

    public void BeginOpeningCash()
    {
        OpeningAmountText = "0";
        IsOpeningCashDialogOpen = true;
        NotifyChanged();
    }

    public void CancelOpeningCash()
    {
        IsOpeningCashDialogOpen = false;
        NotifyChanged();
    }

    public void SetOpeningAmount(string value)
    {
        OpeningAmountText = value;
        NotifyChanged();
    }

    public async Task<bool> RegisterOpeningCashAsync()
    {
        var raw = OpeningAmountText.Trim().Replace("$", string.Empty).Replace(" ", string.Empty);
        raw = raw.Contains(',')
            ? raw.Replace(".", string.Empty).Replace(',', '.')
            : raw.Count(character => character == '.') > 1
                ? raw.Replace(".", string.Empty)
                : raw;
        if (!decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var openingAmount) ||
            openingAmount < 0)
        {
            ShowError("El efectivo inicial debe ser un monto válido igual o mayor a $0.");
            return false;
        }

        if (MulticajaEnabled && CentralUserId is not null && CentralCajaId is not null)
        {
            var central = await multicaja.OpenOrAttachSessionAsync(
                CentralCajaId.Value, CentralUserId.Value, UserName, openingAmount);
            if (!central.Success)
            {
                ShowError(central.Error);
                return false;
            }
            CentralSessionId = central.SessionId;
        }

        await store.OpenCashSessionAsync(UserName, openingAmount);
        IsOpeningCashDialogOpen = false;
        await RefreshAsync();
        return true;
    }

    public void ToggleCashRegister() => ToggleCashRegisterAsync().GetAwaiter().GetResult();

    public void ApplyGlobalDiscount(decimal percentage)
    {
        percentage = Math.Clamp(percentage, 0, 100);
        foreach (var line in _cart)
            line.DiscountPercentage = percentage;
        NotifyChanged();
    }

    public async Task<bool> RegisterCashMovementAsync(string type, decimal amount, string description)
    {
        if (MulticajaEnabled && CentralUserId is not null && CentralCajaId is not null && CentralSessionId is not null)
        {
            var central = await multicaja.RegisterCashMovementAsync(
                CentralCajaId.Value, CentralSessionId.Value, CentralUserId.Value, type, amount, description);
            if (!central.Success)
            {
                ShowError(central.Message);
                return false;
            }
            if (central.Queued)
                LastSaleMessage = central.Message;
        }
        var ok = await store.RegisterCashMovementAsync(UserName, type, amount, description);
        if (!ok)
            ShowError("No hay una caja abierta o el monto no es válido.");
        else
            await RefreshAsync();
        return ok;
    }

    public async Task AdjustStockAsync(PosProduct product, decimal delta)
    {
        if (MulticajaEnabled && CentralUserId is not null && CentralCajaId is not null && CentralSessionId is not null)
        {
            var central = await multicaja.AdjustInventoryAsync(
                CentralCajaId.Value, CentralSessionId.Value, CentralUserId.Value,
                product.CentralProductId ?? product.Id, (int)delta, "Ajuste desde POS web");
            if (!central.Success)
            {
                ShowError(central.Message);
                return;
            }
            if (central.Queued)
                LastSaleMessage = central.Message;
        }
        if (await store.AdjustStockAsync(product.Id, delta, UserName, "Ajuste manual desde POS web"))
            await RefreshAsync();
    }

    public string FormatCurrency(decimal value) =>
        value.ToString("C0", CultureInfo.GetCultureInfo("es-CL"));

    private void NormalizeShiftSchedule()
    {
        if (TryParseShiftTime(ShiftStartTimeText, out var start))
            ShiftStartTimeText = FormatShiftTime(start);
        else
            ShiftStartTimeText = "00:00";

        if (TryParseShiftTime(ShiftEndTimeText, out var end))
            ShiftEndTimeText = FormatShiftTime(end);
        else
            ShiftEndTimeText = "23:59";
    }

    private bool IsShiftCutoffReached(LocalCashSession session, DateTime now)
    {
        if (!TryParseShiftTime(ShiftStartTimeText, out var startTime) ||
            !TryParseShiftTime(ShiftEndTimeText, out var endTime))
            return false;

        var openedLocal = session.OpenedAtUtc.ToLocalTime();
        var start = openedLocal.Date + startTime.ToTimeSpan();
        var end = openedLocal.Date + endTime.ToTimeSpan();
        if (end <= start)
            end = end.AddDays(1);
        if (openedLocal >= end)
            end = end.AddDays(1);
        return now >= end;
    }

    private static bool TryParseShiftTime(string? raw, out TimeOnly time)
    {
        time = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        raw = raw.Trim();
        string[] formats = ["HH:mm", "H:mm", "HH:mm:ss", "H:mm:ss"];
        if (TimeOnly.TryParseExact(raw, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time))
            return true;

        return TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
               || TimeOnly.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out time);
    }

    private static string FormatShiftTime(TimeOnly time) =>
        time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private bool ShouldBlockUiRefresh() =>
        Interlocked.CompareExchange(ref _saleInProgress, 0, 0) > 0
        || IsCheckoutOpen
        || IsClosingCashDialogOpen
        || IsOpeningCashDialogOpen
        || _cart.Count > 0;

    private bool ShouldDeferCatalogUi() => ShouldBlockUiRefresh();

    private void NotifyChanged()
    {
        void Fire()
        {
            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Notificación UI omitida.");
            }
        }

        if (_uiDispatcher is null)
        {
            Fire();
            return;
        }

        _ = _uiDispatcher(() =>
        {
            Fire();
            return Task.CompletedTask;
        });
    }

    private void RefreshCartProductSnapshots(IReadOnlyList<PosProduct> products)
    {
        if (_cart.Count == 0)
            return;

        try
        {
            var byCode = BuildProductLookup(products);
            foreach (var line in _cart)
            {
                if (byCode.TryGetValue(line.Product.Code, out var updated))
                    line.RefreshProduct(updated);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "No se pudo actualizar stock del carrito.");
        }
    }

    private static Dictionary<string, PosProduct> BuildProductLookup(IReadOnlyList<PosProduct> products)
    {
        var byCode = new Dictionary<string, PosProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var product in products)
        {
            if (string.IsNullOrWhiteSpace(product.Code))
                continue;
            byCode[product.Code] = product;
        }

        return byCode;
    }

    private void UpdateStockMirror(IReadOnlyList<PosProduct> products)
    {
        foreach (var product in products)
            _stockByProductId[product.Id] = product.Stock;
    }

    private bool TryPatchCatalogStocks(IReadOnlyList<PosProduct> fresh)
    {
        if (Products.Count == 0)
            return false;

        var freshById = new Dictionary<int, PosProduct>();
        foreach (var product in fresh)
            freshById[product.Id] = product;
        var patched = new PosProduct[Products.Count];
        var changed = false;
        for (var i = 0; i < Products.Count; i++)
        {
            var current = Products[i];
            if (freshById.TryGetValue(current.Id, out var updated) && updated.Stock != current.Stock)
            {
                patched[i] = current with { Stock = updated.Stock };
                changed = true;
            }
            else
            {
                patched[i] = current;
            }
        }

        if (!changed)
            return false;

        Products = patched;
        return true;
    }

    private void ApplyStockSnapshot(IReadOnlyList<PosProduct> fresh, bool replaceCatalog, bool notifyUi)
    {
        UpdateStockMirror(fresh);
        RefreshCartProductSnapshots(fresh);

        if (replaceCatalog)
        {
            Products = fresh;
        }
        else if (_cart.Count == 0)
        {
            TryPatchCatalogStocks(fresh);
        }

        if (notifyUi && !ShouldBlockUiRefresh())
            ScheduleStockUiRefresh();
    }

    private void ScheduleStockUiRefresh()
    {
        CancelStockUiRefresh();
        var cts = new CancellationTokenSource();
        _stockUiNotifyCts = cts;
        _ = DebouncedStockUiRefreshAsync(cts);
    }

    private async Task DebouncedStockUiRefreshAsync(CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(350, cts.Token);
            await RunOnUiAsync(() =>
            {
                if (ShouldBlockUiRefresh())
                    return Task.CompletedTask;

                var now = Environment.TickCount64;
                if (now - _lastStockUiNotifyTicks < 300)
                    return Task.CompletedTask;

                _lastStockUiNotifyTicks = now;
                NotifyChanged();
                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // Reemplazado por una notificación más reciente.
        }
        finally
        {
            if (ReferenceEquals(_stockUiNotifyCts, cts))
                _stockUiNotifyCts = null;

            try
            {
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void StartShiftCutoffTimer()
    {
        _shiftCutoffTimer ??= new Timer(
            _ => _ = CheckAutomaticCutoffAsync(),
            null,
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(1));
    }

    private async Task CheckAutomaticCutoffAsync()
    {
        if (!IsAuthenticated || Interlocked.Exchange(ref _shiftCutoffCheckRunning, 1) == 1)
            return;

        try
        {
            await RunOnUiAsync(async () =>
            {
                try
                {
                    await RefreshAsync();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Corte automático omitido.");
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _shiftCutoffCheckRunning, 0);
        }
    }

    private void StartLicenseMonitor()
    {
        _licenseMonitorTimer ??= new Timer(
            _ => _ = CheckLicenseDuringSessionAsync(),
            null,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(15));
    }

    private void StopLicenseMonitor()
    {
        _licenseMonitorTimer?.Dispose();
        _licenseMonitorTimer = null;
    }

    private async Task CheckLicenseDuringSessionAsync()
    {
        if (!IsAuthenticated || Interlocked.Exchange(ref _licenseMonitorRunning, 1) == 1)
            return;

        try
        {
            await RunOnUiAsync(async () =>
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(licenseState.ActivationId))
                        await licensingCloud.RefreshAsync(silent: true);

                    await licenseState.RefreshAsync();
                    if (!licenseState.IsPosAccessAllowed)
                    {
                        ErrorMessage = licenseState.BlockReason ?? "La licencia dejó de ser válida.";
                        Logout();
                        return;
                    }

                    MulticajaConfigured = await IsMulticajaConfiguredAsync();
                    if (MulticajaConfigured)
                    {
                        var preparation = await multicaja.PrepareAsync();
                        ApplyMulticajaPreparation(preparation);
                    }
                    else
                    {
                        MulticajaConnected = false;
                        MulticajaStatus = "Multicaja desactivada";
                    }

                    NotifyChanged();
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Monitoreo de licencia omitido.");
                }
            });
        }
        finally
        {
            Interlocked.Exchange(ref _licenseMonitorRunning, 0);
        }
    }

    public void Dispose()
    {
        StopLicenseMonitor();
        StopCatalogSyncMonitor();
        StopScannerListener();
        _shiftCutoffTimer?.Dispose();
        CancelSaleMessageDismissal();
        CancelStockUiRefresh();
    }

    private Guid? CentralUserId { get; set; }
    private Guid? CentralCajaId { get; set; }
    private Guid? CentralSessionId { get; set; }

    private async Task<bool> IsMulticajaConfiguredAsync() =>
        string.Equals(
            await store.GetSettingAsync("multicaja_habilitada", "false"),
            "true",
            StringComparison.OrdinalIgnoreCase) &&
        (!licenseState.RequireLicense || licenseState.Multicaja);

    private void ApplyMulticajaPreparation(MulticajaPreparation preparation)
    {
        MulticajaConnected = preparation.Connected;
        if (!MulticajaConfigured)
        {
            MulticajaStatus = "Multicaja desactivada";
            return;
        }

        if (!preparation.Connected)
        {
            MulticajaStatus = preparation.Status;
            return;
        }

        var pending = multicaja.PendingOfflineCount;
        MulticajaStatus = pending > 0
            ? $"{preparation.Status} · Cola: {pending}"
            : preparation.Status;
    }

    private void StartCatalogSyncMonitor()
    {
        StopCatalogSyncMonitor();
        if (!MulticajaConfigured)
            return;

        _catalogSyncTimer = new Timer(async _ => await RunCatalogSyncAsync(), null,
            TimeSpan.FromSeconds(CatalogSyncIntervalSeconds),
            TimeSpan.FromSeconds(CatalogSyncIntervalSeconds));
    }

    private void StopCatalogSyncMonitor()
    {
        _catalogSyncTimer?.Dispose();
        _catalogSyncTimer = null;
    }

    private async Task RunCatalogSyncAsync()
    {
        if (!IsAuthenticated || !MulticajaConfigured || Interlocked.Exchange(ref _catalogSyncRunning, 1) == 1)
            return;

        try
        {
            var syncOk = await multicaja.SyncCatalogAsync();
            IReadOnlyList<PosProduct>? products = null;
            if (syncOk)
                products = await store.GetProductsAsync();

            await RunOnUiAsync(() => ApplyCatalogSyncResult(syncOk, products));
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Sincronización de catálogo omitida.");
        }
        finally
        {
            Interlocked.Exchange(ref _catalogSyncRunning, 0);
        }
    }

    private Task ApplyCatalogSyncResult(bool syncOk, IReadOnlyList<PosProduct>? products)
    {
        if (!syncOk || products is null)
        {
            if (ShouldDeferCatalogUi())
            {
                Interlocked.Exchange(ref _catalogUiDeferred, 1);
                return Task.CompletedTask;
            }

            MulticajaConnected = false;
            MulticajaStatus = "API central no disponible.";
            ScheduleStockUiRefresh();
            return Task.CompletedTask;
        }

        if (ShouldDeferCatalogUi())
        {
            Interlocked.Exchange(ref _catalogUiDeferred, 1);
            _pendingCatalogProducts = products;
            ApplyStockSnapshot(products, replaceCatalog: false, notifyUi: false);
            return Task.CompletedTask;
        }

        try
        {
            ApplyCatalogSyncResultCore(syncOk, products);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Actualización de catálogo omitida.");
        }

        return Task.CompletedTask;
    }

    private void ApplyCatalogSyncResultCore(bool syncOk, IReadOnlyList<PosProduct>? products)
    {
        if (!syncOk || products is null)
        {
            MulticajaConnected = false;
            MulticajaStatus = "API central no disponible.";
            ScheduleStockUiRefresh();
            return;
        }

        MulticajaConnected = true;
        var pending = multicaja.PendingOfflineCount;
        MulticajaStatus = pending > 0
            ? $"Conectada a la API central · Cola: {pending}"
            : "Conectada a la API central";

        var replaceCatalog = _cart.Count == 0;
        ApplyStockSnapshot(products, replaceCatalog, notifyUi: true);
    }

    private Task FlushDeferredCatalogUiAsync()
    {
        if (Interlocked.CompareExchange(ref _catalogUiDeferred, 0, 0) != 1)
            return Task.CompletedTask;

        return RunOnUiAsync(async () =>
        {
            if (ShouldBlockUiRefresh())
                return;

            if (Interlocked.Exchange(ref _catalogUiDeferred, 0) != 1)
                return;

            try
            {
                var products = _pendingCatalogProducts ?? await store.GetProductsAsync();
                _pendingCatalogProducts = null;
                ApplyCatalogSyncResultCore(true, products);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Catálogo diferido omitido.");
            }
        });
    }

    private void CancelSaleMessageDismissal()
    {
        var cts = _saleMessageCancellation;
        _saleMessageCancellation = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private void CancelStockUiRefresh()
    {
        var cts = _stockUiNotifyCts;
        _stockUiNotifyCts = null;
        if (cts is null)
            return;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cts.Dispose();
        }
    }

    private static decimal ParseMoney(string value) =>
        decimal.TryParse(value?.Replace("$", "").Replace(".", "").Replace(",", "."),
            NumberStyles.Any, CultureInfo.InvariantCulture, out var amount) ? amount : 0;

    private void ScheduleSaleMessageDismissal()
    {
        CancelSaleMessageDismissal();
        var cancellation = new CancellationTokenSource();
        _saleMessageCancellation = cancellation;
        _ = DismissSaleMessageAsync(cancellation);
    }

    private async Task DismissSaleMessageAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(10), cancellation.Token);
            await RunOnUiAsync(() =>
            {
                if (ReferenceEquals(_saleMessageCancellation, cancellation))
                {
                    LastSaleMessage = null;
                    NotifyChanged();
                }

                return Task.CompletedTask;
            });
        }
        catch (OperationCanceledException)
        {
            // A new sale or manual dismissal replaced this message.
        }
        finally
        {
            if (ReferenceEquals(_saleMessageCancellation, cancellation))
                _saleMessageCancellation = null;

            try
            {
                cancellation.Dispose();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }
}

public sealed class CartLine
{
    public PosProduct Product { get; private set; }
    public decimal UnitPrice { get; private set; }
    public decimal Quantity { get; set; }
    public decimal DiscountPercentage { get; set; }

    public CartLine(PosProduct product, decimal quantity, decimal unitPrice)
    {
        Product = product;
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public void UpdateUnitPrice(decimal unitPrice) => UnitPrice = unitPrice;
    public void RefreshProduct(PosProduct product) => Product = product;

    public decimal ListSubtotal => Math.Round(UnitPrice * Quantity, 2, MidpointRounding.AwayFromZero);
    public decimal Subtotal => Math.Round(ListSubtotal * (1m - DiscountPercentage / 100m), 2, MidpointRounding.AwayFromZero);
}
