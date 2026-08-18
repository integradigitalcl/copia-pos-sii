using System;
using System.ComponentModel;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace GrunflexPOS2.Services
{
    public static class CajonDineroService
    {
        // ESC/POS: ESC p m t1 t2
        private static readonly byte[] ComandoApertura = { 0x1B, 0x70, 0x00, 0x19, 0xFA };

        public static bool ProbarApertura(string modelo, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(modelo) || modelo.Contains("Ninguno", StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                if (modelo.StartsWith("COM", StringComparison.OrdinalIgnoreCase))
                {
                    AbrirPorPuertoSerial(modelo);
                    return true;
                }

                if (modelo.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
                {
                    EnviarRawAImpresora(modelo, ComandoApertura);
                    return true;
                }

                if (modelo.Equals("USB", StringComparison.OrdinalIgnoreCase) ||
                    modelo.StartsWith("Apertura a través de impresión", StringComparison.OrdinalIgnoreCase))
                {
                    string impresora = new ConfiguracionService().Get("impresora_nombre");
                    if (string.IsNullOrWhiteSpace(impresora))
                    {
                        error = "No hay impresora configurada en 'Impresora de tickets'.";
                        return false;
                    }
                    EnviarRawAImpresora(impresora, ComandoApertura);
                    return true;
                }

                // Modelos específicos (Epson/Star/Citizen/Ithaca/PostLine): imprimir comando al nombre seleccionado.
                EnviarRawAImpresora(modelo, ComandoApertura);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public static void IntentarAbrirEnCobroEfectivo()
        {
            try
            {
                var cfg = new ConfiguracionService();
                string modelo = cfg.Get("cajon_modelo");
                if (string.IsNullOrWhiteSpace(modelo) || modelo.Contains("Ninguno", StringComparison.OrdinalIgnoreCase))
                    return;
                ProbarApertura(modelo, out _);
            }
            catch
            {
                // No romper flujo de cobro.
            }
        }

        private static void AbrirPorPuertoSerial(string puerto)
        {
            using var sp = new SerialPort(puerto, 9600, Parity.None, 8, StopBits.One)
            {
                Handshake = Handshake.None
            };
            sp.Open();
            sp.Write(ComandoApertura, 0, ComandoApertura.Length);
        }

        private static void EnviarRawAImpresora(string printerName, byte[] bytes)
        {
            if (!RawPrinterHelper.SendBytesToPrinter(printerName, bytes))
                throw new InvalidOperationException($"No se pudo enviar comando al dispositivo '{printerName}'.");
        }

        private static class RawPrinterHelper
        {
            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private class DOCINFO
            {
                [MarshalAs(UnmanagedType.LPWStr)]
                public string pDocName = "Apertura Cajon";
                [MarshalAs(UnmanagedType.LPWStr)]
                public string pOutputFile = string.Empty;
                [MarshalAs(UnmanagedType.LPWStr)]
                public string pDataType = "RAW";
            }

            [DllImport("winspool.Drv", EntryPoint = "OpenPrinterW", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

            [DllImport("winspool.Drv", SetLastError = true)]
            private static extern bool ClosePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool StartDocPrinter(IntPtr hPrinter, int level, [In] DOCINFO di);

            [DllImport("winspool.Drv", SetLastError = true)]
            private static extern bool EndDocPrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", SetLastError = true)]
            private static extern bool StartPagePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", SetLastError = true)]
            private static extern bool EndPagePrinter(IntPtr hPrinter);

            [DllImport("winspool.Drv", SetLastError = true)]
            private static extern bool WritePrinter(IntPtr hPrinter, IntPtr pBytes, int dwCount, out int dwWritten);

            public static bool SendBytesToPrinter(string printerName, byte[] bytes)
            {
                if (bytes == null || bytes.Length == 0)
                    return false;

                IntPtr hPrinter = IntPtr.Zero;
                IntPtr unmanagedBytes = IntPtr.Zero;
                try
                {
                    if (!OpenPrinter(printerName, out hPrinter, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    var doc = new DOCINFO();
                    if (!StartDocPrinter(hPrinter, 1, doc))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    if (!StartPagePrinter(hPrinter))
                        throw new Win32Exception(Marshal.GetLastWin32Error());

                    unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                    Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);

                    bool ok = WritePrinter(hPrinter, unmanagedBytes, bytes.Length, out int written);

                    EndPagePrinter(hPrinter);
                    EndDocPrinter(hPrinter);
                    return ok && written == bytes.Length;
                }
                finally
                {
                    if (unmanagedBytes != IntPtr.Zero)
                        Marshal.FreeCoTaskMem(unmanagedBytes);
                    if (hPrinter != IntPtr.Zero)
                        ClosePrinter(hPrinter);
                }
            }
        }
    }
}
