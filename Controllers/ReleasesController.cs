using Microsoft.AspNetCore.Authorization;
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

    /// <summary>Chốt snapshot và phát hành (chỉ manager).</summary>
    [HttpPost]
    public async Task<IActionResult> Publish(long projectId,PublishReleaseRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        var release=await releases.PublishAsync(projectId,userId,request,ct);
        return Created($"/api/projects/{projectId}/releases/{release.Id}",release);
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
}
