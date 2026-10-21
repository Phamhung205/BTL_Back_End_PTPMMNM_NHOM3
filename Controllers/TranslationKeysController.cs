using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects/{projectId:long}/translation-keys")]
public sealed class TranslationKeysController(ProjectAccessService access, SourceVersionService source) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(long projectId, [FromQuery] string? q, [FromQuery] string? status, CancellationToken ct)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        return Ok(await source.KeysAsync(projectId, q, status, ct));
    }
}
