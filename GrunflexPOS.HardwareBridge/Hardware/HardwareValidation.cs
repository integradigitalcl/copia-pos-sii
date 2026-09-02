using System.IO.Ports;
using System.Text.RegularExpressions;

namespace GrunflexPOS.HardwareBridge.Hardware;

public static partial class HardwareValidation
{
    [GeneratedRegex(@"^COM[1-9][0-9]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComPortPattern();

    public static string RequireComPort(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !ComPortPattern().IsMatch(value.Trim()))
            throw new ArgumentException("A valid COM port is required.", nameof(value));

        var port = value.Trim();
        if (!SerialPort.GetPortNames().Any(x =>
                string.Equals(x, port, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The serial port '{port}' is not available.");
        }

        return port;
    }

    public static string RequireDeviceName(string? value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "The device name is required and must be at most 255 characters.",
                parameterName);
        }

        return value.Trim();
    }

    public static int RequireBaudRate(int value) =>
        value is 1200 or 2400 or 4800 or 9600 or 19200 or 38400 or 57600 or 115200
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Unsupported baud rate.");

    public static int RequireDataBits(int value) =>
        value is >= 5 and <= 8
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), "Data bits must be 5 through 8.");

    public static T ParseEnum<T>(string? value, T defaultValue) where T : struct, Enum =>
        string.IsNullOrWhiteSpace(value)
            ? defaultValue
            : Enum.TryParse<T>(value, true, out var parsed)
                ? parsed
                : throw new ArgumentException($"Invalid {typeof(T).Name} value '{value}'.");
}
