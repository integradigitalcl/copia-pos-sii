using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using GrunflexPOS.API.DTOs;
using GrunflexPOS.API.Services;
using GrunflexPOS.API.Models;

namespace GrunflexPOS.API.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize(Policy = "ApiOperator")]
    public class PagoController : ControllerBase
    {
        private readonly PagoService _service;

        public PagoController(PagoService service)
        {
            _service = service;
        }

        // 🔵 WPF crea pago
        [HttpPost]
        public IActionResult CrearPago([FromBody] PagoRequest request)
        {
            if (string.IsNullOrEmpty(request.TerminalId) || string.IsNullOrEmpty(request.Token))
                return BadRequest("TerminalId y Token son requeridos");

            var tx = _service.CrearPago(request);
            return Ok(tx);
        }

        // 🟡 PAX obtiene pendientes (🔐 SEGURO)
        [HttpGet("pendientes")]
        public IActionResult Pendientes(
            [FromQuery] string terminalId,
            [FromQuery] string token)
        {
            if (string.IsNullOrEmpty(terminalId) || string.IsNullOrEmpty(token))
                return BadRequest("terminalId y token son requeridos");

            var lista = _service.ObtenerPendientesPorTerminal(terminalId, token);

            return Ok(lista);
        }

        // 🟢 PAX responde
        [HttpPost("resolver")]
        public IActionResult Resolver([FromBody] ResolverPagoRequest request)
        {
            var tx = _service.ResolverPago(request.Id, request.Aprobado);

            if (tx == null)
                return NotFound();

            return Ok(tx);
        }

        // 🔵 WPF consulta estado
        [HttpGet("{id}")]
        public IActionResult Estado(Guid id)
        {
            var tx = _service.ObtenerEstado(id);

            if (tx == null)
                return NotFound();

            return Ok(tx);
        }
    }
}
