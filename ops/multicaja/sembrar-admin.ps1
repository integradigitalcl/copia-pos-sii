# Crea usuario admin en la BD correcta (data\grunflex.db) si no existe.
param(
    [string] $Usuario = "claudio",
    [string] $Password = "demo1234",
    [string] $Nombre = "cajero N°1"
)

$ErrorActionPreference = "Stop"
$db = Join-Path $env:ProgramData "GrunflexPOS\data\grunflex.db"
if (-not (Test-Path $db)) {
    Write-Host "No existe BD: $db"
    exit 1
}

$proj = Join-Path $PSScriptRoot "..\..\tools\Multicaja.SimTest\Multicaja.SimTest.csproj"
$seedTool = Join-Path $PSScriptRoot "..\..\tools\Multicaja.SimTest\SeedAdminTool.cs"

# Inline dotnet script via temporary console
$code = @"
using System;
using Microsoft.Data.Sqlite;
var db = @""" + $db.Replace('\','\\') + @""";
using var c = new SqliteConnection($"Data Source={db};Cache=Shared");
c.Open();
using (var cmd = c.CreateCommand()) {
  cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Usuarios'";
  var t = cmd.ExecuteScalar()?.ToString();
  if (t == null) { Console.WriteLine("NO_TABLE"); return; }
}
using (var cmd = c.CreateCommand()) {
  cmd.CommandText = "SELECT COUNT(*) FROM Usuarios WHERE Username = `$u";
  cmd.Parameters.AddWithValue("`$u", "$Usuario");
  if (Convert.ToInt32(cmd.ExecuteScalar()) > 0) { Console.WriteLine("EXISTS"); return; }
}
var id = Guid.NewGuid().ToString("D");
using (var cmd = c.CreateCommand()) {
  cmd.CommandText = "INSERT INTO Usuarios (Id, Nombre, Username, Rol, Password) VALUES (`$id, `$n, `$u, 'Admin', `$p)";
  cmd.Parameters.AddWithValue("`$id", id);
  cmd.Parameters.AddWithValue("`$n", "$Nombre");
  cmd.Parameters.AddWithValue("`$u", "$Usuario");
  cmd.Parameters.AddWithValue("`$p", "$Password");
  cmd.ExecuteNonQuery();
}
Console.WriteLine("CREATED");
"@

$tmp = Join-Path $env:TEMP "grunflex-seed-admin"
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
@'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.11" /></ItemGroup>
</Project>
'@ | Set-Content (Join-Path $tmp "SeedAdmin.csproj")
$code | Set-Content (Join-Path $tmp "Program.cs")
Push-Location $tmp
dotnet run -c Release
$ec = $LASTEXITCODE
Pop-Location
Remove-Item $tmp -Recurse -Force -EA SilentlyContinue
exit $ec
