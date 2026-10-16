using System.Security.Cryptography;
using System.Text;
using TranslationManager.Api.Data;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class ReleaseService(Db db, IConfiguration configuration)
{
    public async Task<object> ValidateAsync(long projectId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        int activeStrings;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM source_strings WHERE project_id=@project AND status='active'";
            cmd.Parameters.AddWithValue("@project", projectId);
            activeStrings = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        if (activeStrings == 0) return new { ready=false, activeStrings=0, problems=new[]{"Dự án chưa có chuỗi nguồn."} };

        var problems = new List<string>();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"SELECT pl.locale_code,
SUM(CASE WHEN t.id IS NOT NULL AND t.status='approved' THEN 1 ELSE 0 END) approved_count
FROM project_locales pl
LEFT JOIN source_strings ss ON ss.project_id=pl.project_id AND ss.status='active'
LEFT JOIN translations t ON t.string_id=ss.id AND t.locale_code=pl.locale_code
WHERE pl.project_id=@project AND pl.is_source=0
GROUP BY pl.locale_code";
            cmd.Parameters.AddWithValue("@project", projectId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var locale = rd.GetString(0); var approved = Convert.ToInt32(rd.GetValue(1));
                if (approved != activeStrings) problems.Add($"{locale}: đã xác nhận {approved}/{activeStrings} chuỗi.");
            }
        }
        return new { ready=problems.Count==0, activeStrings, problems };
    }

    public async Task<object> PublishAsync(long projectId, long userId, string versionName, CancellationToken ct = default)
    {
        var validation = await ValidateAsync(projectId, ct);
        var ready = (bool)validation.GetType().GetProperty("ready")!.GetValue(validation)!;
        if (!ready) throw ApiException.BadRequest("Chưa đủ điều kiện phát hành.", validation);
        if (string.IsNullOrWhiteSpace(versionName)) throw ApiException.BadRequest("Thiếu tên phiên bản phát hành.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        string slug;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT slug FROM projects WHERE id=@project";
            cmd.Parameters.AddWithValue("@project", projectId);
            slug = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? throw ApiException.NotFound("Không tìm thấy dự án.");
        }

        long releaseId;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO releases(project_id,version_name,published_by) VALUES(@project,@version,@user); SELECT LAST_INSERT_ID();";
            cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@version", versionName.Trim()); cmd.Parameters.AddWithValue("@user", userId);
            releaseId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }

        var locales = new List<(string Code,bool IsSource)>();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT locale_code,is_source FROM project_locales WHERE project_id=@project ORDER BY is_source DESC,locale_code";
            cmd.Parameters.AddWithValue("@project", projectId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) locales.Add((rd.GetString(0), rd.GetBoolean(1)));
        }

        var releaseRoot = Path.GetFullPath(configuration["Storage:ReleasePath"] ?? "storage/releases");
        var folder = Path.Combine(releaseRoot, slug, versionName.Trim());
        Directory.CreateDirectory(folder);
        var files = new List<object>();

        foreach (var locale in locales)
        {
            var pairs = new List<KeyValuePair<string,string>>();
            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = locale.IsSource
                ? "SELECT key_path,source_text FROM source_strings WHERE project_id=@project AND status='active' ORDER BY key_path"
                : @"SELECT ss.key_path,t.value FROM source_strings ss JOIN translations t ON t.string_id=ss.id AND t.locale_code=@locale
WHERE ss.project_id=@project AND ss.status='active' AND t.status='approved' ORDER BY ss.key_path";
            cmd.Parameters.AddWithValue("@project", projectId);
            if (!locale.IsSource) cmd.Parameters.AddWithValue("@locale", locale.Code);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) pairs.Add(new(rd.GetString(0), rd.GetString(1)));

            var json = JsonTree.Unflatten(pairs);
            var bytes = Encoding.UTF8.GetBytes(json);
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var fileName = $"{locale.Code}.json";
            await File.WriteAllBytesAsync(Path.Combine(folder, fileName), bytes, ct);

            await using var insert = cn.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = "INSERT INTO release_files(release_id,locale_code,file_name,content_json,sha256) VALUES(@release,@locale,@file,@json,@sha)";
            insert.Parameters.AddWithValue("@release", releaseId); insert.Parameters.AddWithValue("@locale", locale.Code); insert.Parameters.AddWithValue("@file", fileName); insert.Parameters.AddWithValue("@json", json); insert.Parameters.AddWithValue("@sha", sha);
            await insert.ExecuteNonQueryAsync(ct);
            files.Add(new { locale=locale.Code, fileName, sha256=sha });
        }

        await tx.CommitAsync(ct);
        return new { releaseId, version=versionName.Trim(), files };
    }

    public async Task<(string FileName,string Json)> FileAsync(long projectId, long releaseId, string locale, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT rf.file_name,rf.content_json FROM release_files rf JOIN releases r ON r.id=rf.release_id
WHERE r.id=@release AND r.project_id=@project AND rf.locale_code=@locale";
        cmd.Parameters.AddWithValue("@release", releaseId); cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@locale", locale);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Không tìm thấy file trong bản phát hành.");
        return (rd.GetString(0), rd.GetString(1));
    }
}
