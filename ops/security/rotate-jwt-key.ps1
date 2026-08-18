param(
    [Parameter(Mandatory = $false)] [string] $ApiProjectPath = ".\GrunflexPOS.API\GrunflexPOS.API.csproj"
)

$ErrorActionPreference = "Stop"

$oldKey = (dotnet user-secrets --project $ApiProjectPath list | Where-Object { $_ -like 'Jwt:SigningKey*' } | ForEach-Object { ($_ -split ' = ',2)[1] })
if ([string]::IsNullOrWhiteSpace($oldKey)) {
    throw "Jwt:SigningKey actual no encontrado."
}

$newKey = [Convert]::ToBase64String((1..64 | ForEach-Object { Get-Random -Maximum 256 }))

dotnet user-secrets --project $ApiProjectPath set "Jwt:PreviousSigningKeys:0" "$oldKey" | Out-Null
dotnet user-secrets --project $ApiProjectPath set "Jwt:SigningKey" "$newKey" | Out-Null

Write-Host "JWT key rotated."
Write-Host "New key suffix: ...$($newKey.Substring($newKey.Length-8))"
