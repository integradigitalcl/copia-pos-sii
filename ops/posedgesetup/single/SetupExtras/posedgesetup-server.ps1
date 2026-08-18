param(
  [string] $PayloadRoot
)

$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) { Write-Host $msg }
function Ensure-Dir([string] $path) { if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null } }
function Read-JsonOrNull([string] $path) { try { if (Test-Path $path) { return Get-Content -Raw -Path $path | ConvertFrom-Json } } catch { } return $null }
function Write-Json([string] $path, $obj) { Ensure-Dir (Split-Path $path -Parent); ($obj | ConvertTo-Json -Depth 6) | Set-Content -Path $path -Encoding UTF8 }
function New-RandomHex([int] $bytes) { $b = New-Object byte[] $bytes; [System.Security.Cryptography.RandomNumberGenerator]::Fill($b); return ($b | ForEach-Object { $_.ToString("x2") }) -join "" }

function Find-PostgresBin([string] $prefix) {
  $candidates = @(
    (Join-Path $prefix "bin\\psql.exe"),
    (Join-Path $env:ProgramFiles "PostgreSQL\\17\\bin\\psql.exe"),
    (Join-Path $env:ProgramFiles "PostgreSQL\\16\\bin\\psql.exe"),
    (Join-Path $env:ProgramFiles "PostgreSQL\\15\\bin\\psql.exe")
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return (Split-Path $c -Parent) } }
  return $null
}

function Install-PostgresIfMissing([string] $installerExe, [string] $prefix, [string] $dataDir, [string] $serviceName, [string] $superPwd, [int] $port) {
  if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) { return }
  Ensure-Dir (Split-Path $dataDir -Parent)
  $args = @(
    "--mode", "unattended",
    "--unattendedmodeui", "minimal",
    "--superaccount", "postgres",
    "--superpassword", $superPwd,
    "--serverport", "$port",
    "--servicename", $serviceName,
    "--prefix", $prefix,
    "--datadir", $dataDir,
    "--enable_acledit", "1",
    "--disable-components", "pgAdmin,stackbuilder"
  )
  $p = Start-Process -FilePath $installerExe -ArgumentList $args -Wait -PassThru
  if ($p.ExitCode -ne 0) { throw "PostgreSQL install failed (exit=$($p.ExitCode))." }
}

function Exec-Psql([string] $psqlExe, [int] $port, [string] $db, [string] $pwd, [string] $sql) {
  $env:PGPASSWORD = $pwd
  try {
    & $psqlExe -h 127.0.0.1 -p "$port" -U postgres -d $db -v ON_ERROR_STOP=1 -c $sql | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "psql failed rc=$LASTEXITCODE" }
  } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Exec-PsqlFile([string] $psqlExe, [int] $port, [string] $db, [string] $pwd, [string] $filePath) {
  $env:PGPASSWORD = $pwd
  try {
    & $psqlExe -h 127.0.0.1 -p "$port" -U postgres -d $db -v ON_ERROR_STOP=1 -f $filePath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "psql -f failed rc=$LASTEXITCODE" }
  } finally { Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue }
}

function Write-AppSettings([string] $targetPath, [string] $connStr, [string] $opsKey) {
  $obj = @{
    Logging = @{ LogLevel = @{ Default = "Information"; "Microsoft.AspNetCore" = "Warning"; "Microsoft.EntityFrameworkCore.Database.Command" = "Warning" } }
    Edge = @{ AutoApplySchema = $false; AutoSeed = $false; OpsKey = $opsKey; SchemaPath = ""; SeedPath = "" }
    ConnectionStrings = @{ PosEdge = $connStr }
    Kestrel = @{ Endpoints = @{ Http = @{ Url = "http://0.0.0.0:5071" } } }
    AllowedHosts = "*"
  }
  Ensure-Dir (Split-Path $targetPath -Parent)
  ($obj | ConvertTo-Json -Depth 6) | Set-Content -Path $targetPath -Encoding UTF8
}

function Add-Firewall {
  cmd /c "netsh advfirewall firewall delete rule name=\"PosEdge API (5071)\" 1>NUL 2>NUL"
  cmd /c "netsh advfirewall firewall add rule name=\"PosEdge API (5071)\" dir=in action=allow protocol=TCP localport=5071 profile=any" | Out-Null
  cmd /c "netsh advfirewall firewall delete rule name=\"PosEdge Discovery (33279)\" 1>NUL 2>NUL"
  cmd /c "netsh advfirewall firewall add rule name=\"PosEdge Discovery (33279)\" dir=in action=allow protocol=UDP localport=33279 profile=any" | Out-Null
}

function Register-Service([string] $name, [string] $exePath) {
  cmd /c "sc.exe stop $name 1>NUL 2>NUL"
  cmd /c "sc.exe delete $name 1>NUL 2>NUL"
  cmd /c "sc.exe create $name binPath= `"`"$exePath`"`" start= auto DisplayName= `"`"$name`"`"" | Out-Null
  cmd /c "sc.exe failure $name reset= 60 actions= restart/5000/restart/5000/restart/5000" | Out-Null
  cmd /c "sc.exe start $name" | Out-Null
}

function Test-Health([string] $url) {
  try { return ((Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri $url).StatusCode -ge 200) } catch { return $false }
}

if ([string]::IsNullOrWhiteSpace($PayloadRoot)) { throw "PayloadRoot required." }

$cfgDir = Join-Path $env:ProgramData "PosEdge\\config"
Ensure-Dir $cfgDir
$secretsPath = Join-Path $cfgDir "server-secrets.json"
$secrets = Read-JsonOrNull $secretsPath

$apiDir = Join-Path $PayloadRoot "Api"
$workersDir = Join-Path $PayloadRoot "Workers"
$pgInstaller = Join-Path $PayloadRoot "Prerequisites\\postgresql-windows-x64.exe"
$schema = Join-Path $PayloadRoot "SetupExtras\\schema.sql"
$seed   = Join-Path $PayloadRoot "SetupExtras\\seed.sql"

$pgSvc = "PosEdgePostgres"
$pgPrefix = Join-Path $env:ProgramFiles "PostgreSQL\\15"
$pgData = Join-Path $env:ProgramData "PosEdge\\postgres\\data"
$pgSuperPwd = if ($secrets -and $secrets.pgSuperPwd) { [string]$secrets.pgSuperPwd } else { New-RandomHex 12 }
$dbPwd = if ($secrets -and $secrets.dbPwd) { [string]$secrets.dbPwd } else { New-RandomHex 12 }
$opsKey = if ($secrets -and $secrets.opsKey) { [string]$secrets.opsKey } else { New-RandomHex 16 }
$pgPort = if ($secrets -and $secrets.pgPort) { [int]$secrets.pgPort } else { 5432 }

Write-Step "Instalando y configurando servidor..."
Install-PostgresIfMissing -installerExe $pgInstaller -prefix $pgPrefix -dataDir $pgData -serviceName $pgSvc -superPwd $pgSuperPwd -port $pgPort

$pgBin = Find-PostgresBin -prefix $pgPrefix
if (-not $pgBin) { throw "psql.exe not found after install." }
$psql = Join-Path $pgBin "psql.exe"

Exec-Psql -psqlExe $psql -port $pgPort -db postgres -pwd $pgSuperPwd -sql "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = 'posedgedb') THEN CREATE DATABASE posedgedb; END IF; END $$;"
Exec-Psql -psqlExe $psql -port $pgPort -db postgres -pwd $pgSuperPwd -sql "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'posedgedb_user') THEN CREATE ROLE posedgedb_user LOGIN PASSWORD '$dbPwd'; END IF; END $$;"
Exec-Psql -psqlExe $psql -port $pgPort -db postgres -pwd $pgSuperPwd -sql "GRANT ALL PRIVILEGES ON DATABASE posedgedb TO posedgedb_user;"

Exec-PsqlFile -psqlExe $psql -port $pgPort -db posedgedb -pwd $pgSuperPwd -filePath $schema
Exec-PsqlFile -psqlExe $psql -port $pgPort -db posedgedb -pwd $pgSuperPwd -filePath $seed

$conn = "Host=127.0.0.1;Port=$pgPort;Database=posedgedb;Username=posedgedb_user;Password=$dbPwd;Pooling=true;Maximum Pool Size=50"
Write-AppSettings -targetPath (Join-Path $apiDir "appsettings.Production.json") -connStr $conn -opsKey $opsKey
Write-AppSettings -targetPath (Join-Path $workersDir "appsettings.Production.json") -connStr $conn -opsKey $opsKey

Add-Firewall
Register-Service -name "PosEdgeApi" -exePath (Join-Path $apiDir "PosEdge.Api.exe")
Register-Service -name "PosEdgeWorkers" -exePath (Join-Path $workersDir "PosEdge.Workers.exe")

Write-Json -path $secretsPath -obj @{ pgSvc=$pgSvc; pgPort=$pgPort; pgSuperPwd=$pgSuperPwd; dbPwd=$dbPwd; opsKey=$opsKey }

if (-not (Test-Health "http://127.0.0.1:5071/health/ready")) { throw "No se pudo validar servidor." }
Write-Step "Servidor multicaja listo."

