using System.Globalization;
using System.Text.RegularExpressions;

namespace GrunflexPOS.Web.Services;

/// <summary>
/// Parses barcodes printed by scale label printers (precio/peso embebido).
/// Typical EAN-13: prefix + PLU + 5-digit value + check digit.
/// </summary>
public static partial class ScaleLabelBarcodeParser
{
    public sealed record ParseResult(
        string ProductLookup,
        decimal? QuantityKg,
        decimal? EmbeddedUnitPrice,
        string Mode);

    public static bool TryParse(
        string barcode,
        string prefix,
        bool priceLabels,
        bool weightLabels,
        out ParseResult? result)
    {
        result = null;
        if (!priceLabels && !weightLabels)
            return false;

        var digits = DigitsOnly().Replace(barcode ?? string.Empty, string.Empty);
        prefix = DigitsOnly().Replace(prefix ?? string.Empty, string.Empty);
        if (string.IsNullOrWhiteSpace(digits) || string.IsNullOrWhiteSpace(prefix))
            return false;
        if (!digits.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        if (digits.Length < prefix.Length + 6)
            return false;

        // Drop EAN check digit when length suggests 12/13 digit retail code.
        var body = digits;
        if (body.Length is 12 or 13)
            body = body[..^1];

        if (body.Length < prefix.Length + 5)
            return false;

        var rest = body[prefix.Length..];
        // EAN-13 with prefix "2000" often yields 8 remaining digits (PLU4 + value4).
        // Classic "2"/"20" prefixes yield 5-digit embedded values.
        var valueLen = rest.Length switch
        {
            8 => 4,
            >= 9 => 5,
            _ => 5
        };
        if (rest.Length < valueLen + 1)
            return false;

        var valueRaw = rest[^valueLen..];
        if (!int.TryParse(valueRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var valueInt))
            return false;

        var plu = rest[..^valueLen];
        if (string.IsNullOrWhiteSpace(plu))
            return false;

        // Prefer matching product codes that include the configured prefix (e.g. 20001234).
        var lookupWithPrefix = prefix + plu.TrimStart('0');
        if (lookupWithPrefix.Length == prefix.Length)
            lookupWithPrefix = prefix + plu;
        var lookupBare = plu.TrimStart('0');
        if (string.IsNullOrWhiteSpace(lookupBare))
            lookupBare = plu;

        if (weightLabels && !priceLabels)
        {
            var kg = valueInt / 1000m;
            if (kg <= 0)
                return false;
            result = new ParseResult(lookupWithPrefix, kg, null, "peso");
            return true;
        }

        if (priceLabels && !weightLabels)
        {
            var price = valueInt; // pesos enteros (CLP típico en etiquetas)
            if (price <= 0)
                return false;
            result = new ParseResult(lookupWithPrefix, 1m, price, "precio");
            return true;
        }

        // Both enabled: treat values >= 100 as price (CLP), smaller as weight in grams→kg
        // when ambiguous; prefer weight if value looks like grams (< 10000 and product is weighed).
        if (valueInt >= 1000)
        {
            result = new ParseResult(lookupWithPrefix, 1m, valueInt, "precio");
            return true;
        }

        var weightKg = valueInt / 1000m;
        if (weightKg > 0)
        {
            result = new ParseResult(lookupWithPrefix, weightKg, null, "peso");
            return true;
        }

        return false;
    }

    /// <summary>Alternate lookups to try after the primary ProductLookup.</summary>
    public static IEnumerable<string> LookupCandidates(ParseResult parsed, string prefix)
    {
        yield return parsed.ProductLookup;
        var digits = DigitsOnly().Replace(parsed.ProductLookup, string.Empty);
        prefix = DigitsOnly().Replace(prefix ?? string.Empty, string.Empty);
        if (digits.StartsWith(prefix, StringComparison.Ordinal) && digits.Length > prefix.Length)
        {
            var bare = digits[prefix.Length..].TrimStart('0');
            if (!string.IsNullOrWhiteSpace(bare))
                yield return bare;
            yield return digits[prefix.Length..];
        }
        else if (digits.Length > 0)
        {
            yield return digits.TrimStart('0');
        }
    }

    [GeneratedRegex(@"\D")]
    private static partial Regex DigitsOnly();
}
