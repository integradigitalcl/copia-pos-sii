using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Grunflex.Licensing;
using System.Windows.Threading;
using Grunflex.LicenseIssuer.Interop;
using Grunflex.LicenseIssuer.Persistence;
using Microsoft.Win32;

namespace Grunflex.LicenseIssuer.ViewModels;

public sealed partial class MainViewModel : INotifyPropertyChanged
{
    // Debe coincidir con la clave temporal usada por el POS.
    private const string PosSigningSecret = "GRUNFLEX_POS_LICENSE_SIGNING_SECRET_V1_CHANGE_ME";

    private string _customerName = string.Empty;
    private string _businessName = string.Empty;
    private string _licenseType = "Suscripción";
    private int _numberOfBoxes = 5;
    private int _offlineGraceDays = GrunflexLicenseDefaults.OfflineGraceDays;
    private DateTime _expirationDate = DateTime.Today.AddYears(1);
    private string _initialStatus = "Activa";
    private bool _onlineSupport = true;
    private bool _cloudBackup = true;
    private bool _prioritySupport = true;
    private string _activationId = string.Empty;
    private string _licenseToken = string.Empty;
    private string _selectedSection = "Dashboard";
    private string _licenseSearch = string.Empty;
    private string _clientSearch = string.Empty;
    private string _activationSearch = string.Empty;
    private string _selectedConfigTab = "General";
    private string _smtpHost = "smtp.grunflex.cl";
    private string _smtpPort = "587";
    private string _senderEmail = "soporte@grunflex.cl";
    private bool _forcePasswordRotation = true;
    private bool _lockAfterFailedAttempts = true;
    private bool _enableAuditLog = true;
    private string _backupFrequency = "Diario";
    private string _backupPath = @"C:\Respaldos\Grunflex";
    private string _timeZone = "UTC-04:00 (Santiago)";
    private string _language = "Español";
    private bool _onlineValidation = true;
    private bool _systemNotifications = true;
    private bool _automaticBackups = true;
    private bool _maintenanceMode;

    private string _apiBaseUrl = string.Empty;

    private int _licensePage = 1;

    private readonly int _licensePageSize = 25;

    private int _licenseTotalCount;

    /// <summary>Todas | Activas | Vencidas | Próximas</summary>
    private string _licenseStatusFilter = "Todas";

    private readonly DispatcherTimer _licenseSearchDebounce;

    private readonly DispatcherTimer _clientSearchDebounce;

    private readonly DispatcherTimer _activationSearchDebounce;

    private int _dashboardActiveLicenses;

    private int _dashboardExpiredLicenses;

    private int _dashboardActivations;

    private int _dashboardClients;

    private int _reportTypeIndex;

    private int _reportPeriodMonthsBack;

    public ObservableCollection<ReportListItem> RecentReports { get; } = new();

    private PerformanceCounter? _cpuCounter;
    private readonly DispatcherTimer _hostMetricsTimer;
    private int _apiHealthPhase;
    private bool _monitoringDisposed;
    private readonly string _systemDriveRoot = GetSystemDriveRoot();

    private double _cpuPercent;
    private double _ramPercentUsed;
    private double _diskPercentUsed;
    private string _apiHealthSummary = "Comprobando API local…";
    private bool _apiHealthOk;

    public MainViewModel()
    {
        GenerateCommand = new RelayCommand(GenerateLicense);
        ClearCommand = new RelayCommand(ClearForm);
        IncreaseBoxesCommand = new RelayCommand(() => NumberOfBoxes++);
        DecreaseBoxesCommand = new RelayCommand(() =>
        {
            if (NumberOfBoxes > 1) NumberOfBoxes--;
        });
        IncreaseOfflineGraceCommand = new RelayCommand(() => OfflineGraceDays++);
        DecreaseOfflineGraceCommand = new RelayCommand(() =>
        {
            if (OfflineGraceDays > 0) OfflineGraceDays--;
        });
        CopyTokenCommand = new RelayCommand(CopyToken);
        SaveSettingsCommand = new RelayCommand(SaveSettings);
        DeleteLicenseCommand = new RelayCommand<LicenseListItem>(DeleteLicenseRow);
        CopyLicenseRowCommand = new RelayCommand<LicenseListItem>(CopyLicenseRow);
        ViewLicenseTokenCommand = new RelayCommand<LicenseListItem>(ViewLicenseTokenRow);
        PrevLicensePageCommand = new RelayCommand(PrevLicensePage);
        NextLicensePageCommand = new RelayCommand(NextLicensePage);
        RefreshLicensesCommand = new RelayCommand(() => _ = LoadLicensesFromServerAsync());
        GenerateReportCommand = new RelayCommand(() => _ = GenerateReportExportAsync());
        TestSmtpCommand = new RelayCommand(TestSmtpConnection);
        DeleteClientCommand = new RelayCommand<ClientListItem>(DeleteClientRow);
        CopyClientEmailCommand = new RelayCommand<ClientListItem>(CopyClientEmailRow);
        EditClientCommand = new RelayCommand<ClientListItem>(EditClientRow);
        DeleteActivationCommand = new RelayCommand<ActivationListItem>(DeleteActivationRow);
        EditActivationCommand = new RelayCommand<ActivationListItem>(EditActivationRow);
        RegisterHeartbeatCommand = new RelayCommand<ActivationListItem>(RegisterHeartbeatRow);

        _licenseSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _licenseSearchDebounce.Tick += (_, _) =>
        {
            _licenseSearchDebounce.Stop();
            LicensePage = 1;
            _ = LoadLicensesFromServerAsync();
        };

        _clientSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _clientSearchDebounce.Tick += (_, _) =>
        {
            _clientSearchDebounce.Stop();
            _ = LoadClientsFromServerAsync();
        };

        _activationSearchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _activationSearchDebounce.Tick += (_, _) =>
        {
            _activationSearchDebounce.Stop();
            _ = LoadActivationsFromServerAsync();
        };

        var persisted = IssuerSettingsPersistence.TryLoad();
        if (persisted != null)
            IssuerSettingsPersistence.ApplyTo(this, persisted);

        if (string.IsNullOrWhiteSpace(ActivationId))
            RegenerateActivationId();

        _hostMetricsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hostMetricsTimer.Tick += HostMetricsTimerOnTick;
        InitHostMonitoring();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public RelayCommand GenerateCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand IncreaseBoxesCommand { get; }
    public RelayCommand DecreaseBoxesCommand { get; }
    public RelayCommand IncreaseOfflineGraceCommand { get; }
    public RelayCommand DecreaseOfflineGraceCommand { get; }
    public RelayCommand CopyTokenCommand { get; }
    public RelayCommand SaveSettingsCommand { get; }

    public RelayCommand<LicenseListItem> DeleteLicenseCommand { get; }

    public RelayCommand<LicenseListItem> CopyLicenseRowCommand { get; }

    public RelayCommand<LicenseListItem> ViewLicenseTokenCommand { get; }

    public RelayCommand PrevLicensePageCommand { get; }

    public RelayCommand NextLicensePageCommand { get; }

    public RelayCommand RefreshLicensesCommand { get; }

    public RelayCommand GenerateReportCommand { get; }

    public RelayCommand TestSmtpCommand { get; }

    public RelayCommand<ClientListItem> DeleteClientCommand { get; }

    public RelayCommand<ClientListItem> CopyClientEmailCommand { get; }

    public RelayCommand<ClientListItem> EditClientCommand { get; }

    public RelayCommand<ActivationListItem> DeleteActivationCommand { get; }

    public RelayCommand<ActivationListItem> EditActivationCommand { get; }

    public RelayCommand<ActivationListItem> RegisterHeartbeatCommand { get; }

    public string CustomerName
    {
        get => _customerName;
        set
        {
            if (SetProperty(ref _customerName, value))
            {
                OnPropertyChanged(nameof(SummaryCustomer));
            }
        }
    }

    public string BusinessName
    {
        get => _businessName;
        set
        {
            if (SetProperty(ref _businessName, value))
            {
                OnPropertyChanged(nameof(SummaryCustomer));
            }
        }
    }

    public string LicenseType
    {
        get => _licenseType;
        set
        {
            if (SetProperty(ref _licenseType, value))
            {
                OnPropertyChanged(nameof(IsSubscription));
                OnPropertyChanged(nameof(SummaryExpiration));
                OnPropertyChanged(nameof(SummaryLicenseTypeBadge));
            }
        }
    }

    public int NumberOfBoxes
    {
        get => _numberOfBoxes;
        set
        {
            if (SetProperty(ref _numberOfBoxes, value))
            {
                OnPropertyChanged(nameof(SummaryBoxes));
            }
        }
    }

    public int OfflineGraceDays
    {
        get => _offlineGraceDays;
        set
        {
            var normalized = Math.Clamp(value, 0, 365);
            if (SetProperty(ref _offlineGraceDays, normalized))
            {
                OnPropertyChanged(nameof(SummaryOfflineGrace));
            }
        }
    }

    public DateTime ExpirationDate
    {
        get => _expirationDate;
        set
        {
            if (SetProperty(ref _expirationDate, value))
            {
                OnPropertyChanged(nameof(SummaryExpiration));
            }
        }
    }

    public string InitialStatus
    {
        get => _initialStatus;
        set
        {
            if (SetProperty(ref _initialStatus, value))
            {
                OnPropertyChanged(nameof(SummaryStatusBadge));
            }
        }
    }

    public bool OnlineSupport
    {
        get => _onlineSupport;
        set
        {
            if (SetProperty(ref _onlineSupport, value))
            {
                OnPropertyChanged(nameof(SummaryModules));
            }
        }
    }

    public bool CloudBackup
    {
        get => _cloudBackup;
        set
        {
            if (SetProperty(ref _cloudBackup, value))
            {
                OnPropertyChanged(nameof(SummaryModules));
            }
        }
    }

    public bool PrioritySupport
    {
        get => _prioritySupport;
        set
        {
            if (SetProperty(ref _prioritySupport, value))
            {
                OnPropertyChanged(nameof(SummaryModules));
            }
        }
    }

    public string ActivationId
    {
        get => _activationId;
        set => SetProperty(ref _activationId, value);
    }

    public string LicenseToken
    {
        get => _licenseToken;
        private set
        {
            if (SetProperty(ref _licenseToken, value))
            {
                OnPropertyChanged(nameof(HasLicenseToken));
            }
        }
    }

    public bool HasLicenseToken => !string.IsNullOrWhiteSpace(LicenseToken);

    public bool IsSubscription => string.Equals(LicenseType, "Suscripción", StringComparison.OrdinalIgnoreCase);

    public string LicenseSearch
    {
        get => _licenseSearch;
        set
        {
            if (!SetProperty(ref _licenseSearch, value)) return;
            _licenseSearchDebounce.Stop();
            _licenseSearchDebounce.Start();
        }
    }

    public ObservableCollection<LicenseListItem> Licenses { get; } = new();
    public string LicensesSubtitle { get; private set; } = "Administra todas las licencias del sistema";

    public string ClientSearch
    {
        get => _clientSearch;
        set
        {
            if (!SetProperty(ref _clientSearch, value)) return;
            _clientSearchDebounce.Stop();
            _clientSearchDebounce.Start();
        }
    }

    public ObservableCollection<ClientListItem> Clients { get; } = new();

    public string ActivationSearch
    {
        get => _activationSearch;
        set
        {
            if (!SetProperty(ref _activationSearch, value)) return;
            _activationSearchDebounce.Stop();
            _activationSearchDebounce.Start();
        }
    }

    public ObservableCollection<ActivationListItem> Activations { get; } = new();

    public string SmtpHost
    {
        get => _smtpHost;
        set => SetProperty(ref _smtpHost, value);
    }

    public string SmtpPort
    {
        get => _smtpPort;
        set => SetProperty(ref _smtpPort, value);
    }

    public string SenderEmail
    {
        get => _senderEmail;
        set => SetProperty(ref _senderEmail, value);
    }

    public bool ForcePasswordRotation
    {
        get => _forcePasswordRotation;
        set => SetProperty(ref _forcePasswordRotation, value);
    }

    public bool LockAfterFailedAttempts
    {
        get => _lockAfterFailedAttempts;
        set => SetProperty(ref _lockAfterFailedAttempts, value);
    }

    public bool EnableAuditLog
    {
        get => _enableAuditLog;
        set => SetProperty(ref _enableAuditLog, value);
    }

    public string BackupFrequency
    {
        get => _backupFrequency;
        set => SetProperty(ref _backupFrequency, value);
    }

    public string BackupPath
    {
        get => _backupPath;
        set => SetProperty(ref _backupPath, value);
    }

    public string TimeZone
    {
        get => _timeZone;
        set => SetProperty(ref _timeZone, value);
    }

    public string Language
    {
        get => _language;
        set => SetProperty(ref _language, value);
    }

    public bool OnlineValidation
    {
        get => _onlineValidation;
        set => SetProperty(ref _onlineValidation, value);
    }

    public bool SystemNotifications
    {
        get => _systemNotifications;
        set => SetProperty(ref _systemNotifications, value);
    }

    public bool AutomaticBackups
    {
        get => _automaticBackups;
        set => SetProperty(ref _automaticBackups, value);
    }

    public bool MaintenanceMode
    {
        get => _maintenanceMode;
        set => SetProperty(ref _maintenanceMode, value);
    }

    public string SelectedConfigTab
    {
        get => _selectedConfigTab;
        private set
        {
            if (!SetProperty(ref _selectedConfigTab, value)) return;
            OnPropertyChanged(nameof(IsConfigGeneralTab));
            OnPropertyChanged(nameof(IsConfigCorreoTab));
            OnPropertyChanged(nameof(IsConfigSeguridadTab));
            OnPropertyChanged(nameof(IsConfigRespaldosTab));
            OnPropertyChanged(nameof(IsConfigSistemaTab));
        }
    }

    public string SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value)) return;
            OnPropertyChanged(nameof(IsDashboardSection));
            OnPropertyChanged(nameof(IsLicenciasSection));
            OnPropertyChanged(nameof(IsClientesSection));
            OnPropertyChanged(nameof(IsActivacionesSection));
            OnPropertyChanged(nameof(IsReportesSection));
            OnPropertyChanged(nameof(IsConfiguracionSection));
            OnPropertyChanged(nameof(IsGenerateLicenseSection));
            OnPropertyChanged(nameof(IsPlaceholderSection));
        }
    }

    public bool IsDashboardSection => string.Equals(SelectedSection, "Dashboard", StringComparison.OrdinalIgnoreCase);

    public bool IsLicenciasSection => string.Equals(SelectedSection, "Licencias", StringComparison.OrdinalIgnoreCase);

    public bool IsClientesSection => string.Equals(SelectedSection, "Clientes", StringComparison.OrdinalIgnoreCase);

    public bool IsActivacionesSection => string.Equals(SelectedSection, "Activaciones", StringComparison.OrdinalIgnoreCase);

    public bool IsReportesSection => string.Equals(SelectedSection, "Reportes", StringComparison.OrdinalIgnoreCase);

    public bool IsConfiguracionSection => string.Equals(SelectedSection, "Configuracion", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigGeneralTab => string.Equals(SelectedConfigTab, "General", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigCorreoTab => string.Equals(SelectedConfigTab, "Correo", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigSeguridadTab => string.Equals(SelectedConfigTab, "Seguridad", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigRespaldosTab => string.Equals(SelectedConfigTab, "Respaldos", StringComparison.OrdinalIgnoreCase);

    public bool IsConfigSistemaTab => string.Equals(SelectedConfigTab, "Sistema", StringComparison.OrdinalIgnoreCase);

    public bool IsGenerateLicenseSection => string.Equals(SelectedSection, "GenerarLicencia", StringComparison.OrdinalIgnoreCase);

    public bool IsPlaceholderSection => !IsDashboardSection && !IsGenerateLicenseSection && !IsLicenciasSection && !IsClientesSection && !IsActivacionesSection && !IsReportesSection && !IsConfiguracionSection;

    public string HostMachineName => Environment.MachineName;

    public string SystemDriveCaption
    {
        get
        {
            var letter = _systemDriveRoot.TrimEnd('\\', '/');
            return string.IsNullOrEmpty(letter) ? "Disco sistema" : $"Disco sistema ({letter})";
        }
    }

    public double CpuPercent
    {
        get => _cpuPercent;
        private set => SetProperty(ref _cpuPercent, value);
    }

    public double RamPercentUsed
    {
        get => _ramPercentUsed;
        private set => SetProperty(ref _ramPercentUsed, value);
    }

    public double DiskPercentUsed
    {
        get => _diskPercentUsed;
        private set => SetProperty(ref _diskPercentUsed, value);
    }

    public string CpuPercentLabel => $"{CpuPercent:F0} %";

    public string RamPercentLabel => $"{RamPercentUsed:F0} %";

    public string DiskPercentLabel => $"{DiskPercentUsed:F0} % usado";

    public ObservableCollection<double> CpuSparkline { get; } = new();

    public ObservableCollection<double> RamSparkline { get; } = new();

    public string ApiHealthSummary
    {
        get => _apiHealthSummary;
        private set => SetProperty(ref _apiHealthSummary, value);
    }

    public bool ApiHealthOk
    {
        get => _apiHealthOk;
        private set => SetProperty(ref _apiHealthOk, value);
    }

    public string SummaryCustomer
    {
        get
        {
            var customerEmpty = string.IsNullOrWhiteSpace(CustomerName);
            var businessEmpty = string.IsNullOrWhiteSpace(BusinessName);
            if (customerEmpty && businessEmpty)
                return "---";

            var customer = customerEmpty ? "—" : CustomerName.Trim();
            var business = businessEmpty ? "—" : BusinessName.Trim();
            return $"{customer} - {business}";
        }
    }

    public string SummaryExpiration => IsSubscription ? ExpirationDate.ToString("dd/MM/yyyy") : "Sin vencimiento";

    public string SummaryBoxes => $"{NumberOfBoxes} dispositivos";

    public string SummaryOfflineGrace => OfflineGraceDays <= 0
        ? "Gracia offline: default POS (appsettings)"
        : $"{OfflineGraceDays} día(s) sin sync";

    public string SummaryLicenseTypeBadge => IsSubscription ? "Suscripción" : "Permanente";

    public string SummaryStatusBadge => InitialStatus;

    public string SummaryModules
    {
        get
        {
            var modules = new List<string>();
            if (OnlineSupport) modules.Add("OnlineSupport");
            if (CloudBackup) modules.Add("CloudBackup");
            if (PrioritySupport) modules.Add("PrioritySupport");
            return modules.Count == 0 ? "Ninguno" : string.Join(", ", modules);
        }
    }

    private void GenerateLicense()
    {
        if (string.IsNullOrWhiteSpace(CustomerName) || string.IsNullOrWhiteSpace(BusinessName))
        {
            MessageBox.Show("Completa el cliente y negocio para generar licencia.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Contrato compatible con GrunflexPOS2.Licensing.LicenseService.
        bool multicaja = NumberOfBoxes > 1;
        bool onlineSupport = OnlineSupport;
        bool cloudBackup = CloudBackup;
        bool prioritySupport = PrioritySupport;
        if (!multicaja && !onlineSupport && !cloudBackup && !prioritySupport)
        {
            MessageBox.Show("La licencia debe habilitar al menos un módulo para que el POS la acepte.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DateTime expUtc = IsSubscription
            ? ExpirationDate.Date.ToUniversalTime()
            : DateTime.UtcNow.AddYears(30);

        var payload = new GrunflexLicensePayload
        {
            Customer = $"{CustomerName.Trim()} - {BusinessName.Trim()}",
            Machine = string.Empty,
            ExpUtc = expUtc,
            Multicaja = multicaja,
            OnlineSupport = onlineSupport,
            CloudBackup = cloudBackup,
            PrioritySupport = prioritySupport,
            ActivationId = ActivationId.Trim(),
            OfflineGraceDays = OfflineGraceDays
        };

        string fullLicense;
        // Política: el LicenseIssuer SIEMPRE debe firmar GFv2 con RSA. Si no encuentra la
        // clave privada (instalación virgen, reset total, etc.) la generamos y persistimos
        // automáticamente en api.secrets.json + licensing-public.pem. Esto evita que
        // caigamos al camino "legacy HMAC", que históricamente produjo licencias que el POS
        // marcaba como "vencidas" porque la simetría JSON estaba rota.
        var pem = TryReadPrivateKeyPemFromSecrets();
        if (string.IsNullOrWhiteSpace(pem))
        {
            try
            {
                pem = GenerateAndPersistRsaKeypair(out var generatedPubPath);
                MessageBox.Show(
                    "No había clave privada RSA. Se generó una nueva y se guardó en:\n" +
                    "  api.secrets.json (Licensing.PrivateKeyPem)\n" +
                    "  " + generatedPubPath + "\n\n" +
                    "IMPORTANTE: para que el POS instalado en clientes valide esta licencia\n" +
                    "(y todas las que generes en adelante con esta clave) tenés que rebuild\n" +
                    "el instalador con build-staging.ps1 + compile-installer.ps1, así la nueva\n" +
                    "clave pública queda embebida en appsettings.json del POS.\n\n" +
                    "El cliente con esta licencia recién generada va a recibir 'Firma RSA\n" +
                    "inválida' hasta que reinstale el POS con el instalador nuevo.",
                    "Clave RSA generada",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "No se pudo generar el par RSA automáticamente:\n" + ex.Message,
                    "Firma RSA",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }
        }

        try
        {
            using var rsa = GrunflexLicenseCodec.ImportPrivateKeyFromPem(pem!);
            fullLicense = GrunflexLicenseCodec.EncodeV2(payload, rsa);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo firmar GFv2 (revisa PrivateKeyPem en api.secrets.json):\n" + ex.Message,
                "Firma RSA", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        LicenseToken = fullLicense;

        var persistidoEnServidor = PersistLicenseOnLocalServer(
            fullLicense,
            expUtc,
            multicaja,
            onlineSupport,
            cloudBackup,
            prioritySupport,
            OfflineGraceDays);
        SaveLicenseFile(fullLicense);
        RefreshSummaryDisplay();

        IssuerAuditLog.Append($"Licencia generada {ActivationId} para {CustomerName} / {BusinessName}.");
        if (EnableAuditLog)
            IssuerAuditLog.Append("AUDIT license_generated " + ActivationId);

        _ = RefreshDashboardStatsAsync();

        if (!persistidoEnServidor)
        {
            MessageBox.Show(
                "Licencia generada, pero no se pudo guardar en el servidor local. Verifica que GrunflexPOS.API esté ejecutándose en localhost.",
                "Servidor local",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void SaveLicenseFile(string token)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "License file (*.lic)|*.lic|Text file (*.txt)|*.txt",
            FileName = $"grunflex-{DateTime.Now:yyyyMMdd-HHmm}.lic"
        };

        if (dialog.ShowDialog() != true) return;
        File.WriteAllText(dialog.FileName, token, Encoding.UTF8);
    }

    private void ClearForm()
    {
        CustomerName = string.Empty;
        BusinessName = string.Empty;
        LicenseType = "Suscripción";
        NumberOfBoxes = 1;
        OfflineGraceDays = GrunflexLicenseDefaults.OfflineGraceDays;
        ExpirationDate = DateTime.Today.AddYears(1);
        InitialStatus = "Activa";
        OnlineSupport = true;
        CloudBackup = true;
        PrioritySupport = false;
        LicenseToken = string.Empty;
        RegenerateActivationId();
        RefreshSummaryDisplay();
    }

    private void RefreshSummaryDisplay()
    {
        OnPropertyChanged(nameof(SummaryCustomer));
        OnPropertyChanged(nameof(SummaryExpiration));
        OnPropertyChanged(nameof(SummaryBoxes));
        OnPropertyChanged(nameof(SummaryOfflineGrace));
        OnPropertyChanged(nameof(SummaryLicenseTypeBadge));
        OnPropertyChanged(nameof(SummaryStatusBadge));
        OnPropertyChanged(nameof(SummaryModules));
    }

    private void CopyToken()
    {
        if (string.IsNullOrWhiteSpace(LicenseToken)) return;
        Clipboard.SetText(LicenseToken);
    }

    private void RegenerateActivationId()
    {
        ActivationId = $"GF-{DateTime.Now:yyyyMMdd}-{RandomNumberGenerator.GetInt32(1000, 9999)}";
    }

    private void SeedLicenses()
    {
        if (Licenses.Count > 0) return;

        const string demoToken = "DEMO.NO.API.CONNECT";
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0001", "Minimarket Don Juan", "Plus", 3, "Activa", "31/05/2026", "01/05/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0002", "Juan Pérez", "Medium", 2, "Activa", "20/05/2026", "20/05/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0003", "Tienda San Miguel", "Básico", 1, "Vencida", "10/05/2025", "10/04/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0004", "Supermercado Central", "Plus", 4, "Activa", "05/06/2026", "05/05/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0005", "Almacén La Esquina", "Medium", 2, "Próxima a vencer", "25/05/2026", "25/04/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0006", "Distribuidora Los Andes", "Plus", 5, "Activa", "15/06/2026", "15/05/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0007", "Botillería El Buen Trago", "Básico", 1, "Vencida", "01/05/2025", "01/04/2025", demoToken));
        Licenses.Add(new LicenseListItem(Guid.Empty, "GF-2025-0008", "Farmacia Vida Salud", "Medium", 2, "Activa", "30/05/2026", "30/04/2025", demoToken));
    }

    private void SeedClients()
    {
        if (Clients.Count > 0) return;

        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0001", "Juan Pérez", "Minimarket Don Juan", "juan@minimarket.cl", "+56 9 1234 5678", 2));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0002", "María González", "Tienda San Miguel", "maria@tiendasanmiguel.cl", "+56 9 8765 4321", 1));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0003", "Carlos Ramírez", "Supermercado Central", "carlos@supercentral.cl", "+56 9 1111 2222", 3));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0004", "Ana López", "Almacén La Esquina", "ana@esquina.cl", "+56 9 3333 4444", 1));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0005", "Luis Fernández", "Distribuidora Los Andes", "luis@losandes.cl", "+56 9 5555 6666", 2));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0006", "Pedro Morales", "Botillería El Buen Trago", "pedro@buentrago.cl", "+56 9 7777 8888", 1));
        Clients.Add(new ClientListItem(Guid.Empty, "CLI-0007", "Sofía Herrera", "Farmacia Vida Salud", "sofia@vidasalud.cl", "+56 9 9999 0000", 2));
    }

    private void SeedActivations()
    {
        if (Activations.Count > 0) return;

        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0001", "GF-2025-0001", "Minimarket Don Juan", "Caja Principal", "ABC123DEF456", "01/05/2025", "29/05/2025 10:30", "Activa"));
        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0002", "GF-2025-0002", "Juan Pérez", "Caja 2", "XYZ789UVW012", "02/05/2025", "28/05/2025 14:22", "Activa"));
        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0003", "GF-2025-0003", "Tienda San Miguel", "Caja 3", "LMN456OPQ890", "03/05/2025", "27/05/2025 09:15", "Activa"));
        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0004", "GF-2025-0004", "Supermercado Central", "Caja Principal", "RST321JKL654", "04/05/2025", "29/05/2025 18:45", "Activa"));
        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0005", "GF-2025-0005", "Supermercado Central", "Caja 1", "GHV987PQW321", "05/05/2025", "26/05/2025 11:08", "Activa"));
        Activations.Add(new ActivationListItem(Guid.Empty, "ACT-0006", "GF-2025-0006", "Distribuidora Los Andes", "Caja Principal", "NOP654EFG098", "06/05/2025", "25/05/2025 16:40", "Activa"));
    }

    public void NavigateToSection(string section)
    {
        if (string.IsNullOrWhiteSpace(section)) return;
        var s = section.Trim();
        SelectedSection = s;
        if (string.Equals(s, "Dashboard", StringComparison.OrdinalIgnoreCase))
            _ = RefreshDashboardStatsAsync();
        if (string.Equals(s, "Reportes", StringComparison.OrdinalIgnoreCase))
            _ = RefreshDashboardStatsAsync();
    }

    public void SelectConfigTab(string tab)
    {
        if (string.IsNullOrWhiteSpace(tab)) return;
        SelectedConfigTab = tab.Trim();
    }

    public void StopHostMonitoring()
    {
        if (_monitoringDisposed) return;
        _monitoringDisposed = true;
        _hostMetricsTimer.Stop();
        _hostMetricsTimer.Tick -= HostMetricsTimerOnTick;
        _cpuCounter?.Dispose();
        _cpuCounter = null;
    }

    private void InitHostMonitoring()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _cpuCounter.NextValue();
        }
        catch
        {
            _cpuCounter?.Dispose();
            _cpuCounter = null;
        }

        _hostMetricsTimer.Start();
        OnHostMetricsTick();
        _ = RefreshApiHealthAsync();
        _ = RefreshDashboardStatsAsync();
    }

    private void HostMetricsTimerOnTick(object? sender, EventArgs e) => OnHostMetricsTick();

    private void OnHostMetricsTick()
    {
        if (_monitoringDisposed) return;

        double cpu = 0;
        try
        {
            cpu = _cpuCounter?.NextValue() ?? 0;
        }
        catch
        {
            cpu = 0;
        }

        cpu = Math.Clamp(cpu, 0, 100);

        double ram = NativeMemory.TryGetPhysicalMemoryUsedPercent(out var rp) ? rp : 0;
        ram = Math.Clamp(ram, 0, 100);

        double disk = 0;
        try
        {
            var di = new DriveInfo(_systemDriveRoot);
            if (di.IsReady && di.TotalSize > 0)
                disk = 100.0 * (di.TotalSize - di.AvailableFreeSpace) / di.TotalSize;
        }
        catch
        {
            disk = 0;
        }

        disk = Math.Clamp(disk, 0, 100);

        CpuPercent = cpu;
        RamPercentUsed = ram;
        DiskPercentUsed = disk;

        CpuSparkline.Add(cpu);
        while (CpuSparkline.Count > 48) CpuSparkline.RemoveAt(0);
        RamSparkline.Add(ram);
        while (RamSparkline.Count > 48) RamSparkline.RemoveAt(0);

        OnPropertyChanged(nameof(CpuPercentLabel));
        OnPropertyChanged(nameof(RamPercentLabel));
        OnPropertyChanged(nameof(DiskPercentLabel));

        _apiHealthPhase++;
        if (_apiHealthPhase >= 5)
        {
            _apiHealthPhase = 0;
            _ = RefreshApiHealthAsync();
        }
    }

    private async Task RefreshApiHealthAsync()
    {
        if (_monitoringDisposed) return;

        var summary =
            "Puerto 7279 sin respuesta: debe estar en ejecución el proyecto web GrunflexPOS.API (F5 o «dotnet run» en GrunflexPOS.API). "
            + "pgAdmin/PostgreSQL no levantan ese servicio; solo la base de datos.";
        var ok = false;
        var reachedServer = false;
        string? lastError = null;

        foreach (var raw in EnumerateApiBaseUrls())
        {
            var baseUrl = raw.TrimEnd('/');
            try
            {
                // /health incluye DB y puede tardar >3s (TaskCanceledException). /health/live solo confirma el proceso Kestrel.
                using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(10));
                var sw = Stopwatch.StartNew();
                using var resp = await client.GetAsync("/health/live").ConfigureAwait(false);
                sw.Stop();
                var ms = sw.ElapsedMilliseconds;
                reachedServer = true;
                ok = resp.IsSuccessStatusCode;
                summary = ok
                    ? $"{baseUrl} · API en línea · {ms} ms"
                    : $"{baseUrl} · HTTP {(int)resp.StatusCode} ({resp.ReasonPhrase}) · {ms} ms";
                break;
            }
            catch (Exception ex)
            {
                lastError = ex.GetBaseException().Message;
            }
        }

        if (!reachedServer && !string.IsNullOrWhiteSpace(lastError))
            summary += $" Detalle: {lastError}";

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
            return;

        await dispatcher.InvokeAsync(() =>
        {
            if (_monitoringDisposed) return;
            ApiHealthOk = ok;
            ApiHealthSummary = summary;
        });
    }

    private static string GetSystemDriveRoot()
    {
        try
        {
            var sys = Environment.GetFolderPath(Environment.SpecialFolder.System);
            if (!string.IsNullOrEmpty(sys) && sys.Length >= 3)
                return sys[..3];
        }
        catch
        {
            // ignorar
        }

        return "C:\\";
    }

    /// <summary>
    /// La API local usa HTTPS con certificado de desarrollo; HttpClient falla si no se relaja la validación solo para loopback.
    /// </summary>
    private static HttpClient CreateLocalApiHttpClient(string baseUrl, TimeSpan timeout)
    {
        var root = baseUrl.Trim().TrimEnd('/');
        var handler = new HttpClientHandler();
        handler.ServerCertificateCustomValidationCallback = static (request, _, _, errors) =>
        {
            var host = request.RequestUri?.Host;
            if (EsLoopbackHost(host))
                return true;
            return errors == SslPolicyErrors.None;
        };

        return new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri(root + "/"),
            Timeout = timeout
        };
    }

    private static void ApplyIssuerHeaders(HttpClient client)
    {
        var key = TryReadIssuerApiKeyFromSecrets();
        if (!string.IsNullOrWhiteSpace(key))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Grunflex-Issuer-Key", key);
    }

    private static string? TryReadIssuerApiKeyFromSecrets()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS",
                "data",
                "api.secrets.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Licensing", out var lic))
                return null;

            return lic.TryGetProperty("IssuerApiKey", out var k) ? k.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Genera un par RSA 2048 y lo persiste en api.secrets.json (Licensing.PrivateKeyPem +
    /// Licensing.PublicKeyPem). También exporta la clave pública a licensing-public.pem
    /// para que build-staging.ps1 la encuentre y la inyecte en el appsettings.json del POS.
    /// Devuelve el PEM de la clave privada.
    /// </summary>
    private static string GenerateAndPersistRsaKeypair(out string publicPemPath)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GrunflexPOS",
            "data");
        Directory.CreateDirectory(dir);

        using var rsa = RSA.Create(2048);
        var privatePem = rsa.ExportRSAPrivateKeyPem();
        var publicPem = rsa.ExportSubjectPublicKeyInfoPem();

        var secretsPath = Path.Combine(dir, "api.secrets.json");
        System.Text.Json.Nodes.JsonObject root;
        try
        {
            if (File.Exists(secretsPath))
            {
                var text = File.ReadAllText(secretsPath);
                root = System.Text.Json.Nodes.JsonNode.Parse(text)?.AsObject()
                       ?? new System.Text.Json.Nodes.JsonObject();
            }
            else
            {
                root = new System.Text.Json.Nodes.JsonObject();
            }
        }
        catch
        {
            root = new System.Text.Json.Nodes.JsonObject();
        }

        var licensing = root["Licensing"] as System.Text.Json.Nodes.JsonObject
                        ?? new System.Text.Json.Nodes.JsonObject();
        licensing["PrivateKeyPem"] = privatePem;
        licensing["PublicKeyPem"] = publicPem;
        if (licensing["IssuerApiKey"] == null)
        {
            licensing["IssuerApiKey"] = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        }
        root["Licensing"] = licensing;

        File.WriteAllText(
            secretsPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        publicPemPath = Path.Combine(dir, "licensing-public.pem");
        File.WriteAllText(publicPemPath, publicPem);

        return privatePem;
    }

    private static string? TryReadPrivateKeyPemFromSecrets()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GrunflexPOS",
                "data",
                "api.secrets.json");
            if (!File.Exists(path))
                return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Licensing", out var lic))
                return null;

            return lic.TryGetProperty("PrivateKeyPem", out var k) ? k.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool EsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase)
               || string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase);
    }

    private void SaveSettings()
    {
        try
        {
            var dto = IssuerSettingsPersistence.FromViewModel(this);
            IssuerSettingsPersistence.Save(dto);
            if (EnableAuditLog)
                IssuerAuditLog.Append("Configuración guardada.");

            MessageBox.Show(
                $"Configuración guardada en:\n{IssuerSettingsPersistence.GetFilePath()}",
                "Configuración",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("No se pudo guardar: " + ex.Message, "Configuración", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    public sealed record LicenseListItem(
        Guid RecordId,
        string LicenseId,
        string Client,
        string Plan,
        int Boxes,
        string Status,
        string Expiration,
        string CreatedAt,
        string FullLicenseToken);

    public sealed record ClientListItem(
        Guid RecordId,
        string ClientId,
        string Name,
        string Business,
        string Email,
        string Phone,
        int Licenses);

    public sealed record ActivationListItem(
        Guid RecordId,
        string ActivationCode,
        string LicenseId,
        string Client,
        string Device,
        string HardwareId,
        string ActivatedAt,
        string LastConnection,
        string Status);

    public sealed record ReportListItem(string Title, string GeneratedAtLocal, string Format);

    /// <summary>Filas de la distribución por plan en el dashboard.</summary>
    public sealed record DashboardPlanSlice(string Label, double Percent, string DetailText);

    public enum DashboardAlertSeverity
    {
        Neutral,
        Danger,
        Warning,
        Info
    }

    /// <summary>Línea de alertas del dashboard (texto + severidad para color).</summary>
    public sealed record DashboardAlertItem(string Text, DashboardAlertSeverity Severity);

    private sealed class ApiPagedResult<T>
    {
        public List<T> Items { get; set; } = new();
        public int Total { get; set; }
    }

    private sealed class ApiLicenseIssuerRecord
    {
        public Guid Id { get; set; }

        public string ActivationId { get; set; } = string.Empty;

        public string CustomerName { get; set; } = string.Empty;

        public string BusinessName { get; set; } = string.Empty;

        public string LicenseType { get; set; } = string.Empty;

        public int NumberOfBoxes { get; set; }

        public int OfflineGraceDays { get; set; }

        public DateTime ExpUtc { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public string LicenseToken { get; set; } = string.Empty;
    }

    private static string ComputeHmacSignatureHex(string payloadBase64Url)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(PosSigningSecret);
        byte[] dataBytes = Encoding.UTF8.GetBytes(payloadBase64Url);
        using var hmac = new HMACSHA256(secretBytes);
        byte[] hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash);
    }

    private bool PersistLicenseOnLocalServer(
        string fullLicense,
        DateTime expUtc,
        bool multicaja,
        bool onlineSupport,
        bool cloudBackup,
        bool prioritySupport,
        int offlineGraceDays)
    {
        try
        {
            var payload = new
            {
                ActivationId,
                CustomerName,
                BusinessName,
                LicenseType,
                NumberOfBoxes,
                OfflineGraceDays = offlineGraceDays,
                ExpUtc = expUtc,
                Multicaja = multicaja,
                OnlineSupport = onlineSupport,
                CloudBackup = cloudBackup,
                PrioritySupport = prioritySupport,
                LicenseToken = fullLicense
            };

            foreach (var baseUrl in EnumerateApiBaseUrls())
            {
                try
                {
                    using var client = CreateLocalApiHttpClient(baseUrl, TimeSpan.FromSeconds(5));
                    ApplyIssuerHeaders(client);
                    var response = client.PostAsJsonAsync("/api/LicenseIssuer", payload).GetAwaiter().GetResult();
                    if (response.IsSuccessStatusCode)
                        return true;
                }
                catch
                {
                    // Se intenta el siguiente endpoint local.
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] input)
    {
        return Convert.ToBase64String(input).Replace("+", "-").Replace("/", "_").TrimEnd('=');
    }

    private static string Chunk(string value, int size)
    {
        var parts = new List<string>();
        for (var i = 0; i < value.Length; i += size)
        {
            parts.Add(value.Substring(i, Math.Min(size, value.Length - i)));
        }

        return string.Join("-", parts).ToUpperInvariant();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
