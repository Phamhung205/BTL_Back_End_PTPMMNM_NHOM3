using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class SourceVersionService(Db db)
{
    public async Task<object> ImportAsync(long projectId, long userId, ImportSourceRequest request, CancellationToken ct = default)
    {
        var flat = JsonTree.Flatten(request.Data);
        if (flat.Count == 0) throw ApiException.BadRequest("Tệp nguồn không có khóa dịch.");
        var versionName = request.VersionName.Trim();
        if (versionName.Length == 0) throw ApiException.BadRequest("Thiếu tên phiên bản nguồn.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

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
UPDATE translations SET status='needs_review',row_version=row_version+1,updated_at=NOW() WHERE string_id=@id AND status<>'draft';";
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
                cmd.CommandText = "UPDATE source_strings SET status='deleted',current_source_version_id=@version,updated_at=NOW() WHERE id=@id";
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

    public async Task<object[]> ChangesAsync(long projectId, long versionId, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
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
