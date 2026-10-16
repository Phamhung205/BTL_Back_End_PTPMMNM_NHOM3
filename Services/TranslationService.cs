using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Models;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed class TranslationService(Db db)
{
    public async Task<object[]> ListAsync(long projectId, string locale, string? q, string? status, CancellationToken ct = default)
    {
        var list = new List<object>();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT ss.id,ss.key_path,ss.source_text,ss.status,
 t.id,t.value,t.status,t.assigned_to,t.row_version,t.updated_at
FROM source_strings ss
LEFT JOIN translations t ON t.string_id=ss.id AND t.locale_code=@locale
WHERE ss.project_id=@project
AND (@q='' OR ss.key_path LIKE CONCAT('%',@q,'%') OR ss.source_text LIKE CONCAT('%',@q,'%'))
AND (@status='' OR COALESCE(t.status,'missing')=@status)
ORDER BY ss.key_path";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@locale", locale);
        cmd.Parameters.AddWithValue("@q", q?.Trim() ?? "");
        cmd.Parameters.AddWithValue("@status", status?.Trim() ?? "");
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new
            {
                stringId=rd.GetInt64(0), key=rd.GetString(1), source=rd.GetString(2), sourceStatus=rd.GetString(3),
                translationId=rd.IsDBNull(4)?null:rd.GetInt64(4), value=rd.IsDBNull(5)?null:rd.GetString(5),
                status=rd.IsDBNull(6)?"missing":rd.GetString(6), assignedTo=rd.IsDBNull(7)?null:rd.GetInt64(7),
                rowVersion=rd.IsDBNull(8)?0:rd.GetInt32(8), updatedAt=rd.IsDBNull(9)?null:rd.GetDateTime(9)
            });
        }
        return [.. list];
    }

    public async Task<object> SaveAsync(long projectId, long userId, SaveTranslationRequest request, CancellationToken ct = default)
    {
        var status = request.Status.Trim().ToLowerInvariant();
        if (status is not (TranslationStatus.Draft or TranslationStatus.Translated))
            throw ApiException.BadRequest("Người dịch chỉ được lưu trạng thái draft hoặc translated.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        string source;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT source_text FROM source_strings WHERE id=@id AND project_id=@project AND status='active'";
            cmd.Parameters.AddWithValue("@id", request.StringId);
            cmd.Parameters.AddWithValue("@project", projectId);
            source = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? throw ApiException.NotFound("Không tìm thấy khóa nguồn đang hoạt động.");
        }

        if (string.IsNullOrWhiteSpace(request.Value)) throw ApiException.BadRequest("Bản dịch không được để trống.");
        if (status != TranslationStatus.Draft && !PlaceholderValidator.Match(source, request.Value, out var detail))
            throw ApiException.BadRequest("Biến thay thế trong bản dịch không khớp chuỗi nguồn.", detail);

        long? id = null; int? currentVersion = null; string? oldValue = null;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id,row_version,value FROM translations WHERE string_id=@string AND locale_code=@locale LIMIT 1";
            cmd.Parameters.AddWithValue("@string", request.StringId);
            cmd.Parameters.AddWithValue("@locale", request.LocaleCode);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { id=rd.GetInt64(0); currentVersion=rd.GetInt32(1); oldValue=rd.GetString(2); }
        }

        if (id is null)
        {
            if (request.ExpectedRowVersion != 0) throw ApiException.Conflict("Bản dịch đã thay đổi. Hãy tải lại dữ liệu.");
            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"INSERT INTO translations(string_id,locale_code,value,status,assigned_to,row_version,updated_by)
VALUES(@string,@locale,@value,@status,@user,1,@user); SELECT LAST_INSERT_ID();";
            cmd.Parameters.AddWithValue("@string", request.StringId); cmd.Parameters.AddWithValue("@locale", request.LocaleCode);
            cmd.Parameters.AddWithValue("@value", request.Value); cmd.Parameters.AddWithValue("@status", status); cmd.Parameters.AddWithValue("@user", userId);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        else
        {
            if (currentVersion != request.ExpectedRowVersion)
                throw ApiException.Conflict("Có người khác đã sửa bản dịch này. Không ghi đè âm thầm.", new { currentRowVersion=currentVersion });

            await using var cmd = cn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"UPDATE translations SET value=@value,status=@status,row_version=row_version+1,updated_by=@user,updated_at=NOW()
WHERE id=@id AND row_version=@expected";
            cmd.Parameters.AddWithValue("@value", request.Value); cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@user", userId); cmd.Parameters.AddWithValue("@id", id.Value); cmd.Parameters.AddWithValue("@expected", request.ExpectedRowVersion);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ApiException.Conflict("Bản dịch vừa bị thay đổi. Hãy tải lại.");
        }

        await using (var history = cn.CreateCommand())
        {
            history.Transaction = tx;
            history.CommandText = "INSERT INTO translation_history(translation_id,user_id,old_value,new_value,action) VALUES(@id,@user,@old,@new,'saved')";
            history.Parameters.AddWithValue("@id", id.Value); history.Parameters.AddWithValue("@user", userId);
            history.Parameters.AddWithValue("@old", (object?)oldValue ?? DBNull.Value); history.Parameters.AddWithValue("@new", request.Value);
            await history.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return new { id, rowVersion=(currentVersion ?? 0)+1, status };
    }

    public async Task AssignAsync(long projectId, long translationId, long? userId, int expectedRowVersion, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"UPDATE translations t JOIN source_strings ss ON ss.id=t.string_id
SET t.assigned_to=@user,t.row_version=t.row_version+1,t.updated_at=NOW()
WHERE t.id=@id AND ss.project_id=@project AND t.row_version=@expected";
        cmd.Parameters.AddWithValue("@user", (object?)userId ?? DBNull.Value); cmd.Parameters.AddWithValue("@id", translationId);
        cmd.Parameters.AddWithValue("@project", projectId); cmd.Parameters.AddWithValue("@expected", expectedRowVersion);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ApiException.Conflict("Không thể phân công vì dữ liệu đã thay đổi.");
    }

    public async Task AddCommentAsync(long projectId, long translationId, long userId, string body, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(body)) throw ApiException.BadRequest("Nhận xét không được để trống.");
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"INSERT INTO translation_comments(translation_id,user_id,body)
SELECT t.id,@user,@body FROM translations t JOIN source_strings ss ON ss.id=t.string_id
WHERE t.id=@id AND ss.project_id=@project";
        cmd.Parameters.AddWithValue("@user", userId); cmd.Parameters.AddWithValue("@body", body.Trim()); cmd.Parameters.AddWithValue("@id", translationId); cmd.Parameters.AddWithValue("@project", projectId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ApiException.NotFound("Không tìm thấy bản dịch.");
    }

    public async Task ReviewAsync(long projectId, long translationId, long reviewerId, bool approve, string? note, int expectedRowVersion, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        long stringId; string source; string value;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = @"SELECT t.string_id,ss.source_text,t.value FROM translations t JOIN source_strings ss ON ss.id=t.string_id
WHERE t.id=@id AND ss.project_id=@project AND ss.status='active'";
            cmd.Parameters.AddWithValue("@id", translationId); cmd.Parameters.AddWithValue("@project", projectId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Không tìm thấy bản dịch.");
            stringId=rd.GetInt64(0); source=rd.GetString(1); value=rd.GetString(2);
        }
        if (approve && !PlaceholderValidator.Match(source, value, out var detail))
            throw ApiException.BadRequest("Không thể xác nhận: biến thay thế không hợp lệ.", detail);

        var newStatus = approve ? TranslationStatus.Approved : TranslationStatus.NeedsReview;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE translations SET status=@status,row_version=row_version+1,updated_by=@user,updated_at=NOW() WHERE id=@id AND row_version=@expected";
            cmd.Parameters.AddWithValue("@status", newStatus); cmd.Parameters.AddWithValue("@user", reviewerId); cmd.Parameters.AddWithValue("@id", translationId); cmd.Parameters.AddWithValue("@expected", expectedRowVersion);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ApiException.Conflict("Bản dịch đã thay đổi trước khi xác nhận.");
        }
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO reviews(translation_id,reviewer_id,decision,note) VALUES(@id,@user,@decision,@note)";
            cmd.Parameters.AddWithValue("@id", translationId); cmd.Parameters.AddWithValue("@user", reviewerId); cmd.Parameters.AddWithValue("@decision", approve?"approved":"rejected"); cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
