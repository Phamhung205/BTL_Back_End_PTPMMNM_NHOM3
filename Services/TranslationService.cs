using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Models;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

/// <summary>
/// Module Translation (Phạm Thế Đức): danh sách/lọc, lưu nháp, gửi review, phân công,
/// bình luận, lịch sử, duyệt/từ chối và phát hiện xung đột bằng row_version.
/// </summary>
public sealed class TranslationService(Db db)
{
    private const int MaxValueLength = 10000;
    private const int MaxCommentLength = 2000;
    private const int MaxBulkAssign = 500;

    // Trạng thái hiển thị: chưa có dòng hoặc dòng chỉ có phân công (value NULL) => missing.
    private const string DisplayStatusSql = "CASE WHEN t.id IS NULL OR t.value IS NULL THEN 'missing' ELSE t.status END";

    private sealed record TranslationRow(
        long Id,
        long StringId,
        string LocaleCode,
        string? Value,
        string Status,
        long? AssignedTo,
        string? AssignedToName,
        int RowVersion,
        string? UpdatedByName,
        DateTime UpdatedAt,
        string SourceText,
        string SourceStatus)
    {
        public string DisplayStatus => Value is null ? TranslationStatus.Missing : Status;
    }

    // ===================== Danh sách / tìm kiếm / lọc =====================

    public async Task<object[]> ListAsync(long projectId, long currentUserId, string? locale, string? q, string? status, string? assignee, CancellationToken ct = default)
    {
        var statusFilter = (status ?? "").Trim().ToLowerInvariant();
        if (statusFilter.Length > 0 && !TranslationStatus.Filterable.Contains(statusFilter))
            throw ApiException.BadRequest("Trạng thái lọc không hợp lệ.");

        // 0 = tất cả, -1 = chưa phân công, >0 = id người được phân công
        long assigneeFilter = 0;
        var assigneeText = (assignee ?? "").Trim().ToLowerInvariant();
        if (assigneeText == "me") assigneeFilter = currentUserId;
        else if (assigneeText == "none") assigneeFilter = -1;
        else if (assigneeText.Length > 0 && (!long.TryParse(assigneeText, out assigneeFilter) || assigneeFilter <= 0))
            throw ApiException.BadRequest("Bộ lọc người được phân công không hợp lệ.");

        await using var cn = await db.OpenAsync(ct);
        var localeCode = await RequireTargetLocaleAsync(cn, null, projectId, locale, ct);

        var list = new List<object>();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"SELECT ss.id, ss.key_path, ss.source_text,
 t.id, t.value, {DisplayStatusSql} AS display_status, t.assigned_to, au.display_name,
 t.row_version, t.updated_at, uu.display_name,
 (SELECT COUNT(*) FROM translation_comments c WHERE c.translation_id=t.id) AS comment_count,
 (SELECT r.decision FROM reviews r WHERE r.translation_id=t.id ORDER BY r.id DESC LIMIT 1) AS last_decision,
 (SELECT r.note FROM reviews r WHERE r.translation_id=t.id ORDER BY r.id DESC LIMIT 1) AS last_note
FROM source_strings ss
LEFT JOIN translations t ON t.string_id=ss.id AND t.locale_code=@locale
LEFT JOIN users au ON au.id=t.assigned_to
LEFT JOIN users uu ON uu.id=t.updated_by
WHERE ss.project_id=@project AND ss.status='active'
AND (@q='' OR ss.key_path LIKE CONCAT('%',@q,'%') OR ss.source_text LIKE CONCAT('%',@q,'%') OR t.value LIKE CONCAT('%',@q,'%'))
AND (@status='' OR {DisplayStatusSql}=@status)
AND (@assignee=0 OR (@assignee=-1 AND t.assigned_to IS NULL) OR t.assigned_to=@assignee)
ORDER BY ss.key_path";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@locale", localeCode);
        cmd.Parameters.AddWithValue("@q", EscapeLike(q?.Trim() ?? ""));
        cmd.Parameters.AddWithValue("@status", statusFilter);
        cmd.Parameters.AddWithValue("@assignee", assigneeFilter);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            var source = rd.GetString(2);
            list.Add(new
            {
                stringId = rd.GetInt64(0),
                key = rd.GetString(1),
                source,
                placeholders = PlaceholderValidator.Extract(source),
                translationId = rd.IsDBNull(3) ? (long?)null : rd.GetInt64(3),
                value = rd.IsDBNull(4) ? null : rd.GetString(4),
                status = rd.GetString(5),
                assignedTo = rd.IsDBNull(6) ? (long?)null : rd.GetInt64(6),
                assignedToName = rd.IsDBNull(7) ? null : rd.GetString(7),
                rowVersion = rd.IsDBNull(8) ? 0 : rd.GetInt32(8),
                updatedAt = rd.IsDBNull(9) ? (DateTime?)null : rd.GetDateTime(9),
                updatedByName = rd.IsDBNull(10) ? null : rd.GetString(10),
                commentCount = Convert.ToInt32(rd.GetValue(11)),
                lastReviewDecision = rd.IsDBNull(12) ? null : rd.GetString(12),
                lastReviewNote = rd.IsDBNull(13) ? null : rd.GetString(13)
            });
        }
        return [.. list];
    }

    public async Task<LocaleProgress[]> StatsAsync(long projectId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        return await LocaleProgressAsync(cn, null, projectId, ct);
    }

    /// <summary>Tiến độ dịch theo từng ngôn ngữ đích; dùng chung cho màn hình dịch và kiểm tra phát hành.</summary>
    internal static async Task<LocaleProgress[]> LocaleProgressAsync(MySqlConnection cn, MySqlTransaction? tx, long projectId, CancellationToken ct)
    {
        var list = new List<LocaleProgress>();
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT pl.locale_code, l.name,
 COUNT(ss.id),
 COALESCE(SUM(CASE WHEN ss.id IS NOT NULL AND (t.id IS NULL OR t.value IS NULL) THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN t.value IS NOT NULL AND t.status='draft' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN t.value IS NOT NULL AND t.status='translated' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN t.value IS NOT NULL AND t.status='needs_review' THEN 1 ELSE 0 END),0),
 COALESCE(SUM(CASE WHEN t.value IS NOT NULL AND t.status='approved' THEN 1 ELSE 0 END),0)
FROM project_locales pl
JOIN locales l ON l.code=pl.locale_code
LEFT JOIN source_strings ss ON ss.project_id=pl.project_id AND ss.status='active'
LEFT JOIN translations t ON t.string_id=ss.id AND t.locale_code=pl.locale_code
WHERE pl.project_id=@project AND pl.is_source=0
GROUP BY pl.locale_code, l.name
ORDER BY pl.locale_code";
        cmd.Parameters.AddWithValue("@project", projectId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new LocaleProgress(
                rd.GetString(0), rd.GetString(1),
                Convert.ToInt32(rd.GetValue(2)), Convert.ToInt32(rd.GetValue(3)), Convert.ToInt32(rd.GetValue(4)),
                Convert.ToInt32(rd.GetValue(5)), Convert.ToInt32(rd.GetValue(6)), Convert.ToInt32(rd.GetValue(7))));
        }
        return [.. list];
    }

    // ===================== Lưu nháp / gửi review =====================

    public async Task<object> SaveAsync(long projectId, long userId, string role, SaveTranslationRequest request, CancellationToken ct = default)
    {
        var status = (request.Status ?? TranslationStatus.Draft).Trim().ToLowerInvariant();
        if (status is not (TranslationStatus.Draft or TranslationStatus.Translated))
            throw ApiException.BadRequest("Chỉ được lưu nháp (draft) hoặc gửi review (translated).");

        var value = request.Value ?? "";
        if (string.IsNullOrWhiteSpace(value)) throw ApiException.BadRequest("Bản dịch không được để trống.");
        if (value.Length > MaxValueLength) throw ApiException.BadRequest($"Bản dịch tối đa {MaxValueLength} ký tự.");

        try
        {
            await using var cn = await db.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            var locale = await RequireTargetLocaleAsync(cn, tx, projectId, request.LocaleCode, ct);

            string source;
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT source_text FROM source_strings WHERE id=@id AND project_id=@project AND status='active'";
                cmd.Parameters.AddWithValue("@id", request.StringId);
                cmd.Parameters.AddWithValue("@project", projectId);
                source = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? throw ApiException.NotFound("Không tìm thấy khóa nguồn đang hoạt động.");
            }

            // Nháp được phép thiếu biến; gửi review thì bắt buộc khớp.
            var check = PlaceholderValidator.Check(source, value);
            if (status == TranslationStatus.Translated && !check.Ok)
                throw ApiException.BadRequest("Biến thay thế trong bản dịch không khớp chuỗi nguồn.", check);

            var current = await LockAsync(cn, tx, projectId, "t.string_id=@string AND t.locale_code=@locale",
                p => { p.AddWithValue("@string", request.StringId); p.AddWithValue("@locale", locale); }, ct);

            long id;
            int newVersion;
            if (current is null)
            {
                if (request.ExpectedRowVersion != 0)
                    throw ApiException.Conflict("Bản dịch đã thay đổi trên máy chủ. Hãy tải lại dữ liệu.");

                await using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO translations(string_id,locale_code,value,status,assigned_to,row_version,updated_by)
VALUES(@string,@locale,@value,@status,NULL,1,@user); SELECT LAST_INSERT_ID();";
                cmd.Parameters.AddWithValue("@string", request.StringId);
                cmd.Parameters.AddWithValue("@locale", locale);
                cmd.Parameters.AddWithValue("@value", value);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@user", userId);
                id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                newVersion = 1;
            }
            else
            {
                EnsureCanEdit(current, userId, role);
                if (current.RowVersion != request.ExpectedRowVersion) throw ConflictFor(current);

                await using var cmd = cn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"UPDATE translations SET value=@value,status=@status,row_version=row_version+1,updated_by=@user,updated_at=NOW()
WHERE id=@id AND row_version=@expected";
                cmd.Parameters.AddWithValue("@value", value);
                cmd.Parameters.AddWithValue("@status", status);
                cmd.Parameters.AddWithValue("@user", userId);
                cmd.Parameters.AddWithValue("@id", current.Id);
                cmd.Parameters.AddWithValue("@expected", request.ExpectedRowVersion);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ConflictFor(current);
                id = current.Id;
                newVersion = current.RowVersion + 1;
            }

            await AddHistoryAsync(cn, tx, id, userId, status == TranslationStatus.Draft ? "saved_draft" : "submitted", current?.Value, value, ct);
            await tx.CommitAsync(ct);
            return new { id, stringId = request.StringId, localeCode = locale, value, status, rowVersion = newVersion, placeholderCheck = check };
        }
        catch (MySqlException ex) when (IsConcurrencyError(ex))
        {
            throw ApiException.Conflict("Có người khác vừa lưu bản dịch này cùng lúc. Hãy tải lại dữ liệu.");
        }
    }

    // ===================== Phân công =====================

    public async Task<object> AssignAsync(long projectId, long managerId, long translationId, long? assigneeId, int expectedRowVersion, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        var assigneeName = assigneeId is null ? null : await RequireAssigneeAsync(cn, tx, projectId, assigneeId.Value, ct);

        var row = await LockByIdAsync(cn, tx, projectId, translationId, ct) ?? throw ApiException.NotFound("Không tìm thấy bản dịch.");
        if (row.RowVersion != expectedRowVersion)
            throw ConflictFor(row, "Không thể phân công vì bản dịch đã thay đổi. Hãy tải lại.");

        if (row.AssignedTo != assigneeId)
        {
            await SetAssigneeAsync(cn, tx, row.Id, assigneeId, ct);
            await AddHistoryAsync(cn, tx, row.Id, managerId, "assigned", row.AssignedToName, assigneeName, ct);
        }
        await tx.CommitAsync(ct);
        return new { translationId = row.Id, assignedTo = assigneeId, assignedToName = assigneeName, rowVersion = row.RowVersion };
    }

    /// <summary>Phân công hàng loạt; khóa chưa có bản dịch sẽ được tạo dòng trống (value NULL) để giữ người được giao.</summary>
    public async Task<object> BulkAssignAsync(long projectId, long managerId, BulkAssignRequest request, CancellationToken ct = default)
    {
        var ids = (request.StringIds ?? []).Distinct().ToArray();
        if (ids.Length == 0) throw ApiException.BadRequest("Chưa chọn khóa nào để phân công.");
        if (ids.Length > MaxBulkAssign) throw ApiException.BadRequest($"Mỗi lần chỉ phân công tối đa {MaxBulkAssign} khóa.");

        try
        {
            await using var cn = await db.OpenAsync(ct);
            await using var tx = await cn.BeginTransactionAsync(ct);
            var locale = await RequireTargetLocaleAsync(cn, tx, projectId, request.LocaleCode, ct);
            var assigneeName = request.UserId is null ? null : await RequireAssigneeAsync(cn, tx, projectId, request.UserId.Value, ct);

            var valid = new HashSet<long>();
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                var names = ids.Select((_, i) => $"@s{i}").ToArray();
                cmd.CommandText = $"SELECT id FROM source_strings WHERE project_id=@project AND status='active' AND id IN ({string.Join(',', names)})";
                cmd.Parameters.AddWithValue("@project", projectId);
                for (var i = 0; i < ids.Length; i++) cmd.Parameters.AddWithValue(names[i], ids[i]);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) valid.Add(rd.GetInt64(0));
            }
            var invalid = ids.Where(x => !valid.Contains(x)).ToArray();
            if (invalid.Length > 0)
                throw ApiException.BadRequest("Có khóa không tồn tại hoặc đã bị xóa khỏi nguồn.", new { invalid });

            var changed = 0;
            foreach (var stringId in ids)
            {
                var row = await LockAsync(cn, tx, projectId, "t.string_id=@string AND t.locale_code=@locale",
                    p => { p.AddWithValue("@string", stringId); p.AddWithValue("@locale", locale); }, ct);

                if (row is null)
                {
                    if (request.UserId is null) continue;
                    long newId;
                    await using (var cmd = cn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        cmd.CommandText = @"INSERT INTO translations(string_id,locale_code,value,status,assigned_to,row_version,updated_by)
VALUES(@string,@locale,NULL,'draft',@assignee,1,@manager); SELECT LAST_INSERT_ID();";
                        cmd.Parameters.AddWithValue("@string", stringId);
                        cmd.Parameters.AddWithValue("@locale", locale);
                        cmd.Parameters.AddWithValue("@assignee", request.UserId.Value);
                        cmd.Parameters.AddWithValue("@manager", managerId);
                        newId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
                    }
                    await AddHistoryAsync(cn, tx, newId, managerId, "assigned", null, assigneeName, ct);
                    changed++;
                }
                else if (row.AssignedTo != request.UserId)
                {
                    await SetAssigneeAsync(cn, tx, row.Id, request.UserId, ct);
                    await AddHistoryAsync(cn, tx, row.Id, managerId, "assigned", row.AssignedToName, assigneeName, ct);
                    changed++;
                }
            }

            await tx.CommitAsync(ct);
            return new { localeCode = locale, assigned = changed, unchanged = ids.Length - changed, assignedTo = request.UserId, assignedToName = assigneeName };
        }
        catch (MySqlException ex) when (IsConcurrencyError(ex))
        {
            throw ApiException.Conflict("Dữ liệu phân công vừa bị thay đổi cùng lúc. Hãy thử lại.");
        }
    }

    // ===================== Bình luận / lịch sử =====================

    public async Task<object> AddCommentAsync(long projectId, long translationId, long userId, string? body, CancellationToken ct = default)
    {
        var text = body?.Trim() ?? "";
        if (text.Length == 0) throw ApiException.BadRequest("Nhận xét không được để trống.");
        if (text.Length > MaxCommentLength) throw ApiException.BadRequest($"Nhận xét tối đa {MaxCommentLength} ký tự.");

        await using var cn = await db.OpenAsync(ct);
        await EnsureTranslationInProjectAsync(cn, projectId, translationId, ct);

        long id;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "INSERT INTO translation_comments(translation_id,user_id,body) VALUES(@id,@user,@body); SELECT LAST_INSERT_ID();";
            cmd.Parameters.AddWithValue("@id", translationId);
            cmd.Parameters.AddWithValue("@user", userId);
            cmd.Parameters.AddWithValue("@body", text);
            id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        }
        return (await QueryCommentsAsync(cn, "c.id=@id", id, ct))[0];
    }

    public async Task<object[]> ListCommentsAsync(long projectId, long translationId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await EnsureTranslationInProjectAsync(cn, projectId, translationId, ct);
        return await QueryCommentsAsync(cn, "c.translation_id=@id", translationId, ct);
    }

    public async Task<object[]> HistoryAsync(long projectId, long translationId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await EnsureTranslationInProjectAsync(cn, projectId, translationId, ct);

        var list = new List<object>();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT h.id,h.action,h.old_value,h.new_value,h.user_id,u.display_name,h.created_at
FROM translation_history h JOIN users u ON u.id=h.user_id
WHERE h.translation_id=@id ORDER BY h.id DESC LIMIT 100";
        cmd.Parameters.AddWithValue("@id", translationId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
        {
            list.Add(new
            {
                id = rd.GetInt64(0),
                action = rd.GetString(1),
                oldValue = rd.IsDBNull(2) ? null : rd.GetString(2),
                newValue = rd.IsDBNull(3) ? null : rd.GetString(3),
                userId = rd.GetInt64(4),
                userName = rd.GetString(5),
                createdAt = rd.GetDateTime(6)
            });
        }
        return [.. list];
    }

    // ===================== Duyệt / từ chối =====================

    public async Task<object> ReviewAsync(long projectId, long translationId, long reviewerId, bool approve, string? note, int expectedRowVersion, CancellationToken ct = default)
    {
        var reviewNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (!approve && reviewNote is null) throw ApiException.BadRequest("Từ chối cần ghi rõ lý do để người dịch sửa lại.");
        if (reviewNote?.Length > MaxCommentLength) throw ApiException.BadRequest($"Ghi chú tối đa {MaxCommentLength} ký tự.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        var row = await LockByIdAsync(cn, tx, projectId, translationId, ct) ?? throw ApiException.NotFound("Không tìm thấy bản dịch.");
        if (row.SourceStatus != "active") throw ApiException.BadRequest("Khóa nguồn đã bị xóa, không thể duyệt bản dịch này.");
        if (row.RowVersion != expectedRowVersion)
            throw ConflictFor(row, "Bản dịch đã thay đổi trước khi bạn duyệt. Hãy xem lại bản mới nhất.");
        if (row.Value is null) throw ApiException.BadRequest("Chưa có nội dung bản dịch để duyệt.");

        if (approve)
        {
            if (row.Status == TranslationStatus.Approved) throw ApiException.BadRequest("Bản dịch đã được duyệt trước đó.");
            if (row.Status == TranslationStatus.Draft) throw ApiException.BadRequest("Bản dịch đang là nháp, người dịch chưa gửi review.");
            var check = PlaceholderValidator.Check(row.SourceText, row.Value);
            if (!check.Ok) throw ApiException.BadRequest("Không thể duyệt: biến thay thế không khớp chuỗi nguồn.", check);
        }
        else if (row.Status == TranslationStatus.Draft)
        {
            throw ApiException.BadRequest("Bản nháp chưa được gửi review nên không thể từ chối.");
        }

        var newStatus = approve ? TranslationStatus.Approved : TranslationStatus.NeedsReview;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            // Giữ updated_by/updated_at là người & thời điểm sửa nội dung; người duyệt lưu ở bảng reviews.
            cmd.CommandText = "UPDATE translations SET status=@status,row_version=row_version+1,updated_at=updated_at WHERE id=@id AND row_version=@expected";
            cmd.Parameters.AddWithValue("@status", newStatus);
            cmd.Parameters.AddWithValue("@id", row.Id);
            cmd.Parameters.AddWithValue("@expected", expectedRowVersion);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ConflictFor(row);
        }
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO reviews(translation_id,reviewer_id,decision,note) VALUES(@id,@user,@decision,@note)";
            cmd.Parameters.AddWithValue("@id", row.Id);
            cmd.Parameters.AddWithValue("@user", reviewerId);
            cmd.Parameters.AddWithValue("@decision", approve ? "approved" : "rejected");
            cmd.Parameters.AddWithValue("@note", (object?)reviewNote ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await AddHistoryAsync(cn, tx, row.Id, reviewerId, approve ? "approved" : "rejected", null, reviewNote, ct);
        await tx.CommitAsync(ct);
        return new { translationId = row.Id, status = newStatus, rowVersion = row.RowVersion + 1 };
    }

    // ===================== Helpers =====================

    private static async Task<string> RequireTargetLocaleAsync(MySqlConnection cn, MySqlTransaction? tx, long projectId, string? locale, CancellationToken ct)
    {
        var code = (locale ?? "").Trim();
        if (code.Length == 0) throw ApiException.BadRequest("Thiếu mã ngôn ngữ (locale).");

        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT locale_code,is_source FROM project_locales WHERE project_id=@project AND locale_code=@locale";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@locale", code);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.BadRequest($"Ngôn ngữ '{code}' chưa được gắn vào dự án.");
        if (rd.GetBoolean(1)) throw ApiException.BadRequest("Không dịch trên ngôn ngữ nguồn. Hãy chọn ngôn ngữ đích.");
        return rd.GetString(0);
    }

    /// <summary>Người được phân công phải là thành viên dự án với vai trò translator hoặc manager.</summary>
    private static async Task<string> RequireAssigneeAsync(MySqlConnection cn, MySqlTransaction tx, long projectId, long userId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT u.display_name,pm.role FROM project_members pm JOIN users u ON u.id=pm.user_id WHERE pm.project_id=@project AND pm.user_id=@user";
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@user", userId);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.BadRequest("Người được phân công không thuộc dự án.");
        if (rd.GetString(1) is not (ProjectRole.Translator or ProjectRole.Manager))
            throw ApiException.BadRequest("Chỉ phân công cho thành viên có vai trò translator hoặc manager.");
        return rd.GetString(0);
    }

    private static Task<TranslationRow?> LockByIdAsync(MySqlConnection cn, MySqlTransaction tx, long projectId, long translationId, CancellationToken ct) =>
        LockAsync(cn, tx, projectId, "t.id=@id", p => p.AddWithValue("@id", translationId), ct);

    /// <summary>Đọc và khóa dòng bản dịch (SELECT ... FOR UPDATE) trong transaction, chỉ trong phạm vi dự án.</summary>
    private static async Task<TranslationRow?> LockAsync(MySqlConnection cn, MySqlTransaction tx, long projectId, string condition, Action<MySqlParameterCollection> bind, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $@"SELECT t.id,t.string_id,t.locale_code,t.value,t.status,t.assigned_to,au.display_name,
 t.row_version,uu.display_name,t.updated_at,ss.source_text,ss.status
FROM translations t
JOIN source_strings ss ON ss.id=t.string_id
LEFT JOIN users au ON au.id=t.assigned_to
LEFT JOIN users uu ON uu.id=t.updated_by
WHERE {condition} AND ss.project_id=@project
FOR UPDATE";
        cmd.Parameters.AddWithValue("@project", projectId);
        bind(cmd.Parameters);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) return null;
        return new TranslationRow(
            rd.GetInt64(0), rd.GetInt64(1), rd.GetString(2),
            rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
            rd.IsDBNull(5) ? null : rd.GetInt64(5), rd.IsDBNull(6) ? null : rd.GetString(6),
            rd.GetInt32(7), rd.IsDBNull(8) ? null : rd.GetString(8), rd.GetDateTime(9),
            rd.GetString(10), rd.GetString(11));
    }

    private static void EnsureCanEdit(TranslationRow row, long userId, string role)
    {
        if (role == ProjectRole.Translator && row.AssignedTo is not null && row.AssignedTo != userId)
            throw ApiException.Forbidden($"Chuỗi này đã được phân công cho {row.AssignedToName ?? "người dịch khác"}.");
    }

    private static ApiException ConflictFor(TranslationRow row, string message = "Có người khác đã sửa bản dịch này. Hệ thống không ghi đè âm thầm — hãy xem bản mới nhất.") =>
        ApiException.Conflict(message, new
        {
            currentRowVersion = row.RowVersion,
            currentValue = row.Value,
            currentStatus = row.DisplayStatus,
            updatedBy = row.UpdatedByName,
            updatedAt = row.UpdatedAt,
            assignedTo = row.AssignedTo,
            assignedToName = row.AssignedToName
        });

    private static async Task SetAssigneeAsync(MySqlConnection cn, MySqlTransaction tx, long translationId, long? assigneeId, CancellationToken ct)
    {
        // Phân công không đổi nội dung nên không tăng row_version (người đang dịch không bị 409 oan),
        // nhưng EnsureCanEdit vẫn chặn người không còn được phân công.
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE translations SET assigned_to=@user,updated_at=updated_at WHERE id=@id";
        cmd.Parameters.AddWithValue("@user", (object?)assigneeId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", translationId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task AddHistoryAsync(MySqlConnection cn, MySqlTransaction tx, long translationId, long userId, string action, string? oldValue, string? newValue, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "INSERT INTO translation_history(translation_id,user_id,old_value,new_value,action) VALUES(@id,@user,@old,@new,@action)";
        cmd.Parameters.AddWithValue("@id", translationId);
        cmd.Parameters.AddWithValue("@user", userId);
        cmd.Parameters.AddWithValue("@old", (object?)oldValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@new", (object?)newValue ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@action", action);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task EnsureTranslationInProjectAsync(MySqlConnection cn, long projectId, long translationId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM translations t JOIN source_strings ss ON ss.id=t.string_id WHERE t.id=@id AND ss.project_id=@project";
        cmd.Parameters.AddWithValue("@id", translationId);
        cmd.Parameters.AddWithValue("@project", projectId);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw ApiException.NotFound("Không tìm thấy bản dịch.");
    }

    private static async Task<object[]> QueryCommentsAsync(MySqlConnection cn, string condition, long id, CancellationToken ct)
    {
        var list = new List<object>();
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = $@"SELECT c.id,c.body,c.user_id,u.display_name,c.created_at
FROM translation_comments c JOIN users u ON u.id=c.user_id
WHERE {condition} ORDER BY c.id";
        cmd.Parameters.AddWithValue("@id", id);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            list.Add(new { id = rd.GetInt64(0), body = rd.GetString(1), userId = rd.GetInt64(2), userName = rd.GetString(3), createdAt = rd.GetDateTime(4) });
        return [.. list];
    }

    private static string EscapeLike(string text) =>
        text.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static bool IsConcurrencyError(MySqlException ex) =>
        ex.ErrorCode is MySqlErrorCode.DuplicateKeyEntry or MySqlErrorCode.LockDeadlock;
}
