using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace GrunflexPOS.API.Controllers;

[ApiController]
[Route("api/multicaja")]
[AllowAnonymous]
public sealed class MulticajaCapabilitiesController : ControllerBase
{
    [HttpGet("capabilities")]
    public IActionResult GetCapabilities()
    {
        var ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
        return Ok(new
        {
            apiVersion = ver,
            incrementalSync = true,
            signalR = true,
            idempotency = true,
            offlineReplay = true,
            heartbeatVersion = 2,
            terminalIdentity = true,
            vincularEquipo = true,
            fullCatalogPull = true,
            minClientVersion = "0.3.0"
        });
    }
}
