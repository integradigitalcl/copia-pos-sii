using System.IO.Ports;

namespace GrunflexPOS.HardwareBridge.Hardware;

public sealed class DrawerService
{
    // Exact command used by GrunflexPOS2.Services.CajonDineroService.
    public static readonly byte[] OpenCommand = { 0x1B, 0x70, 0x00, 0x19, 0xFA };

    private readonly PrinterService _printerService;

    public DrawerService(PrinterService printerService)
    {
        _printerService = printerService;
    }

    public void OpenByCom(string port)
    {
        var validatedPort = HardwareValidation.RequireComPort(port);
        using var serial = new SerialPort(
            validatedPort, 9600, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            WriteTimeout = 1000
        };
        serial.Open();
        serial.Write(OpenCommand, 0, OpenCommand.Length);
    }

    public void OpenByPrinter(string printerName) =>
        _printerService.PrintRaw(printerName, OpenCommand);
}
