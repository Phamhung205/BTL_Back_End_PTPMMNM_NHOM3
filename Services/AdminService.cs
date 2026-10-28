using System.Text.Json;
using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed partial class AdminService(Db db, PasswordHasher hasher)
{
    public async Task BootstrapAsync(IConfiguration configuration, CancellationToken ct = default, bool allowAdditional = false)
    {
        var email = (configuration["BootstrapAdmin:Email"] ?? "").Trim().ToLowerInvariant();
        var name = (configuration["BootstrapAdmin:DisplayName"] ?? "System Admin").Trim();
        var password = configuration["BootstrapAdmin:Password"] ?? "";
        if (!email.Contains('@') || email.Length > 190 || name.Length is < 2 or > 120 || password.Length < 12)
            throw new InvalidOperationException("Set BootstrapAdmin__Email, BootstrapAdmin__DisplayName and BootstrapAdmin__Password (12+ characters) before bootstrapping.");
        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);
        await SystemAdminGuard.LockAsync(cn, tx, ct);
        await using var count = new MySqlCommand("SELECT COUNT(*) FROM users WHERE system_role='admin'", cn, tx);
        if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) > 0 && !allowAdditional) { await tx.CommitAsync(ct); return; }
        long id;
        await using (var existing = new MySqlCommand("SELECT id,password_hash,password_salt FROM users WHERE email=@email FOR UPDATE", cn, tx))
        {
            existing.Parameters.AddWithValue("@email", email);
            await using var rd = await existing.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct))
            {
                if (!hasher.Verify(password, rd.GetString(1), rd.GetString(2))) throw new InvalidOperationException("Existing bootstrap account password does not match.");
                id = rd.GetInt64(0);
            }
            else id = 0;
        }
        if (id == 0)
        {
            var (hash, salt) = hasher.Hash(password);
            await using var insert = new MySqlCommand("INSERT INTO users(email,display_name,password_hash,password_salt,system_role) VALUES(@email,@name,@hash,@salt,'admin'); SELECT LAST_INSERT_ID();", cn, tx);
            insert.Parameters.AddWithValue("@email", email); insert.Parameters.AddWithValue("@name", name); insert.Parameters.AddWithValue("@hash", hash); insert.Parameters.AddWithValue("@salt", salt);
            id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        else
        {
            await using var promote = new MySqlCommand("UPDATE users SET system_role='admin',is_active=1,security_version=security_version+1 WHERE id=@id", cn, tx);
            promote.Parameters.AddWithValue("@id", id); await promote.ExecuteNonQueryAsync(ct);
        }
        await SystemAdminGuard.AuditAsync(cn, tx, id, "admin.bootstrapped", "user", id, "Khởi tạo Admin đầu tiên bằng lệnh CLI.", ct);
        await tx.CommitAsync(ct);
    }

    public async Task<object> StatsAsync(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct); await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT (SELECT COUNT(*) FROM users), (SELECT COUNT(*) FROM users WHERE is_active=1),
 (SELECT COUNT(*) FROM users WHERE system_role='admin' AND is_active=1), (SELECT COUNT(*) FROM projects),
 (SELECT COUNT(*) FROM source_strings WHERE status='active'), (SELECT COUNT(*) FROM translations WHERE value IS NOT NULL),
 (SELECT COUNT(*) FROM releases), (SELECT COUNT(*) FROM admin_audit)";
        await using var rd = await cmd.ExecuteReaderAsync(ct); await rd.ReadAsync(ct);
        return new { users = rd.GetInt64(0), activeUsers = rd.GetInt64(1), admins = rd.GetInt64(2), projects = rd.GetInt64(3), sourceStrings = rd.GetInt64(4), translations = rd.GetInt64(5), releases = rd.GetInt64(6), auditEvents = rd.GetInt64(7) };
    }

    public Task<object> UsersAsync(string? q, string? role, string? status, int page, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(role) && role is not ("user" or "admin")) throw ApiException.BadRequest("Quyền hệ thống không hợp lệ.");
        if (!string.IsNullOrEmpty(status) && status is not ("active" or "locked")) throw ApiException.BadRequest("Trạng thái tài khoản không hợp lệ.");
        const string from = " FROM users u WHERE (@q='' OR u.email LIKE @q OR u.display_name LIKE @q) AND (@role='' OR u.system_role=@role) AND (@status='' OR u.is_active=(@status='active'))";
        return PageAsync("SELECT u.id,u.email,u.display_name,u.system_role,u.is_active,u.security_version,u.created_at,(SELECT COUNT(*) FROM project_members pm WHERE pm.user_id=u.id)" + from + " ORDER BY u.id DESC", "SELECT COUNT(*)" + from,
            new() { ["@q"] = Search(q), ["@role"] = role ?? "", ["@status"] = status ?? "" }, page,
            rd => new { id = rd.GetInt64(0), email = rd.GetString(1), displayName = rd.GetString(2), systemRole = rd.GetString(3), isActive = rd.GetBoolean(4), securityVersion = rd.GetInt32(5), createdAt = rd.GetDateTime(6), projectCount = rd.GetInt64(7) }, ct);
    }

    public Task<object> ProjectsAsync(string? q, int page, CancellationToken ct)
    {
        const string from = " FROM projects p LEFT JOIN users u ON u.id=p.created_by WHERE (@q='' OR p.name LIKE @q OR p.slug LIKE @q)";
        return PageAsync(@"SELECT p.id,p.name,p.slug,p.source_locale_code,p.created_at,u.display_name,
 (SELECT COUNT(*) FROM project_members WHERE project_id=p.id),
 (SELECT COUNT(*) FROM source_strings WHERE project_id=p.id AND status='active'),
 (SELECT COUNT(*) FROM releases WHERE project_id=p.id)" + from + " ORDER BY p.id DESC", "SELECT COUNT(*)" + from,
            new() { ["@q"] = Search(q) }, page, rd => new { id = rd.GetInt64(0), name = rd.GetString(1), slug = rd.GetString(2), sourceLocaleCode = rd.GetString(3), createdAt = rd.GetDateTime(4), createdByName = rd.IsDBNull(5) ? null : rd.GetString(5), memberCount = rd.GetInt64(6), stringCount = rd.GetInt64(7), releaseCount = rd.GetInt64(8) }, ct);
    }

    public async Task<object[]> MembersAsync(long projectId, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var exists = new MySqlCommand("SELECT id FROM projects WHERE id=@project", cn); exists.Parameters.AddWithValue("@project", projectId);
        if (await exists.ExecuteScalarAsync(ct) is null) throw ApiException.NotFound("Không tìm thấy dự án.");
        await using var cmd = new MySqlCommand("SELECT u.id,u.email,u.display_name,pm.role,u.is_active FROM project_members pm JOIN users u ON u.id=pm.user_id WHERE pm.project_id=@project ORDER BY u.display_name", cn);
        cmd.Parameters.AddWithValue("@project", projectId); var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct)) items.Add(new { userId = rd.GetInt64(0), email = rd.GetString(1), displayName = rd.GetString(2), role = rd.GetString(3), isActive = rd.GetBoolean(4) });
        return [.. items];
    }

    public Task<object> AuditAsync(string? q, int page, CancellationToken ct)
    {
        const string from = " FROM admin_audit a LEFT JOIN users u ON u.id=a.actor_id WHERE (@q='' OR a.action LIKE @q OR a.summary LIKE @q OR u.email LIKE @q)";
        return PageAsync("SELECT a.id,a.action,a.target_type,a.target_id,a.summary,a.created_at,u.display_name,u.email" + from + " ORDER BY a.id DESC", "SELECT COUNT(*)" + from,
            new() { ["@q"] = Search(q) }, page, rd => new { id = rd.GetInt64(0), action = rd.GetString(1), targetType = rd.GetString(2), targetId = rd.IsDBNull(3) ? (long?)null : rd.GetInt64(3), summary = rd.GetString(4), createdAt = rd.GetDateTime(5), actorName = rd.IsDBNull(6) ? null : rd.GetString(6), actorEmail = rd.IsDBNull(7) ? null : rd.GetString(7) }, ct);
    }

    public async Task UpdateUserAsync(long actorId, long userId, UpdateSystemUserRequest request, CancellationToken ct)
    {
        var name = (request.DisplayName ?? "").Trim();
        if (name.Length is < 2 or > 120 || request.SystemRole is not ("admin" or "user") || request.ExpectedVersion < 1) throw ApiException.BadRequest("Tên, quyền hoặc phiên bản tài khoản không hợp lệ.");
        await using var cn = await db.OpenAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        await SystemAdminGuard.LockAsync(cn, tx, ct); await SystemAdminGuard.RequireAsync(cn, tx, actorId, ct);
        string role; string oldName; bool active; int version;
        await using (var target = new MySqlCommand("SELECT display_name,system_role,is_active,security_version FROM users WHERE id=@id FOR UPDATE", cn, tx))
        {
            target.Parameters.AddWithValue("@id", userId); await using var rd = await target.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Không tìm thấy tài khoản.");
            oldName = rd.GetString(0); role = rd.GetString(1); active = rd.GetBoolean(2); version = rd.GetInt32(3);
        }
        if (version != request.ExpectedVersion) throw ApiException.Conflict("Tài khoản đã được Admin khác cập nhật. Hãy tải lại trước khi sửa.", new { code = "stale_user" });
        if (active && role == "admin" && (!request.IsActive || request.SystemRole != "admin"))
        {
            await using var count = new MySqlCommand("SELECT COUNT(*) FROM users WHERE system_role='admin' AND is_active=1", cn, tx);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(ct)) <= 1) throw ApiException.Conflict("Hệ thống cần ít nhất một Admin đang hoạt động.", new { code = "last_admin" });
        }
        if (active && !request.IsActive)
        {
            await using var manager = new MySqlCommand(@"SELECT p.name FROM projects p JOIN project_members pm ON pm.project_id=p.id AND pm.user_id=@user AND pm.role='manager'
 WHERE (SELECT COUNT(*) FROM project_members pm2 JOIN users u ON u.id=pm2.user_id AND u.is_active=1 WHERE pm2.project_id=p.id AND pm2.role='manager') <= 1 LIMIT 1", cn, tx);
            manager.Parameters.AddWithValue("@user", userId); var project = await manager.ExecuteScalarAsync(ct);
            if (project is not null) throw ApiException.Conflict("Tài khoản là Manager đang hoạt động cuối cùng của dự án '" + project + "'. Hãy bổ sung Manager trước khi khóa.", new { code = "last_project_manager" });
        }
        await using var update = new MySqlCommand("UPDATE users SET display_name=@name,system_role=@role,is_active=@active,security_version=security_version+1 WHERE id=@id", cn, tx);
        update.Parameters.AddWithValue("@name", name); update.Parameters.AddWithValue("@role", request.SystemRole); update.Parameters.AddWithValue("@active", request.IsActive); update.Parameters.AddWithValue("@id", userId);
        await update.ExecuteNonQueryAsync(ct);
        await SystemAdminGuard.AuditAsync(cn, tx, actorId, "user.updated", "user", userId, JsonSerializer.Serialize(new { before = new { displayName = oldName, systemRole = role, isActive = active }, after = new { displayName = name, request.SystemRole, request.IsActive } }), ct);
        await tx.CommitAsync(ct);
    }

    private static string Search(string? value)
    {
        value = value?.Trim() ?? "";
        if (value.Length > 200) throw ApiException.BadRequest("Từ khóa tối đa 200 ký tự.");
        return value.Length == 0 ? "" : "%" + value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
    }

    private async Task<object> PageAsync(string sql, string countSql, Dictionary<string, object> values, int page, Func<MySqlDataReader, object> read, CancellationToken ct)
    {
        if (page is < 1 or > 999999) throw ApiException.BadRequest("Trang không hợp lệ.");
        await using var cn = await db.OpenAsync(ct); long total;
        await using (var count = new MySqlCommand(countSql, cn))
        {
            foreach (var pair in values) count.Parameters.AddWithValue(pair.Key, pair.Value);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(ct));
        }
        await using var cmd = new MySqlCommand(sql + " LIMIT 50 OFFSET @offset", cn);
        foreach (var pair in values) cmd.Parameters.AddWithValue(pair.Key, pair.Value);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * 50); var items = new List<object>();
        await using var rd = await cmd.ExecuteReaderAsync(ct); while (await rd.ReadAsync(ct)) items.Add(read(rd));
        return new { items, total, page, pageSize = 50, hasMore = page * 50L < total };
    }
}
