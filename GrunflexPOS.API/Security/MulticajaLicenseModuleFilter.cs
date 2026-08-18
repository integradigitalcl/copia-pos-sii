using GrunflexPOS.API.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace GrunflexPOS.API.Security;

/// <summary>Bloquea endpoints multicaja si no hay licencia activa con módulo Multicaja.</summary>
public sealed class MulticajaLicenseModuleFilter : IAsyncActionFilter
{
    private readonly LicenseSlotService _slots;

    public MulticajaLicenseModuleFilter(LicenseSlotService slots) => _slots = slots;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var lic = await _slots.GetActiveLicenseAsync(null, context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        if (lic == null || !lic.Multicaja)
        {
            context.Result = new ObjectResult(new
            {
                ok = false,
                error = "Licencia Multicaja no activa o vencida en el servidor.",
                errorCode = "LICENSE_MULTICAJA_REQUIRED"
            })
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
            return;
        }

        await next().ConfigureAwait(false);
    }
}
