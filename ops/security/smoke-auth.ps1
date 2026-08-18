param(
    [Parameter(Mandatory = $false)] [string] $BaseUrl = "https://localhost:7279",
    [Parameter(Mandatory = $false)] [string] $Username = "admin"
)

$ErrorActionPreference = "Stop"
[System.Net.ServicePointManager]::ServerCertificateValidationCallback = { $true }

$adminPass = (dotnet user-secrets list | Where-Object { $_ -like 'Security:AdminPassword*' } | ForEach-Object { ($_ -split ' = ',2)[1] })
if ([string]::IsNullOrWhiteSpace($adminPass)) {
    throw "Security:AdminPassword no configurado en user-secrets."
}

$loginBody = @{ username = $Username; password = $adminPass } | ConvertTo-Json
$login = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/login" -ContentType "application/json" -Body $loginBody
if (-not $login.accessToken) { throw "Login sin access token." }

$refreshBody = @{ refreshToken = $login.refreshToken } | ConvertTo-Json
$refresh = Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/auth/refresh" -ContentType "application/json" -Body $refreshBody
if (-not $refresh.accessToken) { throw "Refresh sin access token." }

$revokeBody = @{ refreshToken = $refresh.refreshToken } | ConvertTo-Json
$revoke = Invoke-WebRequest -Method Post -Uri "$BaseUrl/api/auth/revoke" -ContentType "application/json" -Headers @{ Authorization = "Bearer $($refresh.accessToken)" } -Body $revokeBody

Write-Host "LOGIN_OK=True"
Write-Host "REFRESH_OK=True"
Write-Host "REVOKE_HTTP=$($revoke.StatusCode)"
