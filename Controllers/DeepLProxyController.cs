using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;

namespace TranslationManager.Api.Controllers;

[ApiController, AllowAnonymous, EnableCors("PublicTranslationApi")]
[Route("api/public/translate")]
public sealed class DeepLProxyController(IConfiguration configuration, IHttpClientFactory httpClientFactory) : ControllerBase
{
    public sealed record TranslateRequest(string[] Text, string TargetLang, string? SourceLang = null);

    [HttpPost]
    public async Task<IActionResult> Translate([FromBody] TranslateRequest request, CancellationToken ct)
    {
        var apiKey = configuration["DeepL:ApiKey"];
        var apiUrl = configuration["DeepL:ApiUrl"] ?? "https://api-free.deepl.com/v2/translate";

        if (string.IsNullOrWhiteSpace(apiKey))
            return StatusCode(500, new { error = "DeepL API key chưa được cấu hình trên server." });

        if (request.Text is null || request.Text.Length == 0 || string.IsNullOrWhiteSpace(request.TargetLang))
            return BadRequest(new { error = "Thiếu text hoặc target_lang." });

        var payload = new
        {
            text = request.Text,
            target_lang = request.TargetLang.ToUpperInvariant(),
            source_lang = request.SourceLang?.ToUpperInvariant()
        };

        var client = httpClientFactory.CreateClient();
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("DeepL-Auth-Key", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await client.SendAsync(httpRequest, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            return StatusCode((int)response.StatusCode, new { error = "DeepL trả lỗi", detail = body });

        return Content(body, "application/json");
    }
}