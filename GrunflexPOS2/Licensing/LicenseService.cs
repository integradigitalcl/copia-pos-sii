using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Grunflex.Licensing;
using GrunflexPOS2.Data;
using GrunflexPOS2.Services;

namespace GrunflexPOS2.Licensing;

public static class LicenseService
{
    // Legado HMAC (solo compatibilidad). Producción: GFv2 RSA.
    private const string SigningSecret = "GRUNFLEX_POS_LICENSE_SIGNING_SECRET_V1_CHANGE_ME";

    // OJO con la simetría con MainViewModel.cs del LicenseIssuer (camino legacy HMAC):
    //   var json = JsonSerializer.Serialize(payload);           // ← sin opciones = PascalCase
    //   var base64 = Base64UrlEncode(Encoding.UTF8.GetBytes(json));
    // Si acá usamos CamelCase, ninguna propiedad matchea ("ExpUtc" vs "expUtc"), y todas
    // caen al default del tipo. Resultado: ExpUtc = DateTime.MinValue → "La licencia está
    // vencida" en una licencia recién generada. Por eso PropertyNameCaseInsensitive=true,
    // que matchea ambos casing y nos deja compatibles con encoders viejos y nuevos.
    private static readonly JsonSerializerOptions JsonLegacy = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static bool TryApplyStoredLicense(ConfiguracionService cfg, out string mensaje)
    {
        var status = EvaluateStored(cfg, out mensaje);
        return status == LicenseStatus.Valid;
    }

    public static LicenseStatus EvaluateStored(ConfiguracionService cfg, out string mensaje)
    {
        string licencia = cfg.Get("licencia_key")?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(licencia))
        {
            ClearEntitlements();
            mensaje = "No hay licencia cargada.";
            return LicenseStatus.Missing;
        }

        if (TryParseExpUtc(cfg.Get("licencia_exp_utc"), out var expStored) && expStored <= DateTime.UtcNow)
        {
            ClearEntitlements();
            mensaje = "La licencia está vencida.";
            return LicenseStatus.Expired;
        }

        if (TryValidate(licencia, out var payload, out mensaje))
        {
            ProductEntitlements.Apply(payload.Multicaja, payload.OnlineSupport, payload.CloudBackup, payload.PrioritySupport);
            SetIfChanged(cfg, "licencia_key", licencia.Trim());
            PersistPayloadMetadata(cfg, payload);
            mensaje = "Licencia válida.";
            return LicenseStatus.Valid;
        }

        ClearEntitlements();
        if (mensaje.Contains("vencida", StringComparison.OrdinalIgnoreCase))
            return LicenseStatus.Expired;

        return LicenseStatus.Invalid;
    }

    private static bool TryParseExpUtc(string? raw, out DateTime expUtc)
    {
        expUtc = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!DateTime.TryParse(raw, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d))
            return false;

        expUtc = d.Kind == DateTimeKind.Unspecified
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc)
            : d.ToUniversalTime();
        return true;
    }

    public static bool TryValidateAndApply(string licencia, out string mensaje)
    {
        return TryValidateAndApply(licencia, new ConfiguracionService(), out mensaje);
    }

    public static bool TryValidateAndApply(string licencia, ConfiguracionService cfg, out string mensaje)
    {
        if (TryValidate(licencia, out var payload, out mensaje))
        {
            ProductEntitlements.Apply(payload.Multicaja, payload.OnlineSupport, payload.CloudBackup, payload.PrioritySupport);
            cfg.Set("licencia_key", licencia.Trim());
            PersistPayloadMetadata(cfg, payload);
            return true;
        }

        ClearEntitlements();
        return false;
    }

    private static void ClearEntitlements()
    {
        ProductEntitlements.Apply(multicaja: false, onlineSupport: false, cloudBackup: false, prioritySupport: false);
    }

    /// <summary>Elimina licencia local (por ejemplo la marca el servidor como vencida o revocada).</summary>
    public static void ClearStoredLicense(ConfiguracionService cfg)
    {
        cfg.Set("licencia_key", string.Empty);
        cfg.Set("licencia_activation_id", string.Empty);
        cfg.Set("licencia_exp_utc", string.Empty);
        cfg.Set("licencia_validada_utc", string.Empty);
        cfg.Set("licencia_last_cloud_ok_utc", string.Empty);
        cfg.Set("licencia_offline_grace_days", string.Empty);
        ClearEntitlements();
    }

    /// <summary>Resuelve días de gracia offline: token &gt; config local &gt; appsettings POS.</summary>
    public static int ResolveOfflineGraceDays(int payloadDays)
    {
        if (payloadDays > 0)
            return payloadDays;

        var cfg = new ConfiguracionService();
        var stored = cfg.Get("licencia_offline_grace_days");
        if (int.TryParse(stored, out var fromConfig) && fromConfig > 0)
            return fromConfig;

        return AppConfig.Cargar().LicensingOfflineGraceDays;
    }

    private static void PersistPayloadMetadata(ConfiguracionService cfg, GrunflexLicensePayload payload)
    {
        SetIfChanged(cfg, "licencia_exp_utc", payload.ExpUtc.ToString("O"));
        if (!string.IsNullOrWhiteSpace(payload.ActivationId))
            SetIfChanged(cfg, "licencia_activation_id", payload.ActivationId.Trim());
        if (string.IsNullOrWhiteSpace(cfg.Get("licencia_validada_utc")))
            SetIfChanged(cfg, "licencia_validada_utc", DateTime.UtcNow.ToString("O"));
        SetIfChanged(
            cfg,
            "licencia_offline_grace_days",
            payload.OfflineGraceDays > 0 ? payload.OfflineGraceDays.ToString() : string.Empty);
        if (payload.NumberOfBoxes > 0)
            SetIfChanged(cfg, "licencia_number_of_boxes", payload.NumberOfBoxes.ToString());
    }

    private static void SetIfChanged(ConfiguracionService cfg, string clave, string valor)
    {
        var actual = cfg.Get(clave) ?? string.Empty;
        if (string.Equals(actual.Trim(), valor.Trim(), StringComparison.Ordinal))
            return;

        cfg.Set(clave, valor);
    }

    private static bool TryValidate(string licencia, out GrunflexLicensePayload payload, out string mensaje)
    {
        payload = new GrunflexLicensePayload();
        if (string.IsNullOrWhiteSpace(licencia))
        {
            mensaje = "Licencia vacía.";
            return false;
        }

        var t = licencia.Trim();
        if (t.StartsWith(GrunflexLicenseCodec.VersionPrefix + ".", StringComparison.Ordinal))
            return TryValidateV2(t, out payload, out mensaje);

        return TryValidateLegacyHmac(t, out payload, out mensaje);
    }

    private static bool TryValidateV2(string token, out GrunflexLicensePayload payload, out string mensaje)
    {
        payload = new GrunflexLicensePayload();
        var pem = TryLoadPublicKeyPem();
        if (string.IsNullOrWhiteSpace(pem))
        {
            pem = TryDownloadAndStorePublicKeyPemFromApi();
        }
        if (string.IsNullOrWhiteSpace(pem))
        {
            mensaje =
                "Licencia GFv2 sin clave pública local. Ejecute la API una vez (genera licensing-public.pem) o defina Licensing:PublicKeyPem.";
            return false;
        }

        try
        {
            using var rsa = GrunflexLicenseCodec.ImportPublicKeyFromPem(pem);
            if (!GrunflexLicenseCodec.TryVerifyV2(token, rsa, out var pl, out mensaje) || pl == null)
                return false;

            payload = pl;
            return ValidatePayloadRules(payload, out mensaje);
        }
        catch (Exception ex)
        {
            mensaje = "No se pudo cargar la clave pública: " + ex.Message;
            return false;
        }
    }

    private static bool TryValidateLegacyHmac(string licencia, out GrunflexLicensePayload payload, out string mensaje)
    {
        payload = new GrunflexLicensePayload();
        string[] partes = licencia.Split('.');
        if (partes.Length != 2)
        {
            mensaje = "Formato de licencia inválido.";
            return false;
        }

        string payloadB64 = partes[0].Trim();
        string firmaHex = partes[1].Trim();

        string firmaEsperada = ComputeSignatureHex(payloadB64);
        if (!FixedTimeEqualsHex(firmaHex, firmaEsperada))
        {
            mensaje = "Firma de licencia inválida.";
            return false;
        }

        try
        {
            byte[] payloadBytes = FromBase64Url(payloadB64);
            var legacy = JsonSerializer.Deserialize<GrunflexLicensePayload>(payloadBytes, JsonLegacy) ?? new GrunflexLicensePayload();
            payload = legacy;
        }
        catch
        {
            mensaje = "No se pudo leer el contenido de la licencia.";
            return false;
        }

        return ValidatePayloadRules(payload, out mensaje);
    }

    private static bool ValidatePayloadRules(GrunflexLicensePayload payload, out string mensaje)
    {
        if (payload.ExpUtc <= DateTime.UtcNow)
        {
            mensaje = "La licencia está vencida.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(payload.Machine) &&
            !string.Equals(payload.Machine.Trim(), Environment.MachineName, StringComparison.OrdinalIgnoreCase))
        {
            mensaje = "Esta licencia no corresponde a este equipo.";
            return false;
        }

        if (!payload.Multicaja && !payload.OnlineSupport && !payload.CloudBackup && !payload.PrioritySupport)
        {
            mensaje = "La licencia no habilita módulos.";
            return false;
        }

        mensaje = "Licencia válida.";
        return true;
    }

    /// <summary>Indica si hay material para verificar licencias GFv2 sin depender de red.</summary>
    public static bool HasLocalPublicKeyMaterial(ConfiguracionService? cfg = null)
    {
        if (!string.IsNullOrWhiteSpace(TryLoadPublicKeyPem()))
            return true;

        cfg ??= new ConfiguracionService();
        var key = cfg.Get("licencia_key")?.Trim() ?? string.Empty;
        return !string.IsNullOrEmpty(key) &&
               !key.StartsWith(GrunflexLicenseCodec.VersionPrefix + ".", StringComparison.Ordinal);
    }

    private static string? TryLoadPublicKeyPem()
    {
        // PRIORIDAD 1: la pubkey embebida en appsettings.json del POS (la que firmó el
        // editor al construir el instalador). Esta es la fuente de verdad: garantiza que
        // toda licencia emitida por el editor sea verificable, independientemente de lo
        // que la API local haya generado en este PC.
        var embedded = TryReadPublicKeyPemFromJsonFiles();
        if (!string.IsNullOrWhiteSpace(embedded))
            return embedded;

        // PRIORIDAD 2: archivo en disco (legacy / fallback). Lo usaba la API local que
        // auto-genera keypair propio, lo cual rompía la verificación en cajas adicionales.
        // Se conserva como respaldo para instalaciones donde appsettings.json no trae pub.
        var path = Path.Combine(LocalDatabasePaths.DataDirectory, "licensing-public.pem");
        if (File.Exists(path))
        {
            try
            {
                return File.ReadAllText(path).Trim();
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static string? TryReadPublicKeyPemFromJsonFiles()
    {
        try
        {
            var basePath = AppContext.BaseDirectory;
            foreach (var name in new[] { "appsettings.local.json", "appsettings.json" })
            {
                var full = Path.Combine(basePath, name);
                if (!File.Exists(full))
                    continue;

                using var doc = JsonDocument.Parse(File.ReadAllText(full));
                if (!doc.RootElement.TryGetProperty("Licensing", out var lic))
                    continue;
                if (!lic.TryGetProperty("PublicKeyPem", out var pe))
                    continue;

                var s = pe.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        catch
        {
            return null;
        }

        return null;
    }

    private static string? TryDownloadAndStorePublicKeyPemFromApi()
    {
        try
        {
            var cfg = AppConfig.Cargar();
            if (string.IsNullOrWhiteSpace(cfg.ApiBaseUrl))
                return null;

            var url = cfg.ApiBaseUrl.TrimEnd('/') + "/api/licensing/public-key";
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
                return null;

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("publicKeyPem", out var pe))
                return null;

            var pem = pe.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(pem))
                return null;

            LocalDatabasePaths.EnsureDataDirectoryExists();
            var path = Path.Combine(LocalDatabasePaths.DataDirectory, "licensing-public.pem");
            File.WriteAllText(path, pem);
            return pem;
        }
        catch
        {
            return null;
        }
    }

    private static string ComputeSignatureHex(string payloadBase64Url)
    {
        byte[] secretBytes = Encoding.UTF8.GetBytes(SigningSecret);
        byte[] dataBytes = Encoding.UTF8.GetBytes(payloadBase64Url);
        using var hmac = new HMACSHA256(secretBytes);
        byte[] hash = hmac.ComputeHash(dataBytes);
        return Convert.ToHexString(hash);
    }

    private static bool FixedTimeEqualsHex(string a, string b)
    {
        try
        {
            byte[] ba = Convert.FromHexString(a.ToUpperInvariant());
            byte[] bb = Convert.FromHexString(b.ToUpperInvariant());
            return CryptographicOperations.FixedTimeEquals(ba, bb);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] FromBase64Url(string input)
    {
        string s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }

        return Convert.FromBase64String(s);
    }
}
