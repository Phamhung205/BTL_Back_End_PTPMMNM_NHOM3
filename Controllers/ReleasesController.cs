using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects/{projectId:long}/releases")]
public sealed class ReleasesController(ProjectAccessService access, ReleaseService releases, Db db) : ControllerBase
{
    [HttpGet("validate")]
    public async Task<IActionResult> Validate(long projectId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        return Ok(await releases.ValidateAsync(projectId,ct));
    }

    [HttpPost]
    public async Task<IActionResult> Publish(long projectId,PublishReleaseRequest request,CancellationToken ct)
    {
        var userId=User.UserId();
        await access.RequireManagerAsync(projectId,userId,ct);
        return Ok(await releases.PublishAsync(projectId,userId,request.VersionName,ct));
    }

    [HttpGet]
    public async Task<IActionResult> List(long projectId,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        var list=new List<object>();
        await using var cn=await db.OpenAsync(ct);
        await using var cmd=cn.CreateCommand();
        cmd.CommandText="SELECT id,version_name,published_at,published_by FROM releases WHERE project_id=@project ORDER BY id DESC";
        cmd.Parameters.AddWithValue("@project",projectId);
        await using var rd=await cmd.ExecuteReaderAsync(ct);
        while(await rd.ReadAsync(ct)) list.Add(new { id=rd.GetInt64(0),version=rd.GetString(1),publishedAt=rd.GetDateTime(2),publishedBy=rd.GetInt64(3) });
        return Ok(list);
    }

    [HttpGet("{releaseId:long}/files/{locale}")]
    public async Task<IActionResult> Download(long projectId,long releaseId,string locale,CancellationToken ct)
    {
        await access.GetRoleAsync(projectId,User.UserId(),ct);
        var file=await releases.FileAsync(projectId,releaseId,locale,ct);
        return File(Encoding.UTF8.GetBytes(file.Json),"application/json; charset=utf-8",file.FileName);
    }
}
