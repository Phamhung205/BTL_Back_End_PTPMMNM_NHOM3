using TranslationManager.Api.Data;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class LocaleService(Db db)
{
    public async Task<object[]> ForProjectAsync(long projectId, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT l.code,l.name,pl.is_source
FROM project_locales pl JOIN locales l ON l.code=pl.locale_code
WHERE pl.project_id=@project ORDER BY pl.is_source DESC,l.code";
        cmd.Parameters.AddWithValue("@project", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) list.Add(new { code=rd.GetString(0), name=rd.GetString(1), isSource=rd.GetBoolean(2) });
        return [.. list];
    }

    public async Task AttachAsync(long projectId, string localeCode, CancellationToken ct = default)
    {
        var code = localeCode.Trim();
        if (code.Length is < 2 or > 20) throw ApiException.BadRequest("Mã ngôn ngữ không hợp lệ.");
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await using var c1 = cn.CreateCommand();
        c1.Transaction = tx;
        c1.CommandText = "INSERT IGNORE INTO locales(code,name) VALUES(@code,@code)";
        c1.Parameters.AddWithValue("@code", code);
        await c1.ExecuteNonQueryAsync(ct);
        await using var c2 = cn.CreateCommand();
        c2.Transaction = tx;
        c2.CommandText = "INSERT INTO project_locales(project_id,locale_code,is_source) VALUES(@project,@code,0)";
        c2.Parameters.AddWithValue("@project", projectId);
        c2.Parameters.AddWithValue("@code", code);
        await c2.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}
