using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Grunflex.Idempotency;

/// <summary>SHA-256 sobre JSON canónico (claves ordenadas, sin cambios de casing).</summary>
public static class IdempotencyPayloadHasher
{
    private static readonly JsonSerializerOptions CanonicalOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string HashPayload<T>(T payload)
    {
        var json = SerializeCanonical(payload);
        return Sha256HexUtf8(json);
    }

    public static string HashJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return Sha256HexUtf8(string.Empty);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var canonical = SerializeCanonicalElement(doc.RootElement);
            return Sha256HexUtf8(canonical);
        }
        catch
        {
            return Sha256HexUtf8(json.Trim());
        }
    }

    public static string SerializeCanonical<T>(T payload) =>
        SerializeCanonicalElement(JsonSerializer.SerializeToElement(payload, CanonicalOptions));

    private static string SerializeCanonicalElement(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, element);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var prop in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    WriteCanonical(writer, prop.Name, prop.Value);
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                    WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, string name, JsonElement value)
    {
        writer.WritePropertyName(name);
        WriteCanonical(writer, value);
    }

    private static string Sha256HexUtf8(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
