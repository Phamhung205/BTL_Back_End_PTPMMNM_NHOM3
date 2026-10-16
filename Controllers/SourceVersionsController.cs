using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects/{projectId:long}/source-versions")]
public sealed class SourceVersionsController(ProjectAccessService access, SourceVersionService source) : ControllerBase
{
    [HttpPost("import")]
    public async Task<IActionResult> Import(long projectId, ImportSourceRequest request, CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        return Ok(await source.ImportAsync(projectId,userId,request,ct));
    }

    [HttpGet("{versionId:long}/changes")]
    public async Task<IActionResult> Changes(long projectId,long versionId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await source.ChangesAsync(projectId,versionId,ct));
    }
}
