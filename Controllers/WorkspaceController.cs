using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TranslationManager.Api.Data;
using TranslationManager.Api.Services;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController, Authorize]
public sealed class WorkspaceController(Db db, ProjectAccessService access) : ControllerBase
{
    [HttpGet("api/dashboard/stats")]
    public async Task<IActionResult> Stats(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        // Every aggregate is scoped to current membership, including after access is revoked.
        cmd.CommandText = @"SELECT
 (SELECT COUNT(*) FROM project_members WHERE user_id=@user),
 (SELECT COUNT(*) FROM project_members WHERE user_id=@user AND role='manager'),
 (SELECT COUNT(DISTINCT pl.locale_code) FROM project_locales pl JOIN project_members pm ON pm.project_id=pl.project_id AND pm.user_id=@user),
 (SELECT COUNT(*) FROM source_strings ss JOIN project_members pm ON pm.project_id=ss.project_id AND pm.user_id=@user
  JOIN project_locales pl ON pl.project_id=ss.project_id AND pl.is_source=0
  LEFT JOIN translations t ON t.string_id=ss.id AND t.locale_code=pl.locale_code
  WHERE ss.status='active' AND (t.id IS NULL OR t.value IS NULL OR t.status<>'approved'))";
        cmd.Parameters.AddWithValue("@user", User.UserId());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        await rd.ReadAsync(ct);
        return Ok(new { projects = rd.GetInt64(0), managedProjects = rd.GetInt64(1), languages = rd.GetInt64(2), pendingTranslations = rd.GetInt64(3) });
    }

    [HttpGet("api/projects/{projectId:long}/activity")]
    public async Task<IActionResult> Activity(long projectId, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        await access.GetRoleAsync(projectId, User.UserId(), ct);
        if (page is < 1 or > 999999) throw ApiException.BadRequest("Trang không hợp lệ.");
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT a.*,u.display_name FROM (
 SELECT h.id,'translation' kind,h.action,ss.key_path label,t.locale_code,h.old_value,h.new_value,h.user_id,h.created_at,t.id translation_id
 FROM translation_history h JOIN translations t ON t.id=h.translation_id JOIN source_strings ss ON ss.id=t.string_id WHERE ss.project_id=@project
 UNION ALL
 SELECT c.id,'comment','commented',ss.key_path,t.locale_code,NULL,c.body,c.user_id,c.created_at,t.id
 FROM translation_comments c JOIN translations t ON t.id=c.translation_id JOIN source_strings ss ON ss.id=t.string_id WHERE ss.project_id=@project
 UNION ALL
 SELECT sv.id,'source','source_imported',sv.version_name,NULL,NULL,NULL,sv.imported_by,sv.imported_at,NULL FROM source_versions sv WHERE sv.project_id=@project
 UNION ALL
 SELECT r.id,'release','published',r.version_name,NULL,NULL,r.note,r.published_by,r.published_at,NULL FROM releases r WHERE r.project_id=@project
 ) a JOIN users u ON u.id=a.user_id ORDER BY a.created_at DESC,a.kind DESC,a.id DESC LIMIT 51 OFFSET @offset";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * 50);
        var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) items.Add(new {
            id = rd.GetInt64(0), kind = rd.GetString(1), action = rd.GetString(2), label = rd.GetString(3),
            localeCode = rd.IsDBNull(4) ? null : rd.GetString(4), oldValue = rd.IsDBNull(5) ? null : rd.GetString(5),
            newValue = rd.IsDBNull(6) ? null : rd.GetString(6), userId = rd.GetInt64(7), createdAt = rd.GetDateTime(8),
            translationId = rd.IsDBNull(9) ? (long?)null : rd.GetInt64(9), userName = rd.GetString(10)
        });
        return Ok(new { items = items.Take(50), page, hasMore = items.Count > 50 });
    }
}
