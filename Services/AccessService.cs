using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class AccessService(Db db)
{
    public async Task<UserPermissionSnapshot> ForUserAsync(long userId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        string email, name, role; bool active; int version;
        await using (var user = cn.CreateCommand())
        {
            user.CommandText = "SELECT email,display_name,system_role,is_active,security_version FROM users WHERE id=@id";
            user.Parameters.AddWithValue("@id", userId);
            await using var rd = await user.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Không tìm thấy tài khoản.");
            email = rd.GetString(0); name = rd.GetString(1); role = rd.GetString(2); active = rd.GetBoolean(3); version = rd.GetInt32(4);
        }
        var projects = new List<ProjectPermissionGrant>();
        await using (var member = cn.CreateCommand())
        {
            member.CommandText = "SELECT p.id,p.name,p.slug,pm.role FROM project_members pm JOIN projects p ON p.id=pm.project_id WHERE pm.user_id=@id ORDER BY p.id";
            member.Parameters.AddWithValue("@id", userId);
            await using var rd = await member.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var projectRole = rd.GetString(3);
                projects.Add(new(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), projectRole,
                    active ? PermissionCatalog.ProjectPermissions(projectRole) : [],
                    !active ? "none" : projectRole == "manager" ? "all" : projectRole == "translator" ? "assigned_only" : "none"));
            }
        }
        return new(userId, email, name, role, active, version, active ? PermissionCatalog.SystemPermissions(role) : [], [.. projects]);
    }
}
