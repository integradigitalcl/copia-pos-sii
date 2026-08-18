using System;
using System.Net;
using System.Net.Mail;
using System.IO;

namespace GrunflexPOS2.Services
{
    public class EmailService
    {
        private readonly ConfiguracionService _configService;

        public EmailService()
        {
            _configService = new ConfiguracionService();
        }

        // 🔹 MÉTODO ORIGINAL (NO SE TOCA)
        public bool EnviarCorreo(string destinatario, string asunto, string mensaje, out string error)
        {
            error = string.Empty;

            try
            {
                var email = _configService.GetCorreoEmail();
                var clave = _configService.GetCorreoClave();

                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(clave))
                {
                    error = "No hay configuración de correo.";
                    return false;
                }

                var smtp = new SmtpClient("smtp.gmail.com", 587)
                {
                    Credentials = new NetworkCredential(email, clave),
                    EnableSsl = true
                };

                var mail = new MailMessage
                {
                    From = new MailAddress(email),
                    Subject = asunto,
                    Body = mensaje,
                    IsBodyHtml = false
                };

                mail.To.Add(destinatario);

                smtp.Send(mail);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 🔥 MÉTODO PROFESIONAL ORIGINAL (SIN TOCAR)
        public bool EnviarCorreo(
            string destinatario,
            string asunto,
            string mensaje,
            string host,
            int puerto,
            bool ssl,
            string email,
            string clave,
            out string error)
        {
            return EnviarCorreo(
                destinatario,
                asunto,
                mensaje,
                host,
                puerto,
                ssl,
                email,
                clave,
                null,
                out error);
        }

        // 🚀 NUEVO: MÉTODO CON ADJUNTO
        public bool EnviarCorreo(
            string destinatario,
            string asunto,
            string mensaje,
            string host,
            int puerto,
            bool ssl,
            string email,
            string clave,
            string? rutaAdjunto,
            out string error)
        {
            error = string.Empty;

            try
            {
                if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(clave))
                {
                    error = "Credenciales inválidas.";
                    return false;
                }

                using var smtp = new SmtpClient(host, puerto)
                {
                    Credentials = new NetworkCredential(email, clave),
                    EnableSsl = ssl
                };

                using var mail = new MailMessage
                {
                    From = new MailAddress(email),
                    Subject = asunto,
                    Body = mensaje,
                    IsBodyHtml = false
                };

                mail.To.Add(destinatario);

                // 📎 ADJUNTO (SI EXISTE)
                if (!string.IsNullOrWhiteSpace(rutaAdjunto) && File.Exists(rutaAdjunto))
                {
                    var attachment = new Attachment(rutaAdjunto);
                    mail.Attachments.Add(attachment);
                }

                smtp.Send(mail);

                return true;
            }
            catch (SmtpException smtpEx)
            {
                error = "Error SMTP: " + smtpEx.Message;
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }
    }
}