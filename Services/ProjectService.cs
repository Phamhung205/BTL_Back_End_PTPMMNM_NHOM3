using System.Text.RegularExpressions;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed partial class ProjectService(Db db)
{
    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugRegex();

    public async Task<object[]> MineAsync(long userId, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT p.id,p.name,p.slug,p.source_locale_code,pm.role,p.created_at
FROM projects p JOIN project_members pm ON pm.project_id=p.id
WHERE pm.user_id=@user ORDER BY p.id DESC";
        cmd.Parameters.AddWithValue("@user", userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new { id=rd.GetInt64(0), name=rd.GetString(1), slug=rd.GetString(2), sourceLocaleCode=rd.GetString(3), role=rd.GetString(4), createdAt=rd.GetDateTime(5) });
        return [.. list];
    }

    public async Task<long> CreateAsync(long userId, CreateProjectRequest request, CancellationToken ct = default)
    {
        var name = request.Name.Trim();
        var slug = request.Slug.Trim().ToLowerInvariant();
        var sourceLocale = request.SourceLocaleCode.Trim();
        ValidateProject(name, slug);
        if (sourceLocale.Length is < 2 or > 20) throw ApiException.BadRequest("Mã ngôn ngữ nguồn không hợp lệ.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await SystemAdminGuard.LockAsync(cn, tx, ct);
        await using var activeUser = new MySqlConnector.MySqlCommand("SELECT id FROM users WHERE id=@user AND is_active=1", cn, tx);
        activeUser.Parameters.AddWithValue("@user", userId);
        if (await activeUser.ExecuteScalarAsync(ct) is null) throw ApiException.Unauthorized();

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO projects(name,slug,source_locale_code,created_by) VALUES(@name,@slug,@locale,@user); SELECT LAST_INSERT_ID();";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@slug", slug);
        cmd.Parameters.AddWithValue("@locale", sourceLocale);
        cmd.Parameters.AddWithValue("@user", userId);
        var projectId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));

        await using var locale = cn.CreateCommand();
        locale.Transaction = tx;
        locale.CommandText = @"INSERT IGNORE INTO locales(code,name) VALUES(@code,@code);
INSERT INTO project_locales(project_id,locale_code,is_source) VALUES(@project,@code,1);";
        locale.Parameters.AddWithValue("@code", sourceLocale);
        locale.Parameters.AddWithValue("@project", projectId);
        await locale.ExecuteNonQueryAsync(ct);

        await using var member = cn.CreateCommand();
        member.Transaction = tx;
        member.CommandText = "INSERT INTO project_members(project_id,user_id,role) VALUES(@project,@user,'manager')";
        member.Parameters.AddWithValue("@project", projectId);
        member.Parameters.AddWithValue("@user", userId);
        await member.ExecuteNonQueryAsync(ct);

        await tx.CommitAsync(ct);
        return projectId;
    }
}
