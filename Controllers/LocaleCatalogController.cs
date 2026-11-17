using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Services;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/locales")]
public sealed class LocaleCatalogController(LocaleService locales) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(await locales.CatalogAsync(ct));
}
