using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize(Policy = "SystemAdmin"), Route("api/admin")]
public sealed class AdminController(AdminService admin, ProjectService projects) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<IActionResult> Stats(CancellationToken ct) => Ok(await admin.StatsAsync(ct));
    [HttpGet("users")]
    public async Task<IActionResult> Users([FromQuery] string? q, [FromQuery] string? role, [FromQuery] string? status, [FromQuery] int page = 1, CancellationToken ct = default) => Ok(await admin.UsersAsync(q, role, status, page, ct));
    [HttpPut("users/{userId:long}")]
    public async Task<IActionResult> UpdateUser(long userId, UpdateSystemUserRequest request, CancellationToken ct) { await admin.UpdateUserAsync(User.UserId(), userId, request, ct); return NoContent(); }
    [HttpGet("projects")]
    public async Task<IActionResult> Projects([FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default) => Ok(await admin.ProjectsAsync(q, page, ct));
    [HttpPut("projects/{projectId:long}")]
    public async Task<IActionResult> UpdateProject(long projectId, UpdateProjectRequest request, CancellationToken ct) { await projects.UpdateAsync(projectId, User.UserId(), request, ct, true); return NoContent(); }
    [HttpDelete("projects/{projectId:long}")]
    public async Task<IActionResult> DeleteProject(long projectId, CancellationToken ct) { await projects.DeleteAsync(projectId, User.UserId(), ct, true); return NoContent(); }
    [HttpGet("projects/{projectId:long}/members")]
    public async Task<IActionResult> Members(long projectId, CancellationToken ct) => Ok(await admin.MembersAsync(projectId, ct));
    [HttpPost("projects/{projectId:long}/members")]
    public async Task<IActionResult> AddMember(long projectId, AddMemberRequest request, CancellationToken ct) { await projects.AddMemberAsync(projectId, User.UserId(), request, ct, true); return StatusCode(201, new { success = true }); }
    [HttpPut("projects/{projectId:long}/members/{userId:long}")]
    public async Task<IActionResult> UpdateMember(long projectId, long userId, UpdateMemberRoleRequest request, CancellationToken ct) { await projects.UpdateMemberAsync(projectId, User.UserId(), userId, request.Role, ct, true); return NoContent(); }
    [HttpDelete("projects/{projectId:long}/members/{userId:long}")]
    public async Task<IActionResult> RemoveMember(long projectId, long userId, CancellationToken ct) { await projects.RemoveMemberAsync(projectId, User.UserId(), userId, ct, true); return NoContent(); }
    [HttpGet("audit")]
    public async Task<IActionResult> Audit([FromQuery] string? q, [FromQuery] int page = 1, CancellationToken ct = default) => Ok(await admin.AuditAsync(q, page, ct));
}
