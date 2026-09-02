namespace GrunflexPOS.HardwareBridge.Configuration;

public sealed class HardwareBridgeOptions
{
    public const string SectionName = "HardwareBridge";

    public string? DevelopmentToken { get; set; }

    public int MaxPrintBytes { get; set; } = 1024 * 1024;

    public ScannerSerialDefaults Scanner { get; set; } = new();
}

public sealed class ScannerSerialDefaults
{
    // These values intentionally match GrunflexPOS2.Services.LectorCodigoService.
    public int BaudRate { get; set; } = 9600;
    public int DataBits { get; set; } = 8;
    public string Parity { get; set; } = "None";
    public string StopBits { get; set; } = "One";
    public string Handshake { get; set; } = "None";
}
