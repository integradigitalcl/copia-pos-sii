using System.ComponentModel;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.InteropServices;
using System.Text;

namespace GrunflexPOS.HardwareBridge.Hardware;

public sealed class PrinterService
{
    public IReadOnlyList<string> ListPrinters() =>
        PrinterSettings.InstalledPrinters
            .Cast<string>()
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public void PrintRaw(string printerName, byte[] bytes)
    {
        var requested = RequireInstalledPrinter(printerName);
        RawPrinterWriter.Send(requested, bytes);
    }

    public void PrintText(string printerName, string text, byte[]? logoPng = null, bool logoMonochrome = true,
        int paperWidthMm = 80)
    {
        var requested = RequireInstalledPrinter(printerName);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("The print job cannot be empty.", nameof(text));

        using var document = new PrintDocument();
        document.PrinterSettings = new PrinterSettings { PrinterName = requested };
        if (!document.PrinterSettings.IsValid)
            throw new InvalidOperationException($"The printer '{requested}' is not valid.");

        // 58mm: márgenes mínimos y alinear a la izquierda (evita el hueco izquierdo que recorta la derecha).
        // 80mm/hoja: márgenes moderados y columna centrada (drivers A4/letter).
        var narrow = paperWidthMm <= 58;
        var margin = narrow ? 4 : 40;
        document.DefaultPageSettings.Margins = new Margins(margin, margin, margin, margin);

        Image? logoImage = logoPng is { Length: > 0 }
            ? TicketLogoProcessor.CreatePrintBitmap(logoPng, logoMonochrome,
                narrow ? 180f : TicketLogoProcessor.MaxLogoWidthPx,
                narrow ? 40f : TicketLogoProcessor.MaxLogoHeightPx)
            : null;

        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineIndex = 0;
        var logoDrawn = false;
        document.PrintPage += (_, e) =>
        {
            using var font = new Font("Consolas", narrow ? 7f : 9f);
            var graphics = e.Graphics ?? throw new InvalidOperationException("Printer graphics are unavailable.");
            graphics.PageUnit = GraphicsUnit.Display;

            var pageLeft = e.MarginBounds.Left;
            var pageWidth = e.MarginBounds.Width;
            float contentWidth;
            float contentLeft;
            if (narrow)
            {
                contentWidth = pageWidth;
                contentLeft = pageLeft;
            }
            else
            {
                // Constrain content to receipt width and center that column on the page
                // (Brother A4/letter drivers otherwise center a huge logo on the full sheet).
                contentWidth = Math.Min(TicketLogoProcessor.TicketContentWidthPx, pageWidth);
                contentLeft = pageLeft + (pageWidth - contentWidth) / 2f;
            }
            float top = e.MarginBounds.Top;
            var bottom = e.MarginBounds.Bottom;
            var lineHeight = font.GetHeight(graphics);

            if (!logoDrawn && logoImage is not null)
            {
                var maxLogoWidth = Math.Min(narrow ? 180f : TicketLogoProcessor.MaxLogoWidthPx, contentWidth);
                var scale = Math.Min(maxLogoWidth / logoImage.Width,
                    (narrow ? 40f : TicketLogoProcessor.MaxLogoHeightPx) / logoImage.Height);
                var width = Math.Max(1f, logoImage.Width * scale);
                var height = Math.Max(1f, logoImage.Height * scale);
                // Align with ticket text: flush left of the receipt column.
                graphics.DrawImage(logoImage, contentLeft, top, width, height);
                top += height + (narrow ? 4f : 8f);
                logoDrawn = true;
            }

            while (lineIndex < lines.Length && top + lineHeight <= bottom)
            {
                graphics.DrawString(lines[lineIndex], font, Brushes.Black, contentLeft, top);
                top += lineHeight;
                lineIndex++;
            }

            e.HasMorePages = lineIndex < lines.Length;
        };

        try
        {
            document.Print();
        }
        finally
        {
            logoImage?.Dispose();
        }
    }

    public void PrintEscPosTicket(string printerName, string text, byte[]? logoPng, int paperWidthMm = 80,
        bool logoMonochrome = true)
    {
        using var buffer = new MemoryStream();
        buffer.WriteByte(0x1B);
        buffer.WriteByte(0x40);
        // GS L — margen izquierdo 0 (algunas térmicas 58mm arrastran margen de fábrica).
        buffer.WriteByte(0x1D);
        buffer.WriteByte(0x4C);
        buffer.WriteByte(0x00);
        buffer.WriteByte(0x00);
        if (paperWidthMm <= 58)
        {
            // ESC M 1 — Font B (más angosta) para que quepan ~32 columnas en 58mm.
            buffer.WriteByte(0x1B);
            buffer.WriteByte(0x4D);
            buffer.WriteByte(0x01);
        }
        if (logoPng is { Length: > 0 })
        {
            var raster = EscPosImageEncoder.EncodePng(logoPng, paperWidthMm, logoMonochrome);
            if (raster.Length > 0)
                buffer.Write(raster);
            buffer.WriteByte(0x0A);
        }

        buffer.Write(Encoding.ASCII.GetBytes(text.Replace("\r\n", "\r", StringComparison.Ordinal)));
        buffer.WriteByte(0x1D);
        buffer.WriteByte(0x56);
        buffer.WriteByte(0x00);
        PrintRaw(printerName, buffer.ToArray());
    }

    private static string RequireInstalledPrinter(string printerName)
    {
        var requested = HardwareValidation.RequireDeviceName(printerName, nameof(printerName));
        if (!PrinterSettings.InstalledPrinters.Cast<string>()
                .Any(x => string.Equals(x, requested, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"The printer '{requested}' is not installed.");
        }

        return requested;
    }

    private static class RawPrinterWriter
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private sealed class DocInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string DocumentName = "GrunflexPOS ESC/POS";

            [MarshalAs(UnmanagedType.LPWStr)]
            public string OutputFile = string.Empty;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string DataType = "RAW";
        }

        [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", SetLastError = true,
            CharSet = CharSet.Unicode)]
        private static extern bool OpenPrinter(string printerName, out IntPtr handle, IntPtr defaults);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool ClosePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool StartDocPrinter(IntPtr handle, int level, DocInfo documentInfo);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndDocPrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool StartPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool EndPagePrinter(IntPtr handle);

        [DllImport("winspool.drv", SetLastError = true)]
        private static extern bool WritePrinter(
            IntPtr handle, IntPtr bytes, int count, out int written);

        public static void Send(string printerName, byte[] bytes)
        {
            if (bytes.Length == 0)
                throw new ArgumentException("The print job cannot be empty.", nameof(bytes));

            IntPtr printer = IntPtr.Zero;
            IntPtr unmanagedBytes = IntPtr.Zero;
            var documentStarted = false;
            var pageStarted = false;
            try
            {
                if (!OpenPrinter(printerName, out printer, IntPtr.Zero))
                    ThrowLastWin32Error("OpenPrinter");

                if (!StartDocPrinter(printer, 1, new DocInfo()))
                    ThrowLastWin32Error("StartDocPrinter");
                documentStarted = true;

                if (!StartPagePrinter(printer))
                    ThrowLastWin32Error("StartPagePrinter");
                pageStarted = true;

                unmanagedBytes = Marshal.AllocCoTaskMem(bytes.Length);
                Marshal.Copy(bytes, 0, unmanagedBytes, bytes.Length);
                if (!WritePrinter(printer, unmanagedBytes, bytes.Length, out var written) ||
                    written != bytes.Length)
                {
                    ThrowLastWin32Error("WritePrinter");
                }
            }
            finally
            {
                if (pageStarted)
                    EndPagePrinter(printer);
                if (documentStarted)
                    EndDocPrinter(printer);
                if (unmanagedBytes != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(unmanagedBytes);
                if (printer != IntPtr.Zero)
                    ClosePrinter(printer);
            }
        }

        private static void ThrowLastWin32Error(string operation) =>
            throw new Win32Exception(
                Marshal.GetLastWin32Error(), $"{operation} failed.");
    }
}
