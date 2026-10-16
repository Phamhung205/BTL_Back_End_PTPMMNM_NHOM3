using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Models;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
[Route("api/projects")]
public sealed class ProjectsController(ProjectService projects, ProjectAccessService access, LocaleService locales, Db db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) => Ok(await projects.MineAsync(User.UserId(), ct));

    [HttpPost]
    public async Task<IActionResult> Create(CreateProjectRequest request, CancellationToken ct)
    {
        var id = await projects.CreateAsync(User.UserId(), request, ct);
        return Created($"/api/projects/{id}", new { id });
    }

    [HttpGet("{projectId:long}/locales")]
    public async Task<IActionResult> Locales(long projectId, CancellationToken ct)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        return Ok(await locales.ForProjectAsync(projectId, ct));
    }

    [HttpPost("{projectId:long}/locales")]
    public async Task<IActionResult> AttachLocale(long projectId, AttachLocaleRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(projectId, User.UserId(), ct);
        await locales.AttachAsync(projectId, request.LocaleCode, ct);
        return Ok(new { success=true });
    }

    [HttpGet("{projectId:long}/members")]
    public async Task<IActionResult> Members(long projectId, CancellationToken ct)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT u.id,u.email,u.display_name,pm.role FROM project_members pm JOIN users u ON u.id=pm.user_id WHERE pm.project_id=@project ORDER BY u.display_name";
        cmd.Parameters.AddWithValue("@project", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { id=rd.GetInt64(0), email=rd.GetString(1), displayName=rd.GetString(2), role=rd.GetString(3) });
        return Ok(list);
    }

    [HttpPost("{projectId:long}/members")]
    public async Task<IActionResult> AddMember(long projectId, AddMemberRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(projectId, User.UserId(), ct);
        if (!ProjectRole.All.Contains(request.Role)) throw ApiException.BadRequest("Role không hợp lệ.");
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"INSERT INTO project_members(project_id,user_id,role) VALUES(@project,@user,@role)
ON DUPLICATE KEY UPDATE role=VALUES(role)";
        cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@user", request.UserId); cmd.Parameters.AddWithValue("@role", request.Role);
        await cmd.ExecuteNonQueryAsync(ct);
        return Ok(new { success=true });
    }
}
