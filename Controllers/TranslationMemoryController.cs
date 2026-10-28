using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Services;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize, Route("api/translation-memory")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TranslationMemoryController(TranslationMemoryService memory) : ControllerBase
{
    [HttpGet("languages")]
    public async Task<IActionResult> Languages(CancellationToken ct) => Ok(await memory.LanguagesAsync(ct));

    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? locale, [FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default) =>
        Ok(await memory.SearchAsync(locale, q, page, ct));
}
