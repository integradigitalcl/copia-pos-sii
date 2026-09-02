using System.Globalization;
using System.Text;

namespace GrunflexPOS.Web.Services;

public sealed class TicketTemplateSettings
{
    public string FontFamily { get; set; } = "Courier New";
    public int FontSize { get; set; } = 10;
    public int Columns { get; set; } = 36;
    public int PaperWidthMm { get; set; } = 80;
    public int TopBlankLines { get; set; }
    public int BottomBlankLines { get; set; } = 5;
    public bool NormalTotalsFont { get; set; }
    public bool BoldText { get; set; } = true;
    public bool IncludeUnitPrice { get; set; } = true;
    public bool ExtendedDescription { get; set; }
    public bool PrintCustomerData { get; set; }
    public bool PrintLogoMonochrome { get; set; } = true;
    public string Line1 { get; set; } = "Nombre de mi Negocio";
    public string Line2 { get; set; } = "Direccion 123 Col. Colonia";
    public string Line3 { get; set; } = "(555) 123 4567";
    public string Line4 { get; set; } = "RFC003128ZBI";
    public string Line5 { get; set; } = "Cant.  Descripcion               Importe";
    public string Line6 { get; set; } = "========================================";
    public string Line7 { get; set; } = "1     Agua Ciel 600ml            $7.00";
    public string Line8 { get; set; } = "1     Coca Cola Light            $8.00";
    public string Line9 { get; set; } = "1Kg   Tomate                    $10.00";
    public string Line10 { get; set; } = "No. de Articulos: {ARTICULOS}";
    public string Line11 { get; set; } = "Total: {TOTAL}";
    public string Line12 { get; set; } = "Gracias por su compra";
    public string Line13 { get; set; } = "www.eleventa.com";

    public IReadOnlyDictionary<string, string> ToSettingsDictionary() => new Dictionary<string, string>
    {
        ["ticket_fuente"] = FontFamily,
        ["ticket_tamano"] = FontSize.ToString(CultureInfo.InvariantCulture),
        ["ticket_columnas"] = Columns.ToString(CultureInfo.InvariantCulture),
        ["ticket_ancho"] = PaperWidthMm.ToString(CultureInfo.InvariantCulture),
        ["ticket_ancho_mm"] = PaperWidthMm.ToString(CultureInfo.InvariantCulture),
        ["ticket_lineas_arriba"] = TopBlankLines.ToString(CultureInfo.InvariantCulture),
        ["ticket_lineas_abajo"] = BottomBlankLines.ToString(CultureInfo.InvariantCulture),
        ["ticket_totales_normal"] = NormalTotalsFont.ToString().ToLowerInvariant(),
        ["ticket_negrita"] = BoldText.ToString().ToLowerInvariant(),
        ["ticket_incluir_precio_unitario"] = IncludeUnitPrice.ToString().ToLowerInvariant(),
        ["ticket_precio_unitario"] = IncludeUnitPrice.ToString().ToLowerInvariant(),
        ["ticket_descripcion_extendida"] = ExtendedDescription.ToString().ToLowerInvariant(),
        ["ticket_datos_cliente"] = PrintCustomerData.ToString().ToLowerInvariant(),
        ["ticket_logo_bn"] = PrintLogoMonochrome.ToString().ToLowerInvariant(),
        ["ticket_linea_1"] = Line1,
        ["ticket_linea_2"] = Line2,
        ["ticket_linea_3"] = Line3,
        ["ticket_linea_4"] = Line4,
        ["ticket_linea_5"] = Line5,
        ["ticket_linea_6"] = Line6,
        ["ticket_linea_7"] = Line7,
        ["ticket_linea_8"] = Line8,
        ["ticket_linea_9"] = Line9,
        ["ticket_linea_10"] = Line10,
        ["ticket_linea_11"] = Line11,
        ["ticket_linea_12"] = Line12,
        ["ticket_linea_13"] = Line13
    };
}

public sealed class TicketTemplateService(LocalPosStore store)
{
    private static readonly CultureInfo Spanish = CultureInfo.GetCultureInfo("es-CL");

    public async Task<TicketTemplateSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        var includeUnit = await store.GetSettingAsync("ticket_incluir_precio_unitario", string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(includeUnit))
            includeUnit = await store.GetSettingAsync("ticket_precio_unitario", "true", cancellationToken);

        var width = await store.GetSettingAsync("ticket_ancho_mm", string.Empty, cancellationToken);
        if (string.IsNullOrWhiteSpace(width))
            width = await store.GetSettingAsync("ticket_ancho", "80", cancellationToken);

        return new TicketTemplateSettings
        {
            FontFamily = await store.GetSettingAsync("ticket_fuente", "Courier New", cancellationToken),
            FontSize = ParseInt(await store.GetSettingAsync("ticket_tamano", "10", cancellationToken), 10, 6, 24),
            Columns = ParseInt(await store.GetSettingAsync("ticket_columnas", "36", cancellationToken), 36, 20, 64),
            PaperWidthMm = ParseInt(width, 80) is 58 or 80 ? ParseInt(width, 80) : 80,
            TopBlankLines = ParseInt(await store.GetSettingAsync("ticket_lineas_arriba", "0", cancellationToken), 0, 0, 20),
            BottomBlankLines = ParseInt(await store.GetSettingAsync("ticket_lineas_abajo", "5", cancellationToken), 5, 0, 20),
            NormalTotalsFont = await IsTrueAsync("ticket_totales_normal", false, cancellationToken),
            BoldText = await IsTrueAsync("ticket_negrita", true, cancellationToken),
            IncludeUnitPrice = string.Equals(includeUnit, "true", StringComparison.OrdinalIgnoreCase),
            ExtendedDescription = await IsTrueAsync("ticket_descripcion_extendida", false, cancellationToken),
            PrintCustomerData = await IsTrueAsync("ticket_datos_cliente", false, cancellationToken),
            PrintLogoMonochrome = await IsTrueAsync("ticket_logo_bn", true, cancellationToken),
            Line1 = await LineAsync("ticket_linea_1", "Nombre de mi Negocio", cancellationToken),
            Line2 = await LineAsync("ticket_linea_2", "Direccion 123 Col. Colonia", cancellationToken),
            Line3 = await LineAsync("ticket_linea_3", "(555) 123 4567", cancellationToken),
            Line4 = await LineAsync("ticket_linea_4", "RFC003128ZBI", cancellationToken),
            Line5 = await LineAsync("ticket_linea_5", "Cant.  Descripcion               Importe", cancellationToken),
            Line6 = await LineAsync("ticket_linea_6", "========================================", cancellationToken),
            Line7 = await LineAsync("ticket_linea_7", "1     Agua Ciel 600ml            $7.00", cancellationToken),
            Line8 = await LineAsync("ticket_linea_8", "1     Coca Cola Light            $8.00", cancellationToken),
            Line9 = await LineAsync("ticket_linea_9", "1Kg   Tomate                    $10.00", cancellationToken),
            Line10 = await LineAsync("ticket_linea_10", "No. de Articulos: {ARTICULOS}", cancellationToken),
            Line11 = await LineAsync("ticket_linea_11", "Total: {TOTAL}", cancellationToken),
            Line12 = await LineAsync("ticket_linea_12", "Gracias por su compra", cancellationToken),
            Line13 = await LineAsync("ticket_linea_13", "www.eleventa.com", cancellationToken)
        };
    }

    public async Task SaveAsync(TicketTemplateSettings settings, CancellationToken cancellationToken = default)
    {
        await store.SetSettingsAsync(settings.ToSettingsDictionary(), cancellationToken);
    }

    public string BuildPreviewTicket(TicketTemplateSettings settings) =>
        BuildSaleTicket(settings, 1024, BuildPreviewItems(), 33m, "Público en general", DateTime.Now, includeSampleLines: true);

    public string BuildSaleTicket(
        TicketTemplateSettings settings,
        long ticketNumber,
        IReadOnlyCollection<CartItem> items,
        decimal total,
        string customer = "Público en general",
        DateTime? soldAt = null,
        bool includeSampleLines = false)
    {
        var cols = Math.Clamp(settings.Columns, 20, 64);
        var sb = new StringBuilder();
        var sold = soldAt ?? DateTime.Now;
        var itemCount = items.Sum(x => x.Quantity);
        var totalText = FormatMoney(total);
        var itemsText = itemCount.ToString("0.##", CultureInfo.InvariantCulture);

        for (var i = 0; i < settings.TopBlankLines; i++)
            sb.AppendLine();

        AppendIfNotEmpty(sb, settings.Line1, cols);
        AppendIfNotEmpty(sb, settings.Line2, cols);
        AppendIfNotEmpty(sb, settings.Line3, cols);
        AppendIfNotEmpty(sb, settings.Line4, cols);
        sb.AppendLine(Truncate($"Ticket N° {ticketNumber}", cols));
        sb.AppendLine(Truncate($"Fecha: {sold:dd/MM/yyyy HH:mm}", cols));
        sb.AppendLine();

        AppendIfNotEmpty(sb, settings.Line5, cols);
        AppendIfNotEmpty(sb, settings.Line6, cols);
        sb.AppendLine(Truncate(new string('-', Math.Min(cols, 48)), cols));

        if (includeSampleLines)
        {
            AppendIfNotEmpty(sb, settings.Line7, cols);
            AppendIfNotEmpty(sb, settings.Line8, cols);
            AppendIfNotEmpty(sb, settings.Line9, cols);
        }
        else
        {
            foreach (var item in items)
            {
                var unit = item.EffectiveUnitPrice;
                var name = settings.ExtendedDescription
                    ? item.Product.Name
                    : Truncate(item.Product.Name, Math.Max(12, cols - 16));
                var left = settings.IncludeUnitPrice
                    ? $"{item.Quantity:0.##} x {FormatMoney(unit)} {name}"
                    : $"{item.Quantity:0.##} x {name}";
                sb.AppendLine(Truncate(left, cols));
                sb.AppendLine(PadLeft(FormatMoney(unit * item.Quantity), cols));
            }
        }

        sb.AppendLine(Truncate(new string('-', Math.Min(cols, 48)), cols));
        sb.AppendLine(PadBetween("TOTAL:", totalText, cols));

        AppendIfNotEmpty(sb, RenderDynamic(settings.Line10, totalText, itemsText), cols);
        AppendIfNotEmpty(sb, RenderDynamic(settings.Line11, totalText, itemsText), cols);

        if (settings.PrintCustomerData && !string.IsNullOrWhiteSpace(customer))
            sb.AppendLine(Truncate($"Cliente: {customer}", cols));

        AppendIfNotEmpty(sb, settings.Line12, cols);
        AppendIfNotEmpty(sb, settings.Line13, cols);

        for (var i = 0; i < settings.BottomBlankLines; i++)
            sb.AppendLine();

        return sb.ToString().TrimEnd();
    }

    private static CartItem[] BuildPreviewItems() =>
    [
        new CartItem(new PosProduct(1, "001", "Agua Ciel 600ml", "Bebidas", 7, 1, "un.", "#2563EB"), 1),
        new CartItem(new PosProduct(2, "002", "Coca Cola Light", "Bebidas", 8, 1, "un.", "#2563EB"), 1),
        new CartItem(new PosProduct(3, "003", "Tomate", "Abarrotes", 10, 1, "kg", "#2563EB"), 1)
    ];

    private async Task<string> LineAsync(string key, string fallback, CancellationToken cancellationToken)
    {
        var value = await store.GetSettingAsync(key, fallback, cancellationToken);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private async Task<bool> IsTrueAsync(string key, bool fallback, CancellationToken cancellationToken)
    {
        var value = await store.GetSettingAsync(key, fallback ? "true" : "false", cancellationToken);
        return string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static int ParseInt(string? raw, int fallback, int min = int.MinValue, int max = int.MaxValue)
    {
        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            parsed = fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static string FormatMoney(decimal amount) =>
        amount.ToString("C0", Spanish);

    private static void AppendIfNotEmpty(StringBuilder sb, string? line, int columns)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;
        sb.AppendLine(Truncate(line, columns));
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max];

    private static string PadLeft(string text, int width) =>
        text.Length >= width ? text : text.PadLeft(width);

    private static string PadBetween(string left, string right, int width)
    {
        if (left.Length + right.Length >= width)
            return Truncate($"{left} {right}", width);
        return left + new string(' ', width - left.Length - right.Length) + right;
    }

    private static string RenderDynamic(string line, string total, string items)
    {
        if (string.IsNullOrWhiteSpace(line))
            return string.Empty;

        var text = line
            .Replace("{TOTAL}", total, StringComparison.OrdinalIgnoreCase)
            .Replace("{ARTICULOS}", items, StringComparison.OrdinalIgnoreCase);

        if (text.StartsWith("total:", StringComparison.OrdinalIgnoreCase))
            return $"Total: {total}";

        if (text.StartsWith("no. de articulos:", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("no de articulos:", StringComparison.OrdinalIgnoreCase))
            return $"No. de Articulos: {items}";

        return text;
    }
}
