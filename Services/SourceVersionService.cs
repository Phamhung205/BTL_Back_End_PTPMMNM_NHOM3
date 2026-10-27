using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class SourceVersionService(Db db)
{
    public async Task<object> ImportAsync(long projectId, long userId, ImportSourceRequest request, CancellationToken ct = default)
    {
        var flat = SourceDocument.Read(request);
        if (flat.Count == 0) throw ApiException.BadRequest("Tệp nguồn không có khóa dịch.");
        var versionName = (request.VersionName ?? "").Trim();
        if (versionName.Length is < 1 or > 120) throw ApiException.BadRequest("Tên phiên bản nguồn phải có từ 1 đến 120 ký tự.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ProjectWriteLock.AcquireAsync(cn, tx, projectId, ct);

        long versionId;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO source_versions(project_id,version_name,imported_by) VALUES(@project,@name,@user); SELECT LAST_INSERT_ID();";
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@name", versionName);
            cmd.Parameters.AddWithValue("@user", userId);
            versionId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        var existing = new Dictionary<string, (long Id,string Value,string Status)>(StringComparer.Ordinal);
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id,key_path,source_text,status FROM source_strings WHERE project_id=@project";
            cmd.Parameters.AddWithValue("@project", projectId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) existing[rd.GetString(1)] = (rd.GetInt64(0), rd.GetString(2), rd.GetString(3));
        }

        var added = 0; var changed = 0; var unchanged = 0; var deleted = 0;

        foreach (var (key, value) in flat)
        {
            if (!existing.TryGetValue(key, out var old))
            {
                long stringId;
                await using (var cmd = cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"INSERT INTO source_strings(project_id,key_path,source_text,status,current_source_version_id)
VALUES(@project,@key,@value,'active',@version); SELECT LAST_INSERT_ID();";
                    cmd.Parameters.AddWithValue("@project", projectId);
                    cmd.Parameters.AddWithValue("@key", key);
                    cmd.Parameters.AddWithValue("@value", value);
                    cmd.Parameters.AddWithValue("@version", versionId);
                    stringId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                }
                await AddSnapshotAsync(cn, tx, versionId, stringId, key, value, ct);
                await AddChangeAsync(cn, tx, versionId, stringId, "new", null, value, ct);
                added++;
            }
            else
            {
                await AddSnapshotAsync(cn, tx, versionId, old.Id, key, value, ct);
                if (!string.Equals(old.Value, value, StringComparison.Ordinal) || old.Status == "deleted")
                {
                    await using var update = cn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = @"UPDATE source_strings SET source_text=@value,status='active',current_source_version_id=@version,updated_at=NOW() WHERE id=@id;
UPDATE translations SET status=CASE WHEN status='draft' THEN 'draft' ELSE 'needs_review' END,row_version=row_version+1,updated_at=NOW() WHERE string_id=@id;";
                    update.Parameters.AddWithValue("@value", value);
                    update.Parameters.AddWithValue("@version", versionId);
                    update.Parameters.AddWithValue("@id", old.Id);
                    await update.ExecuteNonQueryAsync(ct);
                    await AddChangeAsync(cn, tx, versionId, old.Id, old.Status == "deleted" ? "restored" : "changed", old.Value, value, ct);
                    changed++;
                }
                else
                {
                    await using var update = cn.CreateCommand();
                    update.Transaction = tx;
                    update.CommandText = "UPDATE source_strings SET current_source_version_id=@version WHERE id=@id";
                    update.Parameters.AddWithValue("@version", versionId);
                    update.Parameters.AddWithValue("@id", old.Id);
                    await update.ExecuteNonQueryAsync(ct);
                    unchanged++;
                }
            }
        }

        foreach (var (key, old) in existing)
        {
            if (old.Status == "active" && !flat.ContainsKey(key))
            {
                await using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE source_strings SET status='deleted',current_source_version_id=@version,updated_at=NOW() WHERE id=@id;
UPDATE translations SET status=CASE WHEN status='draft' THEN 'draft' ELSE 'needs_review' END,row_version=row_version+1,updated_at=NOW() WHERE string_id=@id;";
                cmd.Parameters.AddWithValue("@version", versionId);
                cmd.Parameters.AddWithValue("@id", old.Id);
                await cmd.ExecuteNonQueryAsync(ct);
                await AddChangeAsync(cn, tx, versionId, old.Id, "deleted", old.Value, null, ct);
                deleted++;
            }
        }

        await tx.CommitAsync(ct);
        return new { versionId, versionName, total=flat.Count, added, changed, unchanged, deleted };
    }

    public async Task<object[]> ChangesAsync(long projectId, long versionId, long? compareToVersionId = null, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await RequireVersionAsync(cn, projectId, versionId, ct);
        if (compareToVersionId.HasValue)
        {
            await RequireVersionAsync(cn, projectId, compareToVersionId.Value, ct);
            var before = await SnapshotAsync(cn, compareToVersionId.Value, ct);
            var after = await SnapshotAsync(cn, versionId, ct);
            foreach (var key in before.Keys.Union(after.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
            {
                var had = before.TryGetValue(key, out var oldValue);
                var has = after.TryGetValue(key, out var newValue);
                if (had && has && string.Equals(oldValue, newValue, StringComparison.Ordinal)) continue;
                list.Add(new { type = !had ? "new" : !has ? "deleted" : "changed", key, oldValue, newValue });
            }
            return [.. list];
        }
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT sc.change_type,ss.key_path,sc.old_value,sc.new_value
FROM source_changes sc JOIN source_strings ss ON ss.id=sc.string_id
JOIN source_versions sv ON sv.id=sc.source_version_id
WHERE sv.project_id=@project AND sv.id=@version ORDER BY ss.key_path";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@version", versionId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { type=rd.GetString(0), key=rd.GetString(1), oldValue=rd.IsDBNull(2)?null:rd.GetString(2), newValue=rd.IsDBNull(3)?null:rd.GetString(3) });
        return [.. list];
    }

    public async Task<object[]> ListAsync(long projectId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT v.id,v.version_name,v.imported_at,u.display_name,v.imported_by,
COALESCE(s.key_count,0),COALESCE(c.added,0),COALESCE(c.changed,0),COALESCE(c.deleted,0),COALESCE(c.restored,0)
FROM source_versions v JOIN users u ON u.id=v.imported_by
LEFT JOIN (SELECT source_version_id,COUNT(*) key_count FROM source_string_versions GROUP BY source_version_id) s ON s.source_version_id=v.id
LEFT JOIN (SELECT source_version_id,SUM(change_type='new') added,SUM(change_type IN ('changed','restored')) changed,
SUM(change_type='deleted') deleted,SUM(change_type='restored') restored FROM source_changes GROUP BY source_version_id) c ON c.source_version_id=v.id
WHERE v.project_id=@project ORDER BY v.id DESC";
        cmd.Parameters.AddWithValue("@project", projectId);
        var list = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var keyCount = Convert.ToInt32(rd.GetValue(5));
            var added = Convert.ToInt32(rd.GetValue(6));
            var changed = Convert.ToInt32(rd.GetValue(7));
            list.Add(new { id = rd.GetInt64(0), versionName = rd.GetString(1), importedAt = rd.GetDateTime(2), importedBy = rd.GetString(3),
                importedById = rd.GetInt64(4), keyCount, added, changed, deleted = Convert.ToInt32(rd.GetValue(8)),
                restored = Convert.ToInt32(rd.GetValue(9)), unchanged = keyCount - added - changed });
        }
        return [.. list];
    }

    public async Task<object[]> KeysAsync(long projectId, string? q, string? status, CancellationToken ct = default)
    {
        status = (status ?? "").Trim().ToLowerInvariant();
        if (status is not ("" or "all" or "active" or "deleted")) throw ApiException.BadRequest("Trạng thái khóa phải là active, deleted hoặc all.");
        var query = (q ?? "").Trim().ToLowerInvariant();
        if (query.Length > 500) throw ApiException.BadRequest("Từ khóa tìm kiếm tối đa 500 ký tự.");
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT id,key_path,source_text,status,current_source_version_id,updated_at FROM source_strings
WHERE project_id=@project AND (@status IN ('','all') OR status=@status)
AND (@q='' OR LOCATE(@q,LOWER(key_path))>0 OR LOCATE(@q,LOWER(source_text))>0) ORDER BY key_path";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@q", query);
        var list = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { id = rd.GetInt64(0), key = rd.GetString(1), sourceText = rd.GetString(2),
            status = rd.GetString(3), sourceVersionId = rd.GetInt64(4), updatedAt = rd.GetDateTime(5) });
        return [.. list];
    }

    private static async Task RequireVersionAsync(MySqlConnection cn, long projectId, long versionId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM source_versions WHERE id=@version AND project_id=@project";
        cmd.Parameters.AddWithValue("@version", versionId);
        cmd.Parameters.AddWithValue("@project", projectId);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw ApiException.NotFound("Không tìm thấy phiên bản nguồn trong dự án.");
    }

    private static async Task<Dictionary<string, string>> SnapshotAsync(MySqlConnection cn, long versionId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT key_path,source_text FROM source_string_versions WHERE source_version_id=@version";
        cmd.Parameters.AddWithValue("@version", versionId);
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) snapshot.Add(rd.GetString(0), rd.GetString(1));
        return snapshot;
    }

    private static async Task AddSnapshotAsync(MySqlConnection cn, MySqlTransaction tx, long versionId, long stringId, string key, string value, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO source_string_versions(source_version_id,string_id,key_path,source_text) VALUES(@v,@s,@k,@t)";
        cmd.Parameters.AddWithValue("@v", versionId); cmd.Parameters.AddWithValue("@s", stringId); cmd.Parameters.AddWithValue("@k", key); cmd.Parameters.AddWithValue("@t", value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddChangeAsync(MySqlConnection cn, MySqlTransaction tx, long versionId, long stringId, string type, string? oldValue, string? newValue, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO source_changes(source_version_id,string_id,change_type,old_value,new_value) VALUES(@v,@s,@type,@old,@new)";
        cmd.Parameters.AddWithValue("@v", versionId); cmd.Parameters.AddWithValue("@s", stringId); cmd.Parameters.AddWithValue("@type", type); cmd.Parameters.AddWithValue("@old", (object?)oldValue ?? DBNull.Value); cmd.Parameters.AddWithValue("@new", (object?)newValue ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
