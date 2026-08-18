using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.IO;
using System.Net;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text;
using Grunflex.LicenseIssuer.Dialogs;
using Grunflex.LicenseIssuer.Persistence;
using Microsoft.Win32;

namespace Grunflex.LicenseIssuer.ViewModels;

public sealed partial class MainViewModel
{
    public string ApiBaseUrl
    {
        get => _apiBaseUrl;
        set => SetProperty(ref _apiBaseUrl, value);
    }

    public int DashboardActiveLicenses
    {
        get => _dashboardActiveLicenses;
        private set => SetProperty(ref _dashboardActiveLicenses, value);
    }

    public int DashboardExpiredLicenses
    {
        get => _dashboardExpiredLicenses;
        private set => SetProperty(ref _dashboardExpiredLicenses, value);
    }

    public int DashboardActivations
    {
        get => _dashboardActivations;
        private set => SetProperty(ref _dashboardActivations, value);
    }

    public int DashboardClients
    {
        get => _dashboardClients;
        private set => SetProperty(ref _dashboardClients, value);
    }

    public string LicenseStatusFilter
    {
        get => _licenseStatusFilter;
        set
        {
            if (!SetProperty(ref _licenseStatusFilter, value)) return;
            LicensePage = 1;
            _ = LoadLicensesFromServerAsync();
        }
    }

    public string ActivationStatusFilter
    {
        get => _activationStatusFilter;
        set
        {
            if (!SetProperty(ref _activationStatusFilter, value)) return;
            _ = LoadActivationsFromServerAsync();
        }
    }

    private string _activationStatusFilter = "Todos";

    public int LicensePage
    {
        get => _licensePage;
        private set
        {
            var v = Math.Max(1, value);
            if (!SetProperty(ref _licensePage, v)) return;
            RefreshPagingProps();
        }
    }

    public int TotalLicensePages =>
        Math.Max(1, (int)Math.Ceiling(_licenseTotalCount / (double)_licensePageSize));

    public string LicensesPagingSummary =>
        _licenseTotalCount == 0
            ? "Sin registros"
            : $"Mostrando {(LicensePage - 1) * _licensePageSize + 1} a {Math.Min(LicensePage * _licensePageSize, _licenseTotalCount)} de {_licenseTotalCount}";

    public bool CanPrevLicensePage => LicensePage > 1;

    public bool CanNextLicensePage => LicensePage < TotalLicensePages;

    public int ReportTypeIndex
    {
        get => _reportTypeIndex;
        set => SetProperty(ref _reportTypeIndex, value);
    }

    public int ReportPeriodMonthsBack
    {
        get => _reportPeriodMonthsBack;
        set => SetProperty(ref _reportPeriodMonthsBack, value);
    }

    public ObservableCollection<string> DashboardAuditLines { get; } = new();

    public ObservableCollection<DashboardPlanSlice> PlanDistribution { get; } = new();

    public ObservableCollection<DashboardAlertItem> DashboardAlerts { get; } = new();

    private string _dashboardPeriodLabel = "";

    private string _dashboardSparklinePoints = "20,130 470,130";

    private string _dashboardSparkAxisText = "";

    private string _dashboardKpiActiveCaption = "";

    private string _dashboardKpiExpiredCaption = "";

    private string _dashboardKpiActivationCaption = "";

    private string _dashboardKpiClientsCaption = "";

    public string DashboardPeriodLabel
    {
        get => _dashboardPeriodLabel;
        set => SetProperty(ref _dashboardPeriodLabel, value);
    }

    public string DashboardSparklinePoints
    {
        get => _dashboardSparklinePoints;
        set => SetProperty(ref _dashboardSparklinePoints, value);
    }

    public string DashboardSparkAxisText
    {
        get => _dashboardSparkAxisText;
        set => SetProperty(ref _dashboardSparkAxisText, value);
    }

    public string DashboardKpiActiveCaption
    {
        get => _dashboardKpiActiveCaption;
        set => SetProperty(ref _dashboardKpiActiveCaption, value);
    }

    public string DashboardKpiExpiredCaption
    {
        get => _dashboardKpiExpiredCaption;
        set => SetProperty(ref _dashboardKpiExpiredCaption, value);
    }

    public string DashboardKpiActivationCaption
    {
        get => _dashboardKpiActivationCaption;
        set => SetProperty(ref _dashboardKpiActivationCaption, value);
    }

    public string DashboardKpiClientsCaption
    {
        get => _dashboardKpiClientsCaption;
        set => SetProperty(ref _dashboardKpiClientsCaption, value);
    }

    public string[] LicenseFilterOptions { get; } = { "Todas", "Activas", "Vencidas", "Próximas a vencer" };

    public string[] ActivationStatusFilterOptions { get; } = { "Todos", "Activa", "Suspendida", "Revocada" };

    private IEnumerable<string> EnumerateApiBaseUrls()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in new[]
                 {
                     Environment.GetEnvironmentVariable("GRUNFLEX_LICENSE_API_URL"),
                     ApiBaseUrl,
                     "http://127.0.0.1:7279",
                     "http://localhost:7279",
                     "https://127.0.0.1:7279",
                     "https://localhost:7279",
                     "http://localhost:5000"
                 })
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            var t = raw.Trim().TrimEnd('/');
            if (seen.Add(t))
                yield return t;
        }
    }

    private static string? MapLicenseStatusApi(string filter) =>
        filter switch
        {
            "Activas" => "active",
            "Vencidas" => "expired",
            "Próximas a vencer" => "expiring",
            _ => null
        };

    private void RefreshPagingProps()
    {
        OnPropertyChanged(nameof(TotalLicensePages));
        OnPropertyChanged(nameof(LicensesPagingSummary));
        OnPropertyChanged(nameof(CanPrevLicensePage));
        OnPropertyChanged(nameof(CanNextLicensePage));
    }

    private void PrevLicensePage()
    {
        if (_licensePage <= 1) return;
        LicensePage = _licensePage - 1;
        _ = LoadLicensesFromServerAsync();
    }

    private void NextLicensePage()
    {
        if (_licensePage >= TotalLicensePages) return;
        LicensePage = _licensePage + 1;
        _ = LoadLicensesFromServerAsync();
    }

    public async Task LoadLicensesFromServerAsync(CancellationToken cancellationToken = default)
    {
        var q = string.IsNullOrWhiteSpace(LicenseSearch) ? string.Empty : LicenseSearch.Trim();
        var st = MapLicenseStatusApi(LicenseStatusFilter);

        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(12));
                ApplyIssuerHeaders(client);
                var endpoint =
                    $"/api/LicenseIssuer?page={LicensePage}&pageSize={_licensePageSize}";
                if (!string.IsNullOrWhiteSpace(q))
                    endpoint += $"&q={Uri.EscapeDataString(q)}";
                if (!string.IsNullOrWhiteSpace(st))
                    endpoint += $"&status={Uri.EscapeDataString(st)}";

                using var response = await client.GetAsync(endpoint, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                var payload = await response.Content.ReadFromJsonAsync<ApiPagedResult<ApiLicenseIssuerRecord>>(cancellationToken: cancellationToken);
                if (payload?.Items == null)
                    continue;

                Licenses.Clear();
                _licenseTotalCount = payload.Total;
                var now = DateTime.UtcNow;
                foreach (var item in payload.Items)
                {
                    var status = "Activa";
                    if (item.ExpUtc <= now)
                        status = "Vencida";
                    else if (item.ExpUtc <= now.AddDays(7))
                        status = "Próxima a vencer";

                    var clientLabel = string.IsNullOrWhiteSpace(item.BusinessName)
                        ? item.CustomerName
                        : $"{item.CustomerName} · {item.BusinessName}";

                    Licenses.Add(new LicenseListItem(
                        item.Id,
                        item.ActivationId,
                        clientLabel,
                        string.IsNullOrWhiteSpace(item.LicenseType) ? "Suscripción" : item.LicenseType,
                        item.NumberOfBoxes,
                        status,
                        item.ExpUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                        item.CreatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                        item.LicenseToken));
                }

                LicensesSubtitle = $"Servidor: {baseUrl} · {payload.Total} registros (pág. {LicensePage}/{TotalLicensePages}).";
                OnPropertyChanged(nameof(LicensesSubtitle));
                RefreshPagingProps();
                return;
            }
            catch
            {
                // siguiente URL
            }
        }

        LicensesSubtitle = "Sin conexión al servidor; datos de demostración.";
        OnPropertyChanged(nameof(LicensesSubtitle));
        Licenses.Clear();
        _licenseTotalCount = 0;
        SeedLicenses();
        RefreshPagingProps();
    }

    public async Task LoadClientsFromServerAsync(CancellationToken cancellationToken = default)
    {
        var q = string.IsNullOrWhiteSpace(ClientSearch) ? string.Empty : ClientSearch.Trim();

        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(12));
                ApplyIssuerHeaders(client);
                var path = "/api/IssuerClients";
                if (!string.IsNullOrWhiteSpace(q))
                    path += $"?q={Uri.EscapeDataString(q)}";

                using var response = await client.GetAsync(path, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                var items = await response.Content.ReadFromJsonAsync<List<ApiIssuerClientRecord>>(cancellationToken: cancellationToken);
                if (items == null)
                    continue;

                Clients.Clear();
                foreach (var x in items)
                {
                    Clients.Add(new ClientListItem(
                        x.Id,
                        x.ClientCode,
                        x.Name,
                        x.Business,
                        x.Email,
                        x.Phone,
                        x.LinkedLicensesCount));
                }

                return;
            }
            catch
            {
                //
            }
        }

        Clients.Clear();
        SeedClients();
    }

    public async Task LoadActivationsFromServerAsync(CancellationToken cancellationToken = default)
    {
        var q = string.IsNullOrWhiteSpace(ActivationSearch) ? string.Empty : ActivationSearch.Trim();
        var st = ActivationStatusFilter;

        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(12));
                ApplyIssuerHeaders(client);
                var path = "/api/IssuerActivations";
                var qs = new List<string>();
                if (!string.IsNullOrWhiteSpace(q))
                    qs.Add($"q={Uri.EscapeDataString(q)}");
                if (!string.IsNullOrWhiteSpace(st) && !string.Equals(st, "Todos", StringComparison.OrdinalIgnoreCase))
                    qs.Add($"status={Uri.EscapeDataString(st)}");
                if (qs.Count > 0)
                    path += "?" + string.Join("&", qs);

                using var response = await client.GetAsync(path, cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                var items = await response.Content.ReadFromJsonAsync<List<ApiIssuerActivationRecord>>(cancellationToken: cancellationToken);
                if (items == null)
                    continue;

                Activations.Clear();
                foreach (var x in items)
                {
                    Activations.Add(new ActivationListItem(
                        x.Id,
                        x.ActivationId,
                        x.ActivationId,
                        x.CustomerDisplay,
                        x.DeviceName,
                        x.HardwareId,
                        x.ActivatedAtUtc.ToLocalTime().ToString("dd/MM/yyyy", CultureInfo.InvariantCulture),
                        x.LastSeenUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                        x.Status));
                }

                return;
            }
            catch
            {
                //
            }
        }

        Activations.Clear();
        SeedActivations();
    }

    public async Task RefreshDashboardStatsAsync(CancellationToken cancellationToken = default)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(12));
                ApplyIssuerHeaders(client);
                using var response = await client.GetAsync("/api/LicenseIssuer/stats", cancellationToken);
                if (!response.IsSuccessStatusCode)
                    continue;

                var s = await response.Content.ReadFromJsonAsync<LicenseIssuerStatsDto>(cancellationToken: cancellationToken);
                if (s == null)
                    continue;

                DashboardActiveLicenses = s.ActiveLicenses;
                DashboardExpiredLicenses = s.ExpiredLicenses;
                DashboardActivations = s.ActivationsTotal;
                DashboardClients = s.ClientsTotal;
                ApplyDashboardDetailsFromStats(s);
                RefreshAuditDashboard();
                return;
            }
            catch
            {
                //
            }
        }

        DashboardActiveLicenses = Licenses.Count(x => x.Status == "Activa");
        DashboardExpiredLicenses = Licenses.Count(x => x.Status == "Vencida");
        DashboardActivations = Activations.Count;
        DashboardClients = Clients.Count;
        ApplyDashboardOfflineDefaults();
        RefreshAuditDashboard();
    }

    private void ApplyDashboardDetailsFromStats(LicenseIssuerStatsDto s)
    {
        var loc = DateTime.Now;
        var start = new DateTime(loc.Year, loc.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        DashboardPeriodLabel = $"{start:dd/MM/yyyy} — {end:dd/MM/yyyy}";

        var nt = s.NewLicensesThisCalendarMonthUtc;
        var np = s.NewLicensesPreviousCalendarMonthUtc;
        string trendNt;
        if (np == 0 && nt == 0)
            trendNt = "Sin altas en estos meses";
        else if (np == 0)
            trendNt = "Primera actividad registrada este mes";
        else
            trendNt = $"{100.0 * (nt - np) / np:+0.0;-0.0;0}% vs mes anterior";

        DashboardKpiActiveCaption = $"Altas por fecha de creación: {nt} este mes · {np} mes anterior ({trendNt}).";

        DashboardKpiExpiredCaption = "Total histórico de licencias con fecha de expiración vencida.";
        DashboardKpiActivationCaption =
            s.PendingDeviceActivations > 0
                ? $"{s.PendingDeviceActivations} activación(es) con dispositivo «Pendiente» · {s.ActivationsTotal} filas en servidor"
                : $"{s.ActivationsTotal} registros de activación · sin pendientes de equipo";

        DashboardKpiClientsCaption = $"{s.ClientsTotal} clientes en catálogo";

        var vals = s.NewLicensesByMonthLast6 ?? new List<int>();
        DashboardSparklinePoints = BuildSparkPolylinePoints(vals);
        DashboardSparkAxisText = s.SparkMonthLabels != null && s.SparkMonthLabels.Count > 0
            ? string.Join("    ", s.SparkMonthLabels)
            : string.Empty;

        PlanDistribution.Clear();
        var act = Math.Max(0, s.ActiveLicenses);
        void AddSlice(string label, int count)
        {
            var pct = act > 0 ? Math.Round(100.0 * count / act, 1) : 0;
            PlanDistribution.Add(new DashboardPlanSlice(label, pct, $"{count} ({pct:0.#}%)"));
        }

        AddSlice("Básico", s.PlanBasico);
        AddSlice("Medium", s.PlanMedium);
        AddSlice("Plus", s.PlanPlus);
        AddSlice("Otros", s.PlanOtro);

        DashboardAlerts.Clear();
        DashboardAlerts.Add(new DashboardAlertItem(
            $"●  {s.ExpiredLicenses} licencias vencidas",
            s.ExpiredLicenses > 0 ? DashboardAlertSeverity.Danger : DashboardAlertSeverity.Neutral));
        DashboardAlerts.Add(new DashboardAlertItem(
            $"●  {s.ExpiringWithin7Days} por vencer en los próximos 7 días",
            s.ExpiringWithin7Days > 0 ? DashboardAlertSeverity.Warning : DashboardAlertSeverity.Neutral));
        if (s.PendingDeviceActivations > 0)
            DashboardAlerts.Add(new DashboardAlertItem(
                $"●  {s.PendingDeviceActivations} activación(es) sin equipo asignado (Pendiente)",
                DashboardAlertSeverity.Info));
    }

    private void ApplyDashboardOfflineDefaults()
    {
        var loc = DateTime.Now;
        var start = new DateTime(loc.Year, loc.Month, 1);
        var end = start.AddMonths(1).AddDays(-1);
        DashboardPeriodLabel = $"{start:dd/MM/yyyy} — {end:dd/MM/yyyy}";
        DashboardSparklinePoints = BuildSparkPolylinePoints(Array.Empty<int>());
        DashboardSparkAxisText = string.Empty;
        DashboardKpiActiveCaption = "Conecte la API para tendencias de altas.";
        DashboardKpiExpiredCaption = "Conecte la API para totales exactos.";
        DashboardKpiActivationCaption = "Sin datos del servidor.";
        DashboardKpiClientsCaption = "Sin datos del servidor.";
        PlanDistribution.Clear();
        DashboardAlerts.Clear();
        DashboardAlerts.Add(new DashboardAlertItem(
            "●  Sin conexión a la API — alertas no disponibles.",
            DashboardAlertSeverity.Neutral));
    }

    private static string BuildSparkPolylinePoints(IReadOnlyList<int> values)
    {
        if (values == null || values.Count == 0)
            return "20,130 470,130";

        var max = values.Max();
        if (max == 0)
            return "20,130 470,130";

        var n = values.Count;
        var pts = new List<string>();
        for (var i = 0; i < n; i++)
        {
            var x = n <= 1 ? 245 : 20 + i * (450.0 / (n - 1));
            var ratio = values[i] / (double)max;
            var y = 130 - ratio * 100;
            pts.Add($"{x:F0},{y:F0}");
        }

        return string.Join(" ", pts);
    }

    private void RefreshAuditDashboard()
    {
        DashboardAuditLines.Clear();
        foreach (var line in IssuerAuditLog.ReadTail(8))
            DashboardAuditLines.Add(line);
    }

    private void DeleteLicenseRow(LicenseListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty)
            return;

        if (MessageBox.Show(
                $"¿Eliminar la licencia {item.LicenseId} del servidor?",
                "Confirmar",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        _ = DeleteLicenseAsync(item);
    }

    private async Task DeleteLicenseAsync(LicenseListItem item)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.DeleteAsync($"/api/LicenseIssuer/{item.RecordId}");
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Licencia eliminada {item.LicenseId}");
                if (EnableAuditLog)
                    IssuerAuditLog.Append($"AUDIT delete license {item.RecordId}");

                await LoadLicensesFromServerAsync();
                await RefreshDashboardStatsAsync();
                MessageBox.Show("Licencia eliminada.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch
            {
                //
            }
        }

        MessageBox.Show("No se pudo eliminar (¿API detenida?).", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private static void CopyLicenseRow(LicenseListItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.FullLicenseToken))
            return;
        Clipboard.SetText(item.FullLicenseToken);
        MessageBox.Show("Token copiado al portapapeles.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void ViewLicenseTokenRow(LicenseListItem? item)
    {
        if (item == null) return;
        var preview = item.FullLicenseToken.Length > 900
            ? item.FullLicenseToken[..900] + "…"
            : item.FullLicenseToken;
        MessageBox.Show(preview, $"Licencia {item.LicenseId}", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void CopyClientEmailRow(ClientListItem? item)
    {
        if (item == null || string.IsNullOrWhiteSpace(item.Email)) return;
        Clipboard.SetText(item.Email);
    }

    private void DeleteClientRow(ClientListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty) return;
        if (MessageBox.Show($"¿Eliminar cliente {item.ClientId}?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
            MessageBoxResult.Yes)
            return;

        _ = DeleteClientAsync(item);
    }

    private async Task DeleteClientAsync(ClientListItem item)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.DeleteAsync($"/api/IssuerClients/{item.RecordId}");
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Cliente eliminado {item.ClientId}");
                await LoadClientsFromServerAsync();
                await RefreshDashboardStatsAsync();
                return;
            }
            catch
            {
                //
            }
        }

        MessageBox.Show("No se pudo eliminar el cliente.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditClientRow(ClientListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty) return;

        var dlg = new EditClientDialog(
            "Editar cliente",
            item.Name,
            item.Business,
            item.Email,
            item.Phone)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dlg.ShowDialog() != true)
            return;

        _ = UpdateClientAsync(item.RecordId, dlg.NameResult, dlg.BusinessResult, dlg.EmailResult, dlg.PhoneResult);
    }

    public async Task<bool> TryCreateClientAsync(string name, string business, string email, string phone)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.PostAsJsonAsync(
                    "/api/IssuerClients",
                    new IssuerClientUpsertDto { Name = name, Business = business, Email = email, Phone = phone });
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Cliente creado {name}");
                await LoadClientsFromServerAsync();
                await RefreshDashboardStatsAsync();
                return true;
            }
            catch
            {
                //
            }
        }

        return false;
    }

    private async Task UpdateClientAsync(Guid id, string name, string business, string email, string phone)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.PutAsJsonAsync(
                    $"/api/IssuerClients/{id}",
                    new IssuerClientUpsertDto { Name = name, Business = business, Email = email, Phone = phone });
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Cliente actualizado {name}");
                await LoadClientsFromServerAsync();
                await RefreshDashboardStatsAsync();
                MessageBox.Show("Cliente guardado.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch
            {
                //
            }
        }

        MessageBox.Show("No se pudo guardar el cliente.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void DeleteActivationRow(ActivationListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty) return;
        if (MessageBox.Show("¿Eliminar este registro de activación?", "Confirmar", MessageBoxButton.YesNo, MessageBoxImage.Warning) !=
            MessageBoxResult.Yes)
            return;

        _ = DeleteActivationAsync(item);
    }

    private async Task DeleteActivationAsync(ActivationListItem item)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.DeleteAsync($"/api/IssuerActivations/{item.RecordId}");
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Activación eliminada {item.ActivationCode}");
                await LoadActivationsFromServerAsync();
                await RefreshDashboardStatsAsync();
                return;
            }
            catch
            {
                //
            }
        }

        MessageBox.Show("No se pudo eliminar.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void EditActivationRow(ActivationListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty) return;

        var owner = Application.Current?.MainWindow;
        var dDlg = new PromptDialog("Dispositivo", "Nombre del dispositivo / caja:", item.Device)
        {
            Owner = owner
        };
        if (dDlg.ShowDialog() != true) return;

        var hDlg = new PromptDialog("Hardware", "ID hardware (opcional):", item.HardwareId) { Owner = owner };
        if (hDlg.ShowDialog() != true) return;

        var sDlg = new PromptDialog("Estado", "Activa, Suspendida o Revocada:", item.Status) { Owner = owner };
        if (sDlg.ShowDialog() != true) return;

        _ = PutActivationAsync(item.RecordId, item.ActivationCode, item.Client, dDlg.Result ?? item.Device, hDlg.Result ?? "-",
            sDlg.Result ?? "Activa");
    }

    private async Task PutActivationAsync(
        Guid id,
        string activationId,
        string customerDisplay,
        string device,
        string hardware,
        string status)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.PutAsJsonAsync(
                    $"/api/IssuerActivations/{id}",
                    new IssuerActivationUpsertDto
                    {
                        ActivationId = activationId,
                        CustomerDisplay = customerDisplay,
                        DeviceName = device,
                        HardwareId = hardware,
                        Status = status
                    });
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Activación actualizada {activationId}");
                await LoadActivationsFromServerAsync();
                await RefreshDashboardStatsAsync();
                MessageBox.Show("Activación guardada.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            catch
            {
                //
            }
        }

        MessageBox.Show("No se pudo guardar.", "Grunflex", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void RegisterHeartbeatRow(ActivationListItem? item)
    {
        if (item == null || item.RecordId == Guid.Empty) return;
        _ = HeartbeatAsync(item);
    }

    private async Task HeartbeatAsync(ActivationListItem item)
    {
        foreach (var baseUrl in EnumerateApiBaseUrls())
        {
            try
            {
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(15));
                ApplyIssuerHeaders(client);
                using var resp = await client.PutAsJsonAsync(
                    $"/api/IssuerActivations/{item.RecordId}",
                    new IssuerActivationUpsertDto
                    {
                        ActivationId = item.ActivationCode,
                        CustomerDisplay = item.Client,
                        DeviceName = item.Device,
                        HardwareId = item.HardwareId,
                        LastSeenUtc = DateTime.UtcNow,
                        Status = item.Status
                    });
                if (!resp.IsSuccessStatusCode)
                    continue;

                IssuerAuditLog.Append($"Heartbeat {item.ActivationCode}");
                await LoadActivationsFromServerAsync();
                return;
            }
            catch
            {
                //
            }
        }
    }

    private async Task GenerateReportExportAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|Texto (*.txt)|*.txt",
            FileName = $"reporte-grunflex-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            var sb = new StringBuilder();
            var title = ReportTypeIndex switch
            {
                1 => "activaciones",
                2 => "resumen",
                3 => "renovaciones",
                _ => "licencias"
            };

            sb.AppendLine($"# Grunflex · {title} · {DateTime.Now:yyyy-MM-dd HH:mm}");

            if (ReportTypeIndex == 0 || ReportTypeIndex == 3)
            {
                await LoadLicensesFromServerAsync();
                sb.AppendLine("ActivationId,Cliente,Plan,Cajas,Estado,VencimientoUTC");
                foreach (var x in Licenses)
                    sb.AppendLine(string.Join(',', Quote(x.LicenseId), Quote(x.Client), Quote(x.Plan), x.Boxes, Quote(x.Status), Quote(x.Expiration)));
            }

            if (ReportTypeIndex == 1)
            {
                await LoadActivationsFromServerAsync();
                sb.AppendLine("ActivationId,Cliente,Dispositivo,Hardware,UltimaConexion,Estado");
                foreach (var x in Activations)
                    sb.AppendLine(string.Join(',', Quote(x.ActivationCode), Quote(x.Client), Quote(x.Device), Quote(x.HardwareId), Quote(x.LastConnection), Quote(x.Status)));
            }

            if (ReportTypeIndex == 2)
            {
                await RefreshDashboardStatsAsync();
                sb.AppendLine("Metric,Valor");
                sb.AppendLine($"Licencias activas,{DashboardActiveLicenses}");
                sb.AppendLine($"Licencias vencidas,{DashboardExpiredLicenses}");
                sb.AppendLine($"Activaciones,{DashboardActivations}");
                sb.AppendLine($"Clientes,{DashboardClients}");
            }

            await File.WriteAllTextAsync(dlg.FileName, sb.ToString(), Encoding.UTF8);

            RecentReports.Insert(
                0,
                new ReportListItem(
                    $"{title} · {Path.GetFileName(dlg.FileName)}",
                    DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture),
                    "CSV"));

            while (RecentReports.Count > 12)
                RecentReports.RemoveAt(RecentReports.Count - 1);

            IssuerAuditLog.Append($"Reporte generado {dlg.FileName}");
            MessageBox.Show($"Archivo guardado:\n{dlg.FileName}", "Reportes", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Reportes", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string Quote(string? s)
    {
        var t = s ?? string.Empty;
        if (t.Contains('"') || t.Contains(',') || t.Contains('\n'))
            return "\"" + t.Replace("\"", "\"\"") + "\"";
        return t;
    }

    private void TestSmtpConnection()
    {
        if (!int.TryParse(SmtpPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port))
            port = 587;

        try
        {
            using var msg = new MailMessage();
            msg.From = new MailAddress(SenderEmail);
            msg.To.Add(SenderEmail);
            msg.Subject = "Grunflex License Issuer · prueba SMTP";
            msg.Body = $"Prueba {DateTime.Now:O}";

            using var smtp = new SmtpClient(SmtpHost, port)
            {
                EnableSsl = true,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            var pwdDlg = new PromptDialog("SMTP", "Contraseña del buzón (opcional, dejar vacío si no aplica):", "")
            {
                Owner = Application.Current?.MainWindow
            };
            if (pwdDlg.ShowDialog() != true)
                return;

            if (!string.IsNullOrEmpty(pwdDlg.Result))
                smtp.Credentials = new NetworkCredential(SenderEmail, pwdDlg.Result);

            smtp.Send(msg);
            MessageBox.Show("Correo de prueba enviado (revisa bandeja / spam).", "SMTP", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo enviar: " + ex.Message, "SMTP", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class LicenseIssuerStatsDto
    {
        public int ActiveLicenses { get; set; }

        public int ExpiredLicenses { get; set; }

        public int ActivationsTotal { get; set; }

        public int ClientsTotal { get; set; }

        public int LicensesTotal { get; set; }

        public int PlanBasico { get; set; }

        public int PlanMedium { get; set; }

        public int PlanPlus { get; set; }

        public int PlanOtro { get; set; }

        public List<int>? NewLicensesByMonthLast6 { get; set; }

        public List<string>? SparkMonthLabels { get; set; }

        public int NewLicensesThisCalendarMonthUtc { get; set; }

        public int NewLicensesPreviousCalendarMonthUtc { get; set; }

        public int ExpiringWithin7Days { get; set; }

        public int PendingDeviceActivations { get; set; }
    }

    private sealed class ApiIssuerClientRecord
    {
        public Guid Id { get; set; }

        public string ClientCode { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Business { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string Phone { get; set; } = string.Empty;

        public DateTime CreatedAtUtc { get; set; }

        public int LinkedLicensesCount { get; set; }
    }

    private sealed class ApiIssuerActivationRecord
    {
        public Guid Id { get; set; }

        public string ActivationId { get; set; } = string.Empty;

        public string CustomerDisplay { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string HardwareId { get; set; } = string.Empty;

        public DateTime ActivatedAtUtc { get; set; }

        public DateTime LastSeenUtc { get; set; }

        public string Status { get; set; } = string.Empty;
    }

    private sealed class IssuerClientUpsertDto
    {
        public string Name { get; set; } = string.Empty;

        public string Business { get; set; } = string.Empty;

        public string Email { get; set; } = string.Empty;

        public string Phone { get; set; } = string.Empty;
    }

    private sealed class IssuerActivationUpsertDto
    {
        public string ActivationId { get; set; } = string.Empty;

        public string CustomerDisplay { get; set; } = string.Empty;

        public string DeviceName { get; set; } = string.Empty;

        public string HardwareId { get; set; } = string.Empty;

        public DateTime? ActivatedAtUtc { get; set; }

        public DateTime? LastSeenUtc { get; set; }

        public string Status { get; set; } = "Activa";
    }
}
