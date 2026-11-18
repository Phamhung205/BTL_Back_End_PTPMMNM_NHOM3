using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>Gợi ý từ kho khi đang dịch: GET /api/translation-memory/suggest?locale=en&amp;text=Trang chủ (đích vi thì thêm from=en).</summary>
    [HttpGet("suggest")]
    public async Task<IActionResult> Suggest([FromQuery] string? locale, [FromQuery] string? text, [FromQuery] string? from, CancellationToken ct = default) =>
        Ok(await memory.SuggestAsync(locale, text, from, ct));
}