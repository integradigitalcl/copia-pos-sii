$ErrorActionPreference = "Stop"
$ProgressPreference    = "SilentlyContinue"

function Write-Step([string] $msg) {
  Write-Host $msg
}

function New-RandomHex([int] $bytes) {
  $b = New-Object byte[] $bytes
  [System.Security.Cryptography.RandomNumberGenerator]::Fill($b)
  return ($b | ForEach-Object { $_.ToString("x2") }) -join ""
}

function Ensure-Dir([string] $path) {
  if (-not (Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
}

function Read-JsonOrNull([string] $path) {
  try {
    if (-not (Test-Path $path)) { return $null }
    return Get-Content -Raw -Path $path | ConvertFrom-Json -ErrorAction Stop
  } catch { return $null }
}

function Write-Json([string] $path, $obj) {
  $json = $obj | ConvertTo-Json -Depth 6
  Set-Content -Path $path -Value $json -Encoding UTF8
}

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
  if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    Write-Step "PostgreSQL service already present: $serviceName"
    return
  }

  if (-not (Test-Path $installerExe)) {
    throw "PostgreSQL installer not found: $installerExe"
  }

  Write-Step "Installing PostgreSQL (unattended)..."
  New-Item -ItemType Directory -Path (Split-Path $dataDir -Parent) -Force | Out-Null

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
  if ($p.ExitCode -ne 0) {
    throw "PostgreSQL install failed (exit=$($p.ExitCode))."
  }
}

function Exec-Psql([string] $psqlExe, [string] $host, [int] $port, [string] $db, [string] $user, [string] $pwd, [string] $sql) {
  $env:PGPASSWORD = $pwd
  try {
    $args = @("-h", $host, "-p", "$port", "-U", $user, "-d", $db, "-v", "ON_ERROR_STOP=1", "-c", $sql)
    & $psqlExe @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "psql failed rc=$LASTEXITCODE" }
  }
  finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  }
}

function Exec-PsqlFile([string] $psqlExe, [string] $host, [int] $port, [string] $db, [string] $user, [string] $pwd, [string] $filePath) {
  if (-not (Test-Path $filePath)) { throw "SQL file not found: $filePath" }
  $env:PGPASSWORD = $pwd
  try {
    $args = @("-h", $host, "-p", "$port", "-U", $user, "-d", $db, "-v", "ON_ERROR_STOP=1", "-f", $filePath)
    & $psqlExe @args | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "psql -f failed rc=$LASTEXITCODE" }
  }
  finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
  }
}

function Write-ApiConfig([string] $targetPath, [string] $connStr, [string] $opsKey) {
  $json = @"
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "Edge": {
    "AutoApplySchema": false,
    "SchemaPath": "",
    "AutoSeed": false,
    "SeedPath": "",
    "OpsKey": "$opsKey"
  },
  "ConnectionStrings": {
    "PosEdge": "$connStr"
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://0.0.0.0:5071"
      }
    }
  },
  "AllowedHosts": "*"
}
"@
  Set-Content -Path $targetPath -Value $json -Encoding UTF8
}

function Add-FirewallRule5071 {
  Write-Step "Configuring firewall TCP 5071..."
  cmd /c "netsh advfirewall firewall delete rule name=\"PosEdge API (5071)\" 1>NUL 2>NUL"
  cmd /c "netsh advfirewall firewall add rule name=\"PosEdge API (5071)\" dir=in action=allow protocol=TCP localport=5071 profile=any" | Out-Null
}

function Add-FirewallRuleDiscovery {
  Write-Step "Configuring firewall UDP 33279 (discovery)..."
  cmd /c "netsh advfirewall firewall delete rule name=\"PosEdge Discovery (33279)\" 1>NUL 2>NUL"
  cmd /c "netsh advfirewall firewall add rule name=\"PosEdge Discovery (33279)\" dir=in action=allow protocol=UDP localport=33279 profile=any" | Out-Null
}

function Register-Service([string] $name, [string] $exePath, [string] $workDir) {
  Write-Step "Registering Windows service: $name"
  cmd /c "sc.exe stop $name 1>NUL 2>NUL"
  cmd /c "sc.exe delete $name 1>NUL 2>NUL"
  $bin = "`"$exePath`""
  cmd /c "sc.exe create $name binPath= $bin start= auto DisplayName= `"$name`"" | Out-Null
  cmd /c "sc.exe failure $name reset= 60 actions= restart/5000/restart/5000/restart/5000" | Out-Null
  cmd /c "sc.exe start $name" | Out-Null
}

function Test-Health([string] $url) {
  try {
    $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 5 -Uri $url
    return ($r.StatusCode -ge 200 -and $r.StatusCode -lt 300)
  } catch { return $false }
}

Write-Step "=== PosEdge Server Setup ==="

$installRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$appRoot = Resolve-Path (Join-Path $installRoot "..") | Select-Object -ExpandProperty Path
$apiDir = Join-Path $appRoot "Api"
$workersDir = Join-Path $appRoot "Workers"

$cfgDir   = Join-Path $env:ProgramData "PosEdge\\config"
Ensure-Dir $cfgDir
$secretsPath = Join-Path $cfgDir "server-secrets.json"
$secrets = Read-JsonOrNull $secretsPath

$pgPrefix = Join-Path $env:ProgramFiles "PostgreSQL\\15"
$pgData   = Join-Path $env:ProgramData "PosEdge\\postgres\\data"
$pgSvc    = "PosEdgePostgres"
$pgInstaller = Join-Path $appRoot "Prerequisites\\postgresql-windows-x64.exe"

$pgSuperPwd = if ($secrets -and $secrets.pgSuperPwd) { [string]$secrets.pgSuperPwd } else { New-RandomHex 12 }
$dbName = "posedgedb"
$dbUser = "posedgedb_user"
$dbPwd  = if ($secrets -and $secrets.dbPwd) { [string]$secrets.dbPwd } else { New-RandomHex 12 }
$opsKey = if ($secrets -and $secrets.opsKey) { [string]$secrets.opsKey } else { New-RandomHex 16 }

$pgPort  = if ($secrets -and $secrets.pgPort) { [int]$secrets.pgPort } else { 5432 }
try {
  $tnc = Test-NetConnection -ComputerName "127.0.0.1" -Port 5432 -WarningAction SilentlyContinue
  if ($tnc.TcpTestSucceeded -and -not (Get-Service -Name $pgSvc -ErrorAction SilentlyContinue)) {
    # 5432 is occupied by something else; avoid conflict by choosing 5433.
    $pgPort = 5433
  }
} catch { }

Install-PostgresIfMissing -installerExe $pgInstaller -prefix $pgPrefix -dataDir $pgData -serviceName $pgSvc -superPwd $pgSuperPwd -port $pgPort

$pgBin = Find-PostgresBin -prefix $pgPrefix
if (-not $pgBin) { throw "Could not locate psql.exe after install." }
$psql = Join-Path $pgBin "psql.exe"

Write-Step "Bootstrapping DB..."
Exec-Psql -psqlExe $psql -host "127.0.0.1" -port $pgPort -db "postgres" -user "postgres" -pwd $pgSuperPwd -sql "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = '$dbName') THEN CREATE DATABASE $dbName; END IF; END $$;"
Exec-Psql -psqlExe $psql -host "127.0.0.1" -port $pgPort -db "postgres" -user "postgres" -pwd $pgSuperPwd -sql "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '$dbUser') THEN CREATE ROLE $dbUser LOGIN PASSWORD '$dbPwd'; END IF; END $$;"
Exec-Psql -psqlExe $psql -host "127.0.0.1" -port $pgPort -db "postgres" -user "postgres" -pwd $pgSuperPwd -sql "GRANT ALL PRIVILEGES ON DATABASE $dbName TO $dbUser;"

$schema = Resolve-Path (Join-Path $appRoot "ServerExtras\\schema.sql") -ErrorAction SilentlyContinue
$seed   = Resolve-Path (Join-Path $appRoot "ServerExtras\\seed.sql") -ErrorAction SilentlyContinue
if (-not $schema) { throw "schema.sql missing from installer payload." }
if (-not $seed) { throw "seed.sql missing from installer payload." }

Exec-PsqlFile -psqlExe $psql -host "127.0.0.1" -port $pgPort -db $dbName -user "postgres" -pwd $pgSuperPwd -filePath $schema.Path
Exec-PsqlFile -psqlExe $psql -host "127.0.0.1" -port $pgPort -db $dbName -user "postgres" -pwd $pgSuperPwd -filePath $seed.Path

Write-Step "Writing appsettings.Production.json..."
$conn = "Host=127.0.0.1;Port=$pgPort;Database=$dbName;Username=$dbUser;Password=$dbPwd;Pooling=true;Maximum Pool Size=50"
Write-ApiConfig -targetPath (Join-Path $apiDir "appsettings.Production.json") -connStr $conn -opsKey $opsKey
Write-ApiConfig -targetPath (Join-Path $workersDir "appsettings.Production.json") -connStr $conn -opsKey $opsKey

Add-FirewallRule5071
Add-FirewallRuleDiscovery

Register-Service -name "PosEdgeApi" -exePath (Join-Path $apiDir "PosEdge.Api.exe") -workDir $apiDir
Register-Service -name "PosEdgeWorkers" -exePath (Join-Path $workersDir "PosEdge.Workers.exe") -workDir $workersDir

Write-Json -path $secretsPath -obj @{
  pgSvc      = $pgSvc
  pgPort     = $pgPort
  pgPrefix   = $pgPrefix
  pgData     = $pgData
  pgSuperPwd = $pgSuperPwd
  dbName     = $dbName
  dbUser     = $dbUser
  dbPwd      = $dbPwd
  opsKey     = $opsKey
}

Write-Step "Validating health..."
Start-Sleep -Seconds 2
if (-not (Test-Health "http://127.0.0.1:5071/health/live")) { throw "health/live failed" }
if (-not (Test-Health "http://127.0.0.1:5071/health/ready")) { throw "health/ready failed" }

Write-Step "OK: Servidor multicaja listo."

