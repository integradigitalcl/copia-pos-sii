using System;
using System.Text.Json.Serialization;

namespace GrunflexPOS2.Services.API
{
    public class PagoResponse
    {
        [JsonPropertyName("aprobado")]
        public bool Aprobado { get; set; }

        [JsonPropertyName("codigoAutorizacion")]
        public string CodigoAutorizacion { get; set; } = "";

        [JsonPropertyName("mensaje")]
        public string Mensaje { get; set; } = "";

        [JsonPropertyName("fecha")]
        public DateTime Fecha { get; set; }
    }
}