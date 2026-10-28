using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects/{projectId:long}/releases")]
public sealed class ReleasesController(ProjectAccessService access, ReleaseService releases) : ControllerBase
{
    [HttpGet("validate")]
    public async Task<IActionResult> Validate(long projectId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await releases.ValidateAsync(projectId,ct));
    }

    [HttpGet("report")]
    public async Task<IActionResult> Report(long projectId, CancellationToken ct)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        // Readiness is current; each history entry contains immutable release metadata.
        return Ok(new { reportType = "current_readiness_and_release_history", projectId,
            generatedAtUtc = DateTime.UtcNow, validation = await releases.ValidateAsync(projectId, ct),
            releases = await releases.ListAsync(projectId, ct) });
    }

    /// <summary>Chốt snapshot và phát hành (chỉ manager).</summary>
    [HttpPost]
    public async Task<IActionResult> Publish(long projectId,PublishReleaseRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        var release=await releases.PublishAsync(projectId,userId,request,ct);
        var apiKey=await releases.RotateApiKeyAsync(projectId,release.Id,userId,ct);
        var published=new PublishedReleaseInfo(release.Id,release.Version,release.Note,release.KeyCount,release.PublishedAt,
            release.PublishedBy,release.PublishedByName,release.SourceVersionId,release.SourceVersionName,release.Files,apiKey);
        return Created($"/api/projects/{projectId}/releases/{release.Id}",published);
    }

    [HttpGet]
    public async Task<IActionResult> List(long projectId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await releases.ListAsync(projectId,ct));
    }

    [HttpGet("{releaseId:long}")]
    public async Task<IActionResult> Detail(long projectId,long releaseId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await releases.GetAsync(projectId,releaseId,ct));
    }

    [HttpGet("{releaseId:long}/files/{locale}")]
    public async Task<IActionResult> Download(long projectId,long releaseId,string locale,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        var file=await releases.FileAsync(projectId,releaseId,locale,ct);
        Response.Headers["X-Content-SHA256"]=file.Sha256;
        return File(file.Content,"application/json; charset=utf-8",file.FileName);
    }

    /// <summary>Tải cả gói ngôn ngữ (.zip gồm các file JSON + manifest.json).</summary>
    [HttpGet("{releaseId:long}/download")]
    public async Task<IActionResult> DownloadPackage(long projectId,long releaseId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        var package=await releases.ZipAsync(projectId,releaseId,ct);
        return File(package.Content,"application/zip",package.FileName);
    }

    /// <summary>Tải client và JSON cố định để khách hàng tích hợp website ngoại tuyến.</summary>
    [HttpGet("{releaseId:long}/integration")]
    public async Task<IActionResult> DownloadIntegration(long projectId,long releaseId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        var package=await releases.IntegrationZipAsync(projectId,releaseId,ct);
        return File(package.Content,"application/zip",package.FileName);
    }

    /// <summary>Create or rotate a key scoped to reading this immutable release.</summary>
    [HttpPost("{releaseId:long}/api-key")]
    public async Task<IActionResult> CreateApiKey(long projectId,long releaseId,CancellationToken ct)
    {
        var managerId=User.UserId();
        await access.RequireManagerAsync(projectId,managerId,ct);
        var key=await releases.RotateApiKeyAsync(projectId,releaseId,managerId,ct);
        return Ok(new { apiKey=key, releaseId, endpoint=$"/api/public/releases/{releaseId}/files/{{locale}}", message="Key chỉ đọc snapshot này. Key cũ đã bị thu hồi và key mới chỉ hiển thị một lần." });
    }
}

[ApiController, AllowAnonymous, EnableCors("PublicTranslationApi")]
[Route("api/public/releases")]
public sealed class PublicReleaseFilesController(ReleaseService releases) : ControllerBase
{
    [HttpGet("{releaseId:long}/files/{locale}")]
    public async Task<IActionResult> File(long releaseId,string locale,[FromHeader(Name="X-API-Key")] string? apiKey,CancellationToken ct)
    {
        var file=await releases.PublicFileAsync(releaseId,locale,apiKey,ct);
        Response.Headers["X-Content-SHA256"]=file.Sha256;
        Response.Headers["Cache-Control"]="public, max-age=300";
        return File(file.Content,"application/json; charset=utf-8",file.FileName);
    }
}
