using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace GrunflexPOS.API.Configuration;

/// <summary>
/// En el primer arranque crea <c>api.secrets.json</c> con clave JWT y contraseña de admin aleatorias
/// (solo en disco del usuario; no van en el instalador).
/// </summary>
public static class ApiLocalSecretsBootstrap
{
    private static readonly object SecretsFileGate = new();

    public const string FileName = "api.secrets.json";

    public static string GetSecretsFilePath() => ApiPaths.SecretsFilePath;

    /// <summary>Registra el JSON como fuente de configuración (debe llamarse antes de leer JwtOptions).</summary>
    public static void EnsureFileAndRegister(WebApplicationBuilder builder)
    {
        lock (SecretsFileGate)
        {
            EnsureFileAndRegisterCore(builder);
        }
    }

    private static void EnsureFileAndRegisterCore(WebApplicationBuilder builder)
    {
        var path = GetSecretsFilePath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        if (File.Exists(path))
        {
            try
            {
                var txt = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(txt))
                    File.Delete(path);
                else
                    JsonDocument.Parse(txt);
            }
            catch
            {
                try
                {
                    var bak = $"{path}.invalid.{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
                    File.Move(path, bak, overwrite: true);
                }
                catch
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        /* último recurso: AddJsonFile fallará si sigue roto */
                    }
                }
            }
        }

        if (!File.Exists(path))
        {
            var signingKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            var adminPassword = GenerarPasswordLegible(22);
            var payload = new
            {
                Jwt = new { SigningKey = signingKey },
                Security = new { AdminPassword = adminPassword }
            };
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }

        EnsureLicensingSectionInSecretsFile();

        builder.Configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
    }

    /// <summary>
    /// Añade IssuerApiKey al mismo archivo que JWT (primera vez o instalaciones antiguas).
    /// NUNCA genera un keypair RSA propio en este PC: la clave pública del editor viene
    /// embebida en appsettings.json al construir el instalador. Si se generase un keypair
    /// aleatorio aquí, ninguna licencia firmada por el editor sería verificable en este PC
    /// (el bug histórico de "Firma RSA inválida" en cajas adicionales).
    /// </summary>
    public static void EnsureLicensingSectionInSecretsFile()
    {
        lock (SecretsFileGate)
        {
            EnsureLicensingSectionInSecretsFileCore();
        }
    }

    private static void EnsureLicensingSectionInSecretsFileCore()
    {
        var path = GetSecretsFilePath();
        if (!File.Exists(path))
            return;

        var text = File.ReadAllText(path);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(text)?.AsObject() ?? new JsonObject();
        }
        catch
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        var pubExportPath = string.IsNullOrEmpty(dir)
            ? null
            : Path.Combine(dir, "licensing-public.pem");

        // Si ya existe sección Licensing en secrets (PC del editor, viene del LicenseIssuer),
        // respetar y exportar pub a disco para consistencia.
        if (root["Licensing"] is JsonObject licExisting)
        {
            if (licExisting["IssuerApiKey"] == null)
                licExisting["IssuerApiKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

            var pub = licExisting["PublicKeyPem"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(pub) && !string.IsNullOrEmpty(pubExportPath))
                File.WriteAllText(pubExportPath, pub);

            File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        // En cajas adicionales NO debe generarse un keypair RSA local: solo verifican licencias
        // firmadas por el editor, usando la pub que vino embebida en appsettings.json del POS.
        // Simplemente generamos un IssuerApiKey (HMAC interno) por si alguna funcionalidad lo necesita.
        var issuerKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        root["Licensing"] = new JsonObject
        {
            ["IssuerApiKey"] = issuerKey
        };
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        // Si el PEM en disco existe pero corresponde a un keypair viejo generado por una
        // versión anterior de la API, lo borramos para que el POS caiga al appsettings.json
        // (la pub del editor) en lugar de validar contra una pubkey ajena.
        if (!string.IsNullOrEmpty(pubExportPath) && File.Exists(pubExportPath))
        {
            try { File.Delete(pubExportPath); }
            catch { /* best-effort: si no podemos borrarlo, el POS lo prioriza al embedido igual */ }
        }
    }

    private static string GenerarPasswordLegible(int length)
    {
        const string alfabeto = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        var bytes = RandomNumberGenerator.GetBytes(length);
        var chars = new char[length];
        for (var i = 0; i < length; i++)
            chars[i] = alfabeto[bytes[i] % alfabeto.Length];
        return new string(chars);
    }
}
