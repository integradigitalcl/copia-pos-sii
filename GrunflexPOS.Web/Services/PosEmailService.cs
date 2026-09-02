using System.Net;
using System.Net.Mail;
using System.Text;

namespace GrunflexPOS.Web.Services;

public sealed class PosEmailService(
    LocalPosStore store,
    BoletaPdfService boletaPdf,
    ILogger<PosEmailService> logger)
{
    public sealed record SmtpSettings(
        bool Enabled,
        string Email,
        string Password,
        string Host,
        int Port,
        bool Ssl);

    public async Task<SmtpSettings?> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken))
            return null;

        var email = (await store.GetSettingAsync("correo_email", cancellationToken: cancellationToken)).Trim();
        var password = await store.GetSettingAsync("correo_clave", cancellationToken: cancellationToken);
        var host = await ResolveHostAsync(cancellationToken);
        var port = await ResolvePortAsync(cancellationToken);
        var ssl = await ResolveSslAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password) || string.IsNullOrWhiteSpace(host))
            return null;

        return new SmtpSettings(true, email, password, host, port, ssl);
    }

    public async Task<(bool Ok, string Message)> SendTestAsync(CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        if (settings is null)
            return (false, "Complete correo, clave y servidor SMTP antes de probar.");

        return await SendAsync(
            settings,
            settings.Email,
            "Prueba de correo - Grunflex POS Web",
            "Este es un correo de prueba enviado desde Grunflex POS Web.",
            attachmentPath: null,
            cancellationToken);
    }

    public async Task TryNotifySaleAsync(
        long ticketNumber,
        IReadOnlyCollection<CartItem> items,
        decimal total,
        string userName,
        string paymentMethod,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            if (settings is null)
                return;

            string? pdfPath = null;
            try
            {
                pdfPath = boletaPdf.Generate(ticketNumber, items, total, openInExplorer: false);
                if (!File.Exists(pdfPath))
                    pdfPath = null;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "No se pudo generar PDF para correo de venta {Ticket}", ticketNumber);
            }

            var body = BuildSaleBody(ticketNumber, items, total, userName, paymentMethod);
            var result = await SendAsync(
                settings,
                settings.Email,
                $"Boleta Ticket #{ticketNumber}",
                body,
                pdfPath,
                cancellationToken);

            if (!result.Ok)
                logger.LogWarning("Correo post-venta ticket {Ticket} falló: {Message}", ticketNumber, result.Message);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Notificación por correo omitida para ticket {Ticket}", ticketNumber);
        }
    }

    public async Task<(bool Ok, string Message)> SendAsync(
        SmtpSettings settings,
        string to,
        string subject,
        string body,
        string? attachmentPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(to))
            return (false, "Destinatario vacío.");

        try
        {
            using var smtp = new SmtpClient(settings.Host, settings.Port)
            {
                Credentials = new NetworkCredential(settings.Email, settings.Password),
                EnableSsl = settings.Ssl,
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            using var mail = new MailMessage
            {
                From = new MailAddress(settings.Email),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            mail.To.Add(to);

            if (!string.IsNullOrWhiteSpace(attachmentPath) && File.Exists(attachmentPath))
                mail.Attachments.Add(new Attachment(attachmentPath));

            await smtp.SendMailAsync(mail, cancellationToken);
            return (true, "Correo enviado correctamente.");
        }
        catch (SmtpException ex)
        {
            return (false, "Error SMTP: " + ex.Message);
        }
        catch (Exception ex)
        {
            return (false, ex.GetBaseException().Message);
        }
    }

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        string.Equals(
            await store.GetSettingAsync("correo_activo", cancellationToken: cancellationToken),
            "true",
            StringComparison.OrdinalIgnoreCase);

    private async Task<string> ResolveHostAsync(CancellationToken cancellationToken)
    {
        var host = (await store.GetSettingAsync("correo_smtp_host", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(host))
            host = (await store.GetSettingAsync("correo_host", cancellationToken: cancellationToken)).Trim();
        return host;
    }

    private async Task<int> ResolvePortAsync(CancellationToken cancellationToken)
    {
        var raw = (await store.GetSettingAsync("correo_smtp_puerto", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = (await store.GetSettingAsync("correo_puerto", cancellationToken: cancellationToken)).Trim();
        return int.TryParse(raw, out var port) && port > 0 ? port : 587;
    }

    private async Task<bool> ResolveSslAsync(CancellationToken cancellationToken)
    {
        var raw = (await store.GetSettingAsync("correo_smtp_ssl", cancellationToken: cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = (await store.GetSettingAsync("correo_ssl", cancellationToken: cancellationToken)).Trim();
        return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildSaleBody(
        long ticketNumber,
        IReadOnlyCollection<CartItem> items,
        decimal total,
        string userName,
        string paymentMethod)
    {
        var sb = new StringBuilder();
        sb.AppendLine("====== GRUNFLEX POS ======");
        sb.AppendLine($"Ticket: {ticketNumber}");
        sb.AppendLine($"Fecha: {DateTime.Now:dd/MM/yyyy HH:mm}");
        sb.AppendLine($"Cajero: {userName}");
        sb.AppendLine($"Pago: {paymentMethod}");
        sb.AppendLine("--------------------------");
        foreach (var item in items)
        {
            var lineTotal = Math.Round(
                item.EffectiveUnitPrice * item.Quantity * (1m - item.DiscountPercentage / 100m),
                2,
                MidpointRounding.AwayFromZero);
            sb.AppendLine($"{item.Product.Name} x{item.Quantity:0.##} - ${lineTotal:0}");
        }
        sb.AppendLine("--------------------------");
        sb.AppendLine($"TOTAL: ${total:0}");
        sb.AppendLine("==========================");
        return sb.ToString();
    }
}
