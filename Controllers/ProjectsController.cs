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

    // Project administration (Manager only).
    [HttpPut("{projectId:long}")]
    public async Task<IActionResult> Update(long projectId, UpdateProjectRequest request, CancellationToken ct)
    {
        await projects.UpdateAsync(projectId, User.UserId(), request, ct);
        return NoContent();
    }

    [HttpDelete("{projectId:long}")]
    public async Task<IActionResult> Delete(long projectId, CancellationToken ct)
    {
        await projects.DeleteAsync(projectId, User.UserId(), ct);
        return NoContent();
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
        return Ok(new { success = true });
    }

    [HttpPut("{projectId:long}/locales/{code}")]
    public async Task<IActionResult> UpdateLocale(long projectId, string code, UpdateLocaleRequest request, CancellationToken ct)
    {
        await access.RequireManagerAsync(projectId, User.UserId(), ct);
        await locales.UpdateAsync(projectId, code, request.Name, ct);
        return Ok(new { success = true });
    }

    [HttpDelete("{projectId:long}/locales/{code}")]
    public async Task<IActionResult> DetachLocale(long projectId, string code, CancellationToken ct)
    {
        await access.RequireManagerAsync(projectId, User.UserId(), ct);
        await locales.DetachAsync(projectId, code, ct);
        return NoContent();
    }

    [HttpGet("{projectId:long}/members")]
    public async Task<IActionResult> Members(long projectId, CancellationToken ct)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT u.id,u.email,u.display_name,pm.role,u.is_active FROM project_members pm JOIN users u ON u.id=pm.user_id WHERE pm.project_id=@project ORDER BY u.display_name";
        cmd.Parameters.AddWithValue("@project", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt64(0), userId = rd.GetInt64(0), email = rd.GetString(1), displayName = rd.GetString(2), role = rd.GetString(3), isActive = rd.GetBoolean(4) });
        return Ok(list);
    }

    [HttpPost("{projectId:long}/members")]
    public async Task<IActionResult> AddMember(long projectId, AddMemberRequest request, CancellationToken ct)
    {
        await projects.AddMemberAsync(projectId, User.UserId(), request, ct);
        return StatusCode(201, new { success = true });
    }

    [HttpPut("{projectId:long}/members/{userId:long}")]
    public async Task<IActionResult> UpdateMember(long projectId, long userId, UpdateMemberRoleRequest request, CancellationToken ct)
    {
        await projects.UpdateMemberAsync(projectId, User.UserId(), userId, request.Role, ct);
        return NoContent();
    }

    [HttpDelete("{projectId:long}/members/{userId:long}")]
    public async Task<IActionResult> RemoveMember(long projectId, long userId, CancellationToken ct)
    {
        await projects.RemoveMemberAsync(projectId, User.UserId(), userId, ct);
        return NoContent();
    }
}
