using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using GrunflexPOS.HardwareBridge.Models;

namespace GrunflexPOS.HardwareBridge.Hardware;

public sealed partial class ScaleService
{
    public ScaleReadResponse ReadWeight(
        string port,
        string driver,
        int baudRate,
        int dataBits,
        Parity parity,
        StopBits stopBits)
    {
        var validatedPort = HardwareValidation.RequireComPort(port);
        HardwareValidation.RequireBaudRate(baudRate);
        HardwareValidation.RequireDataBits(dataBits);
        driver = string.IsNullOrWhiteSpace(driver) ? "GENERICA" : driver.Trim().ToUpperInvariant();

        using var serial = new SerialPort(validatedPort, baudRate, parity, dataBits, stopBits)
        {
            Encoding = Encoding.ASCII,
            ReadTimeout = 800,
            WriteTimeout = 500,
            NewLine = "\r\n"
        };
        serial.Open();

        try
        {
            // Some scales respond to a poll; ignore failures for continuous-output devices.
            try
            {
                serial.DiscardInBuffer();
                serial.Write(driver switch
                {
                    "TORREY" => "P\r",
                    "TOLEDO" => "W\r",
                    _ => "P\r\n"
                });
            }
            catch
            {
                // continuous stream scales may not accept commands
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(900);
            var buffer = new StringBuilder();
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var chunk = serial.ReadExisting();
                    if (!string.IsNullOrEmpty(chunk))
                        buffer.Append(chunk);
                    if (TryParseWeight(buffer.ToString(), driver, out var kg, out var raw))
                        return new ScaleReadResponse(true, kg, raw, validatedPort, driver, null);
                }
                catch (TimeoutException)
                {
                    // keep waiting until deadline
                }

                Thread.Sleep(40);
            }

            if (TryParseWeight(buffer.ToString(), driver, out var finalKg, out var finalRaw))
                return new ScaleReadResponse(true, finalKg, finalRaw, validatedPort, driver, null);

            return new ScaleReadResponse(
                false, null, buffer.ToString(), validatedPort, driver,
                "No se pudo leer un peso válido desde la báscula.");
        }
        finally
        {
            if (serial.IsOpen)
                serial.Close();
        }
    }

    public static bool TryParseWeight(string payload, string driver, out decimal kg, out string raw)
    {
        kg = 0;
        raw = payload?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        foreach (Match match in WeightToken().Matches(raw))
        {
            var numberText = match.Groups[1].Value.Replace(',', '.');
            if (!decimal.TryParse(numberText, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
                continue;

            var unit = match.Groups[2].Success ? match.Groups[2].Value.ToLowerInvariant() : "kg";
            kg = unit switch
            {
                "g" or "gr" or "gram" or "grams" => value / 1000m,
                "lb" or "lbs" => value * 0.45359237m,
                _ => value
            };

            if (kg > 0 && kg < 500)
            {
                raw = match.Value.Trim();
                return true;
            }
        }

        // TORREY/TOLEDO often embed ST,GS,+0001.234 or similar without unit suffix.
        var signed = SignedWeight().Match(raw);
        if (signed.Success &&
            decimal.TryParse(signed.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var signedKg) &&
            signedKg > 0 && signedKg < 500)
        {
            kg = signedKg;
            raw = signed.Value.Trim();
            return true;
        }

        _ = driver;
        return false;
    }

    [GeneratedRegex(@"([+-]?\d+(?:[.,]\d+)?)\s*(kg|g|gr|grams?|lb|lbs)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WeightToken();

    [GeneratedRegex(@"[+-]?\s*(\d+[.,]\d{1,4})", RegexOptions.CultureInvariant)]
    private static partial Regex SignedWeight();
}
