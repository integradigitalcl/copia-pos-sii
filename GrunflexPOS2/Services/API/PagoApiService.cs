using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using GrunflexPOS2.Data;

namespace GrunflexPOS2.Services.API
{
    public class PagoApiService
    {
        private readonly string _baseUrl;

        private readonly string _terminalId;

        public PagoApiService()
        {
            var cfg = AppConfig.Cargar();
            _baseUrl = cfg.PagoApiBaseUrl;
            _terminalId = Environment.GetEnvironmentVariable("GRUNFLEX_PAGO_TERMINAL_ID") ?? "POS-LOCAL";
        }

        public async Task<PagoResponse?> ProcesarPagoAsync(decimal monto, string numeroTicket)
        {
            using var httpClient = new HttpClient();

            var request = new
            {
                monto = monto,
                numeroTicket = numeroTicket,
                terminalId = _terminalId
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(_baseUrl, content);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<PagoResponse>(responseJson, options);
        }

        public async Task<Guid?> CrearPagoAsync(decimal monto, string numeroTicket)
        {
            using var httpClient = new HttpClient();

            var request = new
            {
                monto = monto,
                numeroTicket = numeroTicket,
                terminalId = _terminalId
            };

            var json = JsonSerializer.Serialize(request);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(_baseUrl, content);

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            var tx = JsonSerializer.Deserialize<PagoTransaccion>(responseJson, options);

            return tx?.Id;
        }

        public async Task<PagoTransaccion?> ObtenerEstadoAsync(Guid id)
        {
            using var httpClient = new HttpClient();

            var response = await httpClient.GetAsync($"{_baseUrl}/{id}");

            if (!response.IsSuccessStatusCode)
                return null;

            var responseJson = await response.Content.ReadAsStringAsync();

            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return JsonSerializer.Deserialize<PagoTransaccion>(responseJson, options);
        }
    }

    public class PagoResponse
    {
        public bool Aprobado { get; set; }
        public string CodigoAutorizacion { get; set; } = "";
        public string Mensaje { get; set; } = "";
        public DateTime Fecha { get; set; }
    }

    public class PagoTransaccion
    {
        public Guid Id { get; set; }
        public decimal Monto { get; set; }
        public string Estado { get; set; } = "";
        public string CodigoAutorizacion { get; set; } = "";
    }
}