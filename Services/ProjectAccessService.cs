using TranslationManager.Api.Data;
using TranslationManager.Api.Models;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class ProjectAccessService(Db db)
{
    public async Task<string> GetRoleAsync(long projectId, long userId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT role FROM project_members WHERE project_id=@project AND user_id=@user LIMIT 1";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@user", userId);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null) throw ApiException.Forbidden("Bạn không thuộc dự án này.");
        return Convert.ToString(value)!;
    }

    public async Task RequireAnyAsync(long projectId, long userId, string[] roles, CancellationToken ct = default)
    {
        var role = await GetRoleAsync(projectId, userId, ct);
        if (!roles.Contains(role, StringComparer.OrdinalIgnoreCase))
            throw ApiException.Forbidden("Bạn không có quyền thực hiện thao tác này.");
    }

    public Task RequireManagerAsync(long projectId, long userId, CancellationToken ct = default) =>
        RequireAnyAsync(projectId, userId, [ProjectRole.Manager], ct);
}
