namespace GrunflexPOS.HardwareBridge.Models;

public sealed record PrinterInfo(string Name);

public sealed record SerialPortInfo(string Name);

public sealed record PrintJobRequest(string Printer, string DataBase64);

public sealed record PrintTextRequest(
    string Printer,
    string Text,
    string? LogoBase64 = null,
    bool? LogoMonochrome = null);

public sealed record PrintTicketRequest(
    string Printer,
    string Text,
    string? LogoBase64 = null,
    int? PaperWidthMm = null,
    bool? LogoMonochrome = null);

public sealed record DrawerOpenRequest(string Mode, string Device);

public sealed record ScannerConnectRequest(
    string Port,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null,
    string? Handshake = null);

public sealed record ScannerStateResponse(bool Connected, string? Port, string? Error = null);

public sealed record ScannerEvent(
    string Type,
    string? Code = null,
    string? Port = null,
    string? Error = null,
    DateTimeOffset? At = null);

public sealed record ScaleReadRequest(
    string Port,
    string? Driver = null,
    int? BaudRate = null,
    int? DataBits = null,
    string? Parity = null,
    string? StopBits = null);

public sealed record ScaleReadResponse(
    bool Success,
    decimal? Kilograms,
    string? Raw,
    string? Port,
    string? Driver,
    string? Error);
