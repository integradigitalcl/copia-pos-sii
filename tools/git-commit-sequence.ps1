# Secuencia de commits modulares — GrunflexPOS2
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

function Stage-Paths([string[]]$Paths) {
    foreach ($p in $Paths) {
        if (-not (Test-Path $p)) { Write-Warning "Skip missing: $p"; continue }
        git add -- "$p"
    }
}

function Stage-Tree([string]$Root, [string[]]$ExcludeDirs = @('bin','obj')) {
    if (-not (Test-Path $Root)) { return }
    Get-ChildItem -Path $Root -Recurse -File |
        Where-Object {
            $rel = $_.FullName
            $excluded = $false
            foreach ($dir in $ExcludeDirs) {
                if ($rel -match "\\$([regex]::Escape($dir))\\") { $excluded = $true; break }
            }
            -not $excluded
        } |
        ForEach-Object { git add -- $_.FullName }
}

function Commit-IfStaged([string]$Msg) {
    $staged = git diff --cached --name-only
    if (-not $staged) { Write-Warning "Nothing staged for: $Msg"; return }
    git commit -m $Msg
    if ($LASTEXITCODE -ne 0) { throw "Commit failed: $Msg" }
    Write-Host "OK: $Msg" -ForegroundColor Green
}

$posCsproj = "GrunflexPOS2/GrunflexPOS2.csproj"
$absCsproj = "Grunflex.Licensing.Abstractions/Grunflex.Licensing.Abstractions.csproj"
$posCsprojBackup = Get-Content $posCsproj -Raw
$absCsprojBackup = Get-Content $absCsproj -Raw

function Set-PosCsprojWithoutAbstractions {
    (Get-Content $posCsproj -Raw) -replace '(?s)\s*<ItemGroup>\s*<ProjectReference Include="\.\.\\Grunflex\.Licensing\.Abstractions\\Grunflex\.Licensing\.Abstractions\.csproj" />\s*</ItemGroup>\s*','' |
        Set-Content $posCsproj -NoNewline
}
function Set-AbsCsprojWithoutBcrypt {
@'
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>Grunflex.Licensing</RootNamespace>
  </PropertyGroup>

</Project>
'@ | Set-Content $absCsproj -NoNewline
}
function Restore-Csproj {
    $posCsprojBackup | Set-Content $posCsproj -NoNewline
    $absCsprojBackup | Set-Content $absCsproj -NoNewline
}

Write-Host "=== 00 Foundation ===" -ForegroundColor Cyan
Set-PosCsprojWithoutAbstractions
Stage-Paths @('.gitignore')
Stage-Paths @(
    'GrunflexPOS2/App.xaml','GrunflexPOS2/GrunflexPOS2.csproj',
    'GrunflexPOS2/Data/GrunflexDbContext.cs','GrunflexPOS2/Data/GrunflexDbContextFactory.cs',
    'GrunflexPOS2/Data/LocalDatabasePaths.cs','GrunflexPOS2/Data/GrunflexDataDirectoryAcl.cs',
    'GrunflexPOS2/Data/SqliteSchemaBootstrap.cs','GrunflexPOS2/Data/SqliteBusyTimeoutInterceptor.cs',
    'GrunflexPOS2/Data/SqliteConnectionStringHelpers.cs','GrunflexPOS2/Data/PosEdgeRoleProbe.cs',
    'GrunflexPOS2/Data/TerminalConfigProbe.cs',
    'GrunflexPOS2/Migrations/GrunflexDbContextModelSnapshot.cs',
    'GrunflexPOS2/Migrations/20260428002237_InitialSqlite.cs',
    'GrunflexPOS2/Migrations/20260428002237_InitialSqlite.Designer.cs',
    'GrunflexPOS2/Migrations/20260509210347_AgregarConsumoPersonalDetalleCodigo.cs',
    'GrunflexPOS2/Migrations/20260509210347_AgregarConsumoPersonalDetalleCodigo.Designer.cs'
)
git add -u -- GrunflexPOS2/Migrations/
Stage-Paths @('GrunflexPOS2/Models')
Stage-Paths @('GrunflexPOS2/Domain/Repositories','GrunflexPOS2/Domain/Abstractions/IUserSessionContext.cs')
Stage-Paths @('GrunflexPOS2/Infrastructure/Session','GrunflexPOS2/Infrastructure/Repositories')
Stage-Tree 'GrunflexPOS2/Application'
Stage-Tree 'GrunflexPOS2/Diagnostics'
Stage-Tree 'GrunflexPOS2/ViewModels'
Stage-Paths @(
    'GrunflexPOS2/Services/CajaService.cs','GrunflexPOS2/Services/CorteService.cs','GrunflexPOS2/Services/VentaService.cs',
    'GrunflexPOS2/Services/ReportePdfService.cs','GrunflexPOS2/Services/ReporteService.cs','GrunflexPOS2/Services/TicketPdfService.cs',
    'GrunflexPOS2/Services/BoletaPdfService.cs','GrunflexPOS2/Services/ConfiguracionService.cs','GrunflexPOS2/Services/PosDiagnostics.cs',
    'GrunflexPOS2/Services/CustomFontResolver.cs','GrunflexPOS2/Services/EmailService.cs','GrunflexPOS2/Services/InicializadorService.cs',
    'GrunflexPOS2/Services/LectorCodigoService.cs','GrunflexPOS2/Services/ProductoLocalLookupService.cs','GrunflexPOS2/Services/IProductoLookupService.cs',
    'GrunflexPOS2/Services/InventarioExcelImportService.cs','GrunflexPOS2/Services/LogoHelper.cs','GrunflexPOS2/Services/SoporteContexto.cs',
    'GrunflexPOS2/Services/CajonDineroService.cs'
)
Stage-Tree 'GrunflexPOS2/Services/DTOs'
Stage-Tree 'GrunflexPOS2/Services/DataSources'
Stage-Tree 'GrunflexPOS2/Services/Discovery'
Stage-Tree 'GrunflexPOS2/Services/Telemetry'
Stage-Tree 'GrunflexPOS2/Services/Updates'
Stage-Paths @(
    'GrunflexPOS2/Services/API/PagoApiService.cs','GrunflexPOS2/Services/API/ProductoApiService.cs',
    'GrunflexPOS2/Services/API/SupportTicketApi.cs','GrunflexPOS2/Services/API/BackupsApiClient.cs'
)
$foundationViews = @(
    'VentasView','CobroView','CorteView','ProductosView','InventarioView','VentasDelDiaView',
    'BuscarProductoWindow','DashboardView','BasculaView','CajonDineroView','CorreoView',
    'CorteConfigView','FacturacionView','FolioView','FormasPagoView','ImpresoraView',
    'LectorCodigoView','LogoView','NavegadorView','PagoView','UnidadesView','WebViewWindow',
    'TicketPdfSuccessWindow','SupportTicketWindow','ExistenciaForegroundConverter','MainWindow'
)
foreach ($v in $foundationViews) { Stage-Paths @("GrunflexPOS2/Views/$v.xaml","GrunflexPOS2/Views/$v.xaml.cs") }
Stage-Paths @('GrunflexPOS2/Views/CajaView.xaml','GrunflexPOS2/appsettings.json','GrunflexPOS2/appsettings.local.json')
Stage-Paths @('GrunflexPOS2/Views/ExistenciaForegroundConverter.cs')
git add -u -- `
    GrunflexPOS2/MainWindow.xaml `
    GrunflexPOS2/MainWindow.xaml.cs `
    GrunflexPOS2/Views/AperturaCajaView.xaml `
    GrunflexPOS2/Views/AperturaCajaView.xaml.cs `
    GrunflexPOS2/Views/ReportesView.xaml `
    GrunflexPOS2/Views/ReportesView.xaml.cs
Commit-IfStaged 'chore(pos): reescritura base, migraciones SQLite y capa de aplicacion'

Write-Host "=== 01 Licensing abstractions ===" -ForegroundColor Cyan
Restore-Csproj
Set-AbsCsprojWithoutBcrypt
Stage-Paths @(
    'Grunflex.Licensing.Abstractions/Grunflex.Licensing.Abstractions.csproj',
    'Grunflex.Licensing.Abstractions/GrunflexLicenseCodec.cs',
    'Grunflex.Licensing.Abstractions/GrunflexLicensePayload.cs',
    'Grunflex.Licensing.Abstractions/GrunflexLicenseDefaults.cs',
    'Grunflex.Licensing.Abstractions/Idempotency'
)
Stage-Paths @('GrunflexPOS2/GrunflexPOS2.csproj')
Commit-IfStaged 'feat(licensing): abstracciones GFv2 compartidas'

Write-Host "=== 02 Licensing POS ===" -ForegroundColor Cyan
Stage-Tree 'GrunflexPOS2/Licensing'
Stage-Paths @('GrunflexPOS2/Domain/Abstractions/ILicenseStateProvider.cs')
Stage-Tree 'GrunflexPOS2/Services/Licensing'
Stage-Paths @(
    'GrunflexPOS2/Services/API/LicensingActivateApi.cs','GrunflexPOS2/Services/API/LicensingCloudService.cs',
    'GrunflexPOS2/Services/CloudBackupCoordinator.cs',
    'GrunflexPOS2/Views/ActivacionLicenciaModo.cs','GrunflexPOS2/Views/ActivacionLicenciaWindow.xaml','GrunflexPOS2/Views/ActivacionLicenciaWindow.xaml.cs',
    'GrunflexPOS2/Views/LicenciaView.xaml','GrunflexPOS2/Views/LicenciaView.xaml.cs',
    'GrunflexPOS2/Views/InsertarLicenciaWindow.xaml','GrunflexPOS2/Views/InsertarLicenciaWindow.xaml.cs',
    'GrunflexPOS2/Views/OpcionesHabilitadasView.xaml','GrunflexPOS2/Views/OpcionesHabilitadasView.xaml.cs',
    'GrunflexPOS2/Views/RespaldoNubeView.xaml','GrunflexPOS2/Views/RespaldoNubeView.xaml.cs',
    'GrunflexPOS2/Data/AppConfig.cs','GrunflexPOS2/App.xaml.cs','GrunflexPOS2/Views/CajaView.xaml.cs',
    'MVP-CLOUD-LICENCIAS-30DIAS.md'
)
Commit-IfStaged 'feat(licensing): enforcement POS, gracia offline y sync cloud'

Write-Host "=== 03 Licensing API + Issuer ===" -ForegroundColor Cyan
Stage-Paths @('GrunflexPOS2.slnx')
Stage-Tree 'Grunflex.LicenseIssuer' @('bin','obj')
Stage-Tree 'GrunflexPOS.LicenseManager' @('bin','obj')
Stage-Paths @(
    'GrunflexPOS.API/GrunflexPOS.API.csproj',
    'GrunflexPOS.API/Program.cs',
    'GrunflexPOS.API/Properties',
    'GrunflexPOS.API/Configuration',
    'GrunflexPOS.API/Controllers/LicensingController.cs',
    'GrunflexPOS.API/Controllers/LicenseIssuerController.cs',
    'GrunflexPOS.API/Controllers/IssuerActivationsController.cs',
    'GrunflexPOS.API/Controllers/IssuerClientsController.cs',
    'GrunflexPOS.API/Services/LicensingIssueService.cs',
    'GrunflexPOS.API/Services/LicenseSlotService.cs',
    'GrunflexPOS.API/Security/MulticajaLicenseModuleFilter.cs',
    'GrunflexPOS.API/Middleware/LicenseIssuerApiKeyMiddleware.cs',
    'GrunflexPOS.API/Models/LicenseIssuerRecord.cs',
    'GrunflexPOS.API/Models/IssuerActivationRecord.cs',
    'GrunflexPOS.API/DTOs/LicensingActivateRequest.cs',
    'GrunflexPOS.API/DTOs/LicensingTokenResponse.cs',
    'GrunflexPOS.API/DTOs/LicensingStatusResponse.cs',
    'GrunflexPOS.API/DTOs/LicenseIssuerUpsertRequest.cs',
    'GrunflexPOS.API/DTOs/LicenseIssuerRecordResponse.cs',
    'GrunflexPOS.API/DTOs/LicenseIssuerStatsResponse.cs',
    'GrunflexPOS.API/Data/ApiDbContext.cs',
    'GrunflexPOS.API/Hosting/DatabaseSchemaInitializer.cs'
)
Commit-IfStaged 'feat(licensing): API emision, Issuer comercial y License Manager'

Write-Host "=== 04 BCrypt abstractions ===" -ForegroundColor Cyan
Restore-Csproj
Stage-Paths @(
    'Grunflex.Licensing.Abstractions/Grunflex.Licensing.Abstractions.csproj',
    'Grunflex.Licensing.Abstractions/Security',
    'docs/USUARIOS-BCRYPT.md'
)
Commit-IfStaged 'feat(security): PasswordHasher BCrypt en abstracciones'

Write-Host "=== 08 Multicaja infra POS ===" -ForegroundColor Cyan
Stage-Paths @('GrunflexPOS2/Data/MulticajaLanDefaults.cs','GrunflexPOS2/Data/MulticajaLocalWriteGuard.cs')
Stage-Tree 'GrunflexPOS2/Networking'
Stage-Tree 'GrunflexPOS2/Services/Connectivity'
Stage-Tree 'GrunflexPOS2/Services/Offline'
Stage-Tree 'GrunflexPOS2/Services/Idempotency'
Stage-Paths @(
    'GrunflexPOS2/Infrastructure/Setup/MulticajaInstallerConfigWriter.cs',
    'GrunflexPOS2/Infrastructure/Setup/TerminalConfigBootstrap.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaRuntime.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaStartupValidation.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaDiagnostics.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaRiskScanner.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaCapabilitiesClient.cs',
    'GrunflexPOS2/Services/Licensing/TerminalRegistrationClient.cs'
)
Commit-IfStaged 'feat(multicaja): infraestructura datos, red y runtime POS'

Write-Host "=== 09 Multicaja client POS ===" -ForegroundColor Cyan
Stage-Paths @(
    'GrunflexPOS2/Services/Multicaja/MulticajaOperacionesClient.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaShadowCatalogSync.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaOperacionesOfflineHandlers.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaVentaCommitOfflineHandler.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaOfflineEnqueue.cs',
    'GrunflexPOS2/Services/Multicaja/OfflineReplayService.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaInventoryWriter.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaTerminalAuditHelper.cs',
    'GrunflexPOS2/Services/Multicaja/MulticajaRealtimeServices.cs'
)
Stage-Tree 'GrunflexPOS2/Services/Multicaja/Bootstrap'
Stage-Tree 'GrunflexPOS2/Services/Multicaja/Sync'
Stage-Tree 'GrunflexPOS2/Services/Multicaja/Terminal'
Stage-Tree 'GrunflexPOS2/Services/Multicaja/Cache'
Stage-Tree 'GrunflexPOS2/Services/Multicaja/Events'
Stage-Tree 'GrunflexPOS2/Services/Multicaja/UI'
Stage-Paths @(
    'GrunflexPOS2/Views/ConectarMasCajasWindow.xaml','GrunflexPOS2/Views/ConectarMasCajasWindow.xaml.cs',
    'GrunflexPOS2/Views/ConectarServidorWindow.xaml','GrunflexPOS2/Views/ConectarServidorWindow.xaml.cs',
    'GrunflexPOS2/Views/TerminalBootstrapWindow.xaml','GrunflexPOS2/Views/TerminalBootstrapWindow.xaml.cs',
    'GrunflexPOS2/Views/CajasView.xaml','GrunflexPOS2/Views/CajasView.xaml.cs',
    'GrunflexPOS2/Views/DiagnosticoView.xaml','GrunflexPOS2/Views/DiagnosticoView.xaml.cs'
)
Commit-IfStaged 'feat(multicaja): cliente POS sync, offline queue y bootstrap'

Write-Host "=== 06 RBAC abstractions ===" -ForegroundColor Cyan
Stage-Paths @('Grunflex.Licensing.Abstractions/UsuarioRolPermisos.cs')
Stage-Tree 'GrunflexPOS2/Security'
Commit-IfStaged 'feat(rbac): reglas compartidas UsuarioRolPermisos'

Write-Host "=== 07 RBAC UI + API ===" -ForegroundColor Cyan
Stage-Paths @(
    'GrunflexPOS2/Views/ConfiguracionView.xaml','GrunflexPOS2/Views/ConfiguracionView.xaml.cs',
    'GrunflexPOS.API/Services/MulticajaPermisosHelper.cs',
    'GrunflexPOS.API/Services/MulticajaAnulacionProcessor.cs',
    'GrunflexPOS.API/Services/MulticajaAnulacionProcessor.Idempotency.cs',
    'GrunflexPOS.API/Services/MulticajaDevolucionProcessor.cs',
    'GrunflexPOS.API/Services/MulticajaDevolucionProcessor.Idempotency.cs',
    'GrunflexPOS.API/Services/MulticajaInventarioProcessor.cs',
    'GrunflexPOS.API/Services/MulticajaInventarioProcessor.Idempotency.cs'
)
Commit-IfStaged 'feat(rbac): gates UI y permisos API multicaja'

Write-Host "=== 05 BCrypt POS ===" -ForegroundColor Cyan
Stage-Paths @(
    'GrunflexPOS2/Services/UsuarioService.cs',
    'GrunflexPOS2/Models/Entities/Usuario.cs',
    'GrunflexPOS2/Views/CrearUsuarioInicialWindow.xaml','GrunflexPOS2/Views/CrearUsuarioInicialWindow.xaml.cs',
    'GrunflexPOS2/Views/BaseDatosView.xaml','GrunflexPOS2/Views/BaseDatosView.xaml.cs',
    'GrunflexPOS2/Views/LoginView.xaml','GrunflexPOS2/Views/LoginView.xaml.cs',
    'GrunflexPOS2/Views/LoginWindow.xaml','GrunflexPOS2/Views/LoginWindow.xaml.cs',
    'GrunflexPOS2/Views/CajerosView.xaml','GrunflexPOS2/Views/CajerosView.xaml.cs',
    'GrunflexPOS.LicenseManager/MainWindow.xaml.cs'
)
Commit-IfStaged 'feat(security): migracion BCrypt en POS y License Manager'

Write-Host "=== 10 Multicaja API + ops ===" -ForegroundColor Cyan
Stage-Tree 'GrunflexPOS.API' @('bin','obj')
Stage-Tree 'GrunflexPOS.API.Tests' @('bin','obj')
Stage-Tree 'docs'
Stage-Paths @('docker-compose.posedgedb.yml')
Stage-Tree 'ops'
Stage-Tree 'tools'
Stage-Tree 'deploy'
Stage-Tree 'db'
Stage-Tree '.github'
Stage-Tree 'agents'
Stage-Tree 'src'
Stage-Tree 'tests'
Commit-IfStaged 'feat(multicaja): API operacional, BCrypt API, documentacion y ops'

Write-Host "=== 12 UI theme ===" -ForegroundColor Cyan
Stage-Paths @(
    'GrunflexPOS2/Themes/Theme.xaml',
    'GrunflexPOS2/Assets/icons',
    'GrunflexPOS2/Assets/logo.png',
    'GrunflexPOS2/Assets/ventas_grid_background.png',
    'GrunflexPOS2/Assets/ventas_grid_watermark_logo.png',
    'GrunflexPOS2/Views/CajaResponsiveHelper.cs',
    'GrunflexPOS2/Views/PosGridHeightHelper.cs'
)
Commit-IfStaged 'feat(ui): tema, assets e identidad visual'

Write-Host "=== 13 UI fixes (opcional) ===" -ForegroundColor Cyan
# SalesService/InventoryService suelen quedar en commit 00 via Application/
Commit-IfStaged 'fix(ui): descuento stock al completar venta'

$leftover = git status --porcelain | Where-Object { $_ -notmatch 'PROMPT-ANALISIS-CHATGPT\.md' }
if ($leftover) {
    Write-Warning "Archivos sin commitear tras la secuencia:"
    $leftover | ForEach-Object { Write-Warning $_ }
    throw 'Quedan cambios fuera de la secuencia modular; revisa tools/git-commit-sequence.ps1'
}

Restore-Csproj
Write-Host "`n=== Historial ===" -ForegroundColor Cyan
git log --oneline -20
git status --short
