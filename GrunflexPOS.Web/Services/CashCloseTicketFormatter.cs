using System.Globalization;
using System.Text;

namespace GrunflexPOS.Web.Services;

/// <summary>Formatea el comprobante de cierre de caja para térmica 80mm o 58mm.</summary>
public static class CashCloseTicketFormatter
{
    private static readonly CultureInfo Chilean = CultureInfo.GetCultureInfo("es-CL");

    public static string Format(CashCloseReport report, int columns = 42, int paperWidthMm = 80)
    {
        var maxCols = paperWidthMm <= 58 ? 32 : 48;
        columns = Math.Clamp(columns, 20, maxCols);
        var narrow = paperWidthMm <= 58;
        var sb = new StringBuilder();
        void Line(string text = "") => sb.AppendLine(text);
        void Center(string text) => Line(CenterText(text, columns));
        void Rule(char ch = '-') => Line(new string(ch, columns));
        void Row(string label, string value)
        {
            label = Truncate(label, columns - 1);
            value = Truncate(value, columns);
            if (narrow && label.Length + value.Length + 1 > columns)
            {
                Line(label);
                Line(value.PadLeft(columns));
                return;
            }

            var spaces = Math.Max(1, columns - label.Length - value.Length);
            Line(label + new string(' ', spaces) + value);
        }

        Center("GRÜNFLEX");
        Center("POINT OF SALES");
        Rule('=');
        Center(narrow ? "CIERRE DE CAJA" : "COMPROBANTE DE CIERRE DE CAJA");
        Rule('=');
        Line();
        Center(narrow ? "TURNO" : "INFORMACIÓN DEL TURNO");
        Rule();
        Row("Fecha", report.DateLocal.ToString("dd/MM/yyyy", Chilean));
        Row("Caja", report.RegisterName);
        Row("Cajero", report.CashierName);
        Row("Apertura", report.OpenedAtLocal.ToString("HH:mm", Chilean));
        Row("Cierre", report.ClosedAtLocal.ToString("HH:mm", Chilean));
        Row(narrow ? "Duración" : "Duración del turno", FormatDuration(report.Duration));
        Line();
        Center(narrow ? "VENTAS" : "RESUMEN DE VENTAS");
        Rule();
        Row(narrow ? "TOTAL" : "TOTAL DE VENTAS", Money(report.TotalSales));
        Row(narrow ? "Transacciones" : "Número de transacciones", report.Transactions.ToString(Chilean));
        Row(narrow ? "Promedio" : "Venta promedio", Money(report.AverageTicket));
        Line();
        Center(narrow ? "PAGOS" : "FORMAS DE PAGO");
        Rule();
        Row("Efectivo", Money(report.CashPayments));
        Row("Tarjetas", Money(report.CardPayments));
        Row(narrow ? "Transfer." : "Transferencias", Money(report.TransferPayments));
        if (report.OtherPayments > 0)
            Row("Otros", Money(report.OtherPayments));
        var paymentsTotal = report.CashPayments + report.CardPayments + report.TransferPayments + report.OtherPayments;
        Row(narrow ? "Total pagos" : "Total medios de pago", Money(paymentsTotal));
        Line();
        Center(narrow ? "CONSUMO PERS." : "CONSUMO PERSONAL");
        Rule();
        Row(narrow ? "Tickets" : "Tickets consumo", report.PersonalConsumptionCount.ToString(Chilean));
        Row(narrow ? "Total" : "Total consumo personal", Money(report.PersonalConsumptionTotal));
        Row(narrow ? "Impacto $" : "Impacto en efectivo", Money(0));
        Line();
        Center(narrow ? "IMPUESTOS" : "IMPUESTOS RECAUDADOS");
        Rule();
        Row(narrow ? $"IVA ({FormatRate(report.TaxRate)}%)" : $"IVA incluido ({FormatRate(report.TaxRate)}%)", Money(report.TaxCollected));
        Row(narrow ? "Netas" : "Ventas netas", Money(report.TaxableSales));
        Line();
        Center(narrow ? "EFECTIVO" : "CUADRATURA DE EFECTIVO");
        Rule();
        Row(narrow ? "Fondo" : "Fondo inicial", Money(report.OpeningAmount));
        Row(narrow ? "Ventas $" : "Efectivo por ventas", Money(report.CashFromSales));
        if (report.CashEntries > 0)
            Row(narrow ? "Ingresos" : "Ingresos / entradas", Money(report.CashEntries));
        Row(narrow ? "Retiros" : "Retiros / egresos", Money(report.CashExits));
        if (report.PersonalConsumptionTotal > 0)
            Row(narrow ? "Cons.pers." : "Consumo personal (sin $)", Money(report.PersonalConsumptionTotal));
        Row(narrow ? "Esperado" : "Efectivo esperado", Money(report.ExpectedCash));
        Row(narrow ? "Contado" : "Efectivo contado", Money(report.CountedCash));
        Rule();
        Row("DIFERENCIA", Money(report.Difference));
        if (report.CancelledSales > 0)
        {
            Line();
            Row("Anulaciones", Money(report.CancelledSales));
        }
        Line();
        Center("VALIDACIÓN");
        Rule();
        Line();
        if (narrow)
        {
            Line("Firma cajero");
            Line(new string('_', Math.Min(columns, 28)));
            Line();
            Line("Firma supervisor");
            Line(new string('_', Math.Min(columns, 28)));
        }
        else
        {
            Line(PadPair("Firma cajero", "Firma supervisor", columns));
            Line();
            Line(new string('_', Math.Min(16, columns / 2 - 1)) + new string(' ', Math.Max(2, columns - 32)) +
                 new string('_', Math.Min(16, columns / 2 - 1)));
        }
        Line();
        Center(narrow ? "GrünFlex POS Web" : "Cierre generado por GrünFlex POS Web");
        Center($"Folio: {report.Folio}");
        Center(narrow ? "Doc. interno" : "Documento interno - No válido como boleta");
        Line();
        Line();
        Line();
        return sb.ToString();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        var hours = (int)duration.TotalHours;
        var minutes = duration.Minutes;
        return $"{hours} h {minutes:00} min";
    }

    private static string FormatRate(decimal rate) =>
        rate == Math.Truncate(rate)
            ? rate.ToString("0", Chilean)
            : rate.ToString("0.####", Chilean);

    private static string Money(decimal amount) =>
        amount.ToString("C0", Chilean);

    private static string CenterText(string text, int columns)
    {
        text = Truncate(text, columns);
        if (text.Length >= columns)
            return text;
        var pad = columns - text.Length;
        var left = pad / 2;
        return new string(' ', left) + text;
    }

    private static string Truncate(string text, int max) =>
        string.IsNullOrEmpty(text) || text.Length <= max ? text : text[..max];

    private static string PadPair(string left, string right, int columns)
    {
        left = Truncate(left, columns / 2);
        right = Truncate(right, columns / 2);
        var spaces = Math.Max(1, columns - left.Length - right.Length);
        return left + new string(' ', spaces) + right;
    }
}
