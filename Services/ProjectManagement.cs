using MySqlConnector;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Models;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed partial class ProjectService
{
    private static void ValidateProject(string name, string slug)
    {
        if (name.Length is < 2 or > 160) throw ApiException.BadRequest("Tên dự án cần từ 2 đến 160 ký tự.");
        if (slug.Length > 160 || !SlugRegex().IsMatch(slug)) throw ApiException.BadRequest("Slug không hợp lệ.");
    }

    // Serialize membership changes for each project and check the actor's current role in the same transaction.
    private async Task ManageAsync(long projectId, long actorId, Func<MySqlConnection, MySqlTransaction, Task> action, CancellationToken ct, bool systemAdmin = false, string auditAction = "", string auditSummary = "")
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await SystemAdminGuard.LockAsync(cn, tx, ct);
        await using var project = new MySqlCommand("SELECT id FROM projects WHERE id=@project FOR UPDATE", cn, tx);
        project.Parameters.AddWithValue("@project", projectId);
        if (await project.ExecuteScalarAsync(ct) is null) throw ApiException.NotFound("Không tìm thấy dự án.");
        await using var permission = new MySqlCommand("SELECT pm.role FROM project_members pm JOIN users u ON u.id=pm.user_id AND u.is_active=1 WHERE pm.project_id=@project AND pm.user_id=@user", cn, tx);
        permission.Parameters.AddWithValue("@project", projectId);
        permission.Parameters.AddWithValue("@user", actorId);
        if (systemAdmin) await SystemAdminGuard.RequireAsync(cn, tx, actorId, ct);
        else if (Convert.ToString(await permission.ExecuteScalarAsync(ct)) != ProjectRole.Manager)
            throw ApiException.Forbidden("Chỉ Manager được quản lý dự án.");
        await action(cn, tx);
        if (systemAdmin) await SystemAdminGuard.AuditAsync(cn, tx, actorId, auditAction, "project", projectId, auditSummary, ct);
        await tx.CommitAsync(ct);
    }

    public Task UpdateAsync(long projectId, long actorId, UpdateProjectRequest request, CancellationToken ct = default, bool systemAdmin = false) =>
        ManageAsync(projectId, actorId, async (cn, tx) =>
        {
            var name = request.Name.Trim();
            var slug = request.Slug.Trim().ToLowerInvariant();
            ValidateProject(name, slug);
            await using var cmd = new MySqlCommand("UPDATE projects SET name=@name,slug=@slug WHERE id=@project", cn, tx);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@slug", slug);
            cmd.Parameters.AddWithValue("@project", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct, systemAdmin, "project.updated", request.Name + " / " + request.Slug);

    public Task DeleteAsync(long projectId, long actorId, CancellationToken ct = default, bool systemAdmin = false) =>
        ManageAsync(projectId, actorId, async (cn, tx) =>
        {
            // Remove strings first: current_source_version_id has ON DELETE RESTRICT.
            // Other project-owned rows are removed by the schema's cascading foreign keys.
            await using var cmd = new MySqlCommand("DELETE FROM source_strings WHERE project_id=@project; DELETE FROM projects WHERE id=@project;", cn, tx);
            cmd.Parameters.AddWithValue("@project", projectId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct, systemAdmin, "project.deleted", "Đã xóa dự án và dữ liệu nghiệp vụ trong database.");

    public Task AddMemberAsync(long projectId, long actorId, AddMemberRequest request, CancellationToken ct = default, bool systemAdmin = false) =>
        ManageAsync(projectId, actorId, async (cn, tx) =>
        {
            if (!ProjectRole.All.Contains(request.Role)) throw ApiException.BadRequest("Vai trò không hợp lệ.");
            await using var user = new MySqlCommand(request.UserId.HasValue
                ? "SELECT id FROM users WHERE id=@user AND is_active=1"
                : "SELECT id FROM users WHERE email=@email AND is_active=1", cn, tx);
            user.Parameters.AddWithValue("@user", request.UserId);
            user.Parameters.AddWithValue("@email", request.Email?.Trim().ToLowerInvariant() ?? "");
            var userId = await user.ExecuteScalarAsync(ct);
            if (userId is null) throw ApiException.NotFound("Người dùng chưa đăng ký hoặc tài khoản đã bị khóa.");
            await using var cmd = new MySqlCommand("INSERT INTO project_members(project_id,user_id,role) VALUES(@project,@user,@role)", cn, tx);
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@user", userId);
            cmd.Parameters.AddWithValue("@role", request.Role);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct, systemAdmin, "project.member_added", "Thành viên: " + (request.Email ?? request.UserId?.ToString()) + "; vai trò: " + request.Role);

    private static async Task GuardManagerAsync(MySqlConnection cn, MySqlTransaction tx, long projectId, long userId, CancellationToken ct)
    {
        await using var target = new MySqlCommand("SELECT IF(u.is_active,pm.role,'inactive') FROM project_members pm JOIN users u ON u.id=pm.user_id WHERE pm.project_id=@project AND pm.user_id=@user", cn, tx);
        target.Parameters.AddWithValue("@project", projectId);
        target.Parameters.AddWithValue("@user", userId);
        var role = await target.ExecuteScalarAsync(ct);
        if (role is null) throw ApiException.NotFound("Không tìm thấy thành viên.");
        if (Convert.ToString(role) != ProjectRole.Manager) return;
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM project_members pm JOIN users u ON u.id=pm.user_id AND u.is_active=1 WHERE pm.project_id=@project AND pm.role='manager'", cn, tx);
        count.Parameters.AddWithValue("@project", projectId);
        if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) <= 1)
            throw ApiException.Conflict("Dự án cần ít nhất một Manager.", new { code = "last_manager" });
    }

    public Task UpdateMemberAsync(long projectId, long actorId, long userId, string role, CancellationToken ct = default, bool systemAdmin = false) =>
        ManageAsync(projectId, actorId, async (cn, tx) =>
        {
            if (!ProjectRole.All.Contains(role)) throw ApiException.BadRequest("Vai trò không hợp lệ.");
            if (role != ProjectRole.Manager) await GuardManagerAsync(cn, tx, projectId, userId, ct);
            else
            {
                await using var exists = new MySqlCommand("SELECT pm.user_id FROM project_members pm JOIN users u ON u.id=pm.user_id AND u.is_active=1 WHERE pm.project_id=@project AND pm.user_id=@user", cn, tx);
                exists.Parameters.AddWithValue("@project", projectId); exists.Parameters.AddWithValue("@user", userId);
                if (await exists.ExecuteScalarAsync(ct) is null) throw ApiException.NotFound("Không tìm thấy thành viên.");
            }
            await using var cmd = new MySqlCommand("UPDATE project_members SET role=@role WHERE project_id=@project AND user_id=@user", cn, tx);
            cmd.Parameters.AddWithValue("@role", role); cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@user", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct, systemAdmin, "project.member_updated", "Thành viên: " + userId + "; vai trò: " + role);

    public Task RemoveMemberAsync(long projectId, long actorId, long userId, CancellationToken ct = default, bool systemAdmin = false) =>
        ManageAsync(projectId, actorId, async (cn, tx) =>
        {
            await GuardManagerAsync(cn, tx, projectId, userId, ct);
            await using var cmd = new MySqlCommand("DELETE FROM project_members WHERE project_id=@project AND user_id=@user", cn, tx);
            cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@user", userId);
            await cmd.ExecuteNonQueryAsync(ct);
        }, ct, systemAdmin, "project.member_removed", "Gỡ thành viên: " + userId);
}
