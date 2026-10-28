using System.Text.RegularExpressions;
using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed partial class LocaleService(Db db)
{
    [GeneratedRegex(@"^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*$")]
    private static partial Regex LocaleRegex();

    internal static string NormalizeCode(string? value)
    {
        var code = (value ?? "").Trim();
        if (code.Length > 20 || !LocaleRegex().IsMatch(code))
            throw ApiException.BadRequest("Mã ngôn ngữ không hợp lệ. Ví dụ: vi, en, zh, en-US; tối đa 20 ký tự.");
        return code.ToLowerInvariant();
    }

    public async Task<object[]> ForProjectAsync(long projectId, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT l.code,COALESCE(pl.display_name,l.name),pl.is_source
FROM project_locales pl JOIN locales l ON l.code=pl.locale_code
WHERE pl.project_id=@project ORDER BY pl.is_source DESC,l.code";
        cmd.Parameters.AddWithValue("@project", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { code=rd.GetString(0), name=rd.GetString(1), isSource=rd.GetBoolean(2) });
        return [.. list];
    }

    public async Task AttachAsync(long projectId, string? localeCode, CancellationToken ct = default)
    {
        var code = NormalizeCode(localeCode);
        var name = code switch { "vi" => "Tiếng Việt", "en" => "English", "zh" => "中文", "ja" => "日本語", "ko" => "한국어", "fr" => "Français", "de" => "Deutsch", _ => code };
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ProjectWriteLock.AcquireAsync(cn, tx, projectId, ct);
        await using var c1 = cn.CreateCommand();
        c1.Transaction = tx;
        c1.CommandText = "INSERT IGNORE INTO locales(code,name) VALUES(@code,@name)";
        c1.Parameters.AddWithValue("@code", code);
        c1.Parameters.AddWithValue("@name", name);
        await c1.ExecuteNonQueryAsync(ct);
        await using var c2 = cn.CreateCommand();
        c2.Transaction = tx;
        c2.CommandText = "INSERT INTO project_locales(project_id,locale_code,is_source) VALUES(@project,@code,0)";
        c2.Parameters.AddWithValue("@project", projectId);
        c2.Parameters.AddWithValue("@code", code);
        await c2.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task UpdateAsync(long projectId, string code, string? name, CancellationToken ct = default)
    {
        code = NormalizeCode(code);
        name = (name ?? "").Trim();
        if (name.Length is < 1 or > 80) throw ApiException.BadRequest("Tên ngôn ngữ phải có từ 1 đến 80 ký tự.");
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ProjectWriteLock.AcquireAsync(cn, tx, projectId, ct);
        await RequireTargetAsync(cn, tx, projectId, code, ct);
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE project_locales SET display_name=@name WHERE project_id=@project AND locale_code=@code";
        cmd.Parameters.AddWithValue("@name", name);
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@code", code);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task DetachAsync(long projectId, string code, CancellationToken ct = default)
    {
        code = NormalizeCode(code);
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await ProjectWriteLock.AcquireAsync(cn, tx, projectId, ct);
        await RequireTargetAsync(cn, tx, projectId, code, ct);
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT
EXISTS(SELECT 1 FROM translations t JOIN source_strings s ON s.id=t.string_id WHERE s.project_id=@project AND t.locale_code=@code)
OR EXISTS(SELECT 1 FROM release_files f JOIN releases r ON r.id=f.release_id WHERE r.project_id=@project AND f.locale_code=@code)";
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@code", code);
            if (Convert.ToBoolean(await cmd.ExecuteScalarAsync(ct)))
                throw ApiException.Conflict("Không thể gỡ ngôn ngữ đã có bản dịch, phân công hoặc gói phát hành. Dữ liệu và lịch sử được giữ nguyên.");
        }
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM project_locales WHERE project_id=@project AND locale_code=@code";
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@code", code);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static async Task RequireTargetAsync(MySqlConnection cn, MySqlTransaction tx, long projectId, string code, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT is_source FROM project_locales WHERE project_id=@project AND locale_code=@code FOR UPDATE";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@code", code);
        var value = await cmd.ExecuteScalarAsync(ct);
        if (value is null) throw ApiException.NotFound("Ngôn ngữ chưa được gắn vào dự án.");
        if (Convert.ToBoolean(value)) throw ApiException.BadRequest("Không thể sửa hoặc gỡ ngôn ngữ nguồn. Ngôn ngữ nguồn được thiết lập khi tạo dự án.");
    }
}
