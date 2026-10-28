using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

/// <summary>Anonymous, shared Vietnamese-first translation suggestions.</summary>
public sealed class TranslationMemoryService(Db db)
{
    private const string PairSql = @"
SELECT t.locale_code AS target_locale_code, ss.source_text AS source_text, t.value AS target_text
FROM projects p
JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
JOIN translations t ON t.string_id=ss.id AND t.locale_code<>'vi' AND t.status IN ('translated','approved')
WHERE p.source_locale_code='vi' AND ss.id=@string
UNION ALL
SELECT t.locale_code, vi.value, t.value
FROM projects p
JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
JOIN translations vi ON vi.string_id=ss.id AND vi.locale_code='vi' AND vi.status IN ('translated','approved')
JOIN translations t ON t.string_id=ss.id AND t.locale_code<>'vi' AND t.status IN ('translated','approved')
WHERE p.source_locale_code<>'vi' AND ss.id=@string
UNION ALL
SELECT p.source_locale_code, vi.value, ss.source_text
FROM projects p
JOIN source_strings ss ON ss.project_id=p.id AND ss.status='active'
JOIN translations vi ON vi.string_id=ss.id AND vi.locale_code='vi' AND vi.status IN ('translated','approved')
WHERE p.source_locale_code<>'vi' AND ss.id=@string";

    internal static async Task CaptureForStringAsync(MySqlConnection cn, MySqlTransaction tx, long stringId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(@$"INSERT IGNORE INTO translation_memory
(source_locale_code,target_locale_code,source_text,target_text,source_hash,target_hash)
SELECT 'vi', pair.target_locale_code, pair.source_text, pair.target_text,
       SHA2(TRIM(pair.source_text),256), SHA2(TRIM(pair.target_text),256)
FROM ({PairSql}) pair
WHERE TRIM(pair.source_text)<>'' AND TRIM(pair.target_text)<>''", cn, tx);
        cmd.Parameters.AddWithValue("@string", stringId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<object> LanguagesAsync(CancellationToken ct)
    {
        var items = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT l.code,l.name,COUNT(m.id)
FROM locales l
LEFT JOIN translation_memory m ON m.target_locale_code=l.code AND m.source_locale_code='vi'
WHERE l.code<>'vi'
GROUP BY l.code,l.name ORDER BY l.name,l.code";
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) items.Add(new { code = rd.GetString(0), name = rd.GetString(1), count = rd.GetInt64(2) });
        return new { items };
    }

    public async Task<object> SearchAsync(string? targetLocaleCode, string? query, int page, CancellationToken ct)
    {
        var locale = LocaleService.NormalizeCode(targetLocaleCode);
        if (locale == "vi") throw ApiException.BadRequest("Chọn ngôn ngữ đích khác tiếng Việt.");
        if (page is < 1 or > 999999) throw ApiException.BadRequest("Trang không hợp lệ.");
        var search = Search(query);
        const string from = " FROM translation_memory m WHERE m.source_locale_code='vi' AND m.target_locale_code=@locale AND (@q='' OR m.source_text LIKE @q OR m.target_text LIKE @q)";
        await using var cn = await db.OpenAsync(ct);
        long total;
        await using (var count = new MySqlCommand("SELECT COUNT(*)" + from, cn))
        {
            count.Parameters.AddWithValue("@locale", locale);
            count.Parameters.AddWithValue("@q", search);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }
        var items = new List<object>();
        await using (var cmd = new MySqlCommand("SELECT m.id,m.source_text,m.target_locale_code,m.target_text" + from + " ORDER BY m.source_text,m.target_text,m.id LIMIT 50 OFFSET @offset", cn))
        {
            cmd.Parameters.AddWithValue("@locale", locale);
            cmd.Parameters.AddWithValue("@q", search);
            cmd.Parameters.AddWithValue("@offset", (page - 1) * 50);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct)) items.Add(new { id = rd.GetInt64(0), sourceLocaleCode = "vi", sourceText = rd.GetString(1), targetLocaleCode = rd.GetString(2), targetText = rd.GetString(3) });
        }
        return new { items, total, page, pageSize = 50, hasMore = page * 50L < total };
    }

    private static string Search(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length > 200) throw ApiException.BadRequest("Từ khóa tìm kiếm tối đa 200 ký tự.");
        return value.Length == 0 ? "" : "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }
}
