using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize(Policy = "SystemAdmin"), Route("api/admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminController(AdminService admin, ProjectService projects, AccessService access) : ControllerBase
{
    [HttpGet("roles")]
    public IActionResult Roles() => Ok(new { version = 1, systemRoles = PermissionCatalog.SystemRoles, projectRoles = PermissionCatalog.ProjectRoles });
    [HttpGet("permissions")]
    public IActionResult Permissions() => Ok(new { version = 1, items = PermissionCatalog.Permissions });
    [HttpGet("users/find")]
    public async Task<IActionResult> FindUser([FromQuery] string? email, CancellationToken ct) => Ok(await admin.FindByEmailAsync(email, ct));
    [HttpGet("users/{userId:long}")]
    public async Task<IActionResult> UserDetail(long userId, CancellationToken ct) => Ok(await admin.UserAsync(userId, ct));
    [HttpGet("users/{userId:long}/permissions")]
    public async Task<IActionResult> UserPermissions(long userId, CancellationToken ct) => Ok(await access.ForUserAsync(userId, ct));
    [HttpPost("users")]
    public async Task<IActionResult> CreateUser(CreateSystemUserRequest request, CancellationToken ct)
    {
        var user = await admin.CreateUserAsync(User.UserId(), request, ct);
        return Created($"/api/admin/users/{user.Id}", user);
    }
    [HttpPut("users/{userId:long}/role")]
    public async Task<IActionResult> SetRole(long userId, ChangeSystemRoleRequest request, CancellationToken ct) => Ok(await admin.SetRoleAsync(User.UserId(), userId, request, ct));
    [HttpPut("users/{userId:long}/status")]
    public async Task<IActionResult> SetStatus(long userId, ChangeUserStatusRequest request, CancellationToken ct) => Ok(await admin.SetStatusAsync(User.UserId(), userId, request, ct));
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
