using System.Net.Mail;
using MySqlConnector;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed partial class AdminService
{
    private static string NormalizeEmail(string? value)
    {
        var email = (value ?? "").Trim().ToLowerInvariant();
        if (email.Length > 190 || !MailAddress.TryCreate(email, out var address) || address.Address != email || !email.Contains('@'))
            throw ApiException.BadRequest("Email không hợp lệ.");
        return email;
    }
    private static string NormalizeSystemRole(string? value)
    {
        var role = (value ?? "").Trim().ToLowerInvariant();
        if (role is not ("admin" or "user")) throw ApiException.BadRequest("Quyền hệ thống phải là admin hoặc user. Vai trò Manager/Translator/Reviewer được cấp riêng trong dự án.");
        return role;
    }
    public Task<SystemUserDetails> FindByEmailAsync(string? email, CancellationToken ct) => UserAsync(null, NormalizeEmail(email), ct);
    public Task<SystemUserDetails> UserAsync(long userId, CancellationToken ct) => UserAsync(userId, null, ct);
    private async Task<SystemUserDetails> UserAsync(long? userId, string? email, CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct); await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT u.id,u.email,u.display_name,u.system_role,u.is_active,u.security_version,u.created_at,(SELECT COUNT(*) FROM project_members WHERE user_id=u.id) FROM users u WHERE " + (userId.HasValue ? "u.id=@id" : "u.email=@email");
        cmd.Parameters.AddWithValue("@id", userId); cmd.Parameters.AddWithValue("@email", email);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Chưa có tài khoản với email hoặc ID này.");
        return new(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetBoolean(4), rd.GetInt32(5), rd.GetDateTime(6), rd.GetInt64(7));
    }
    public async Task<SystemUserDetails> SetRoleAsync(long actorId, long userId, ChangeSystemRoleRequest request, CancellationToken ct)
    {
        var role = NormalizeSystemRole(request.Role);
        var current = await UserAsync(userId, ct);
        await UpdateUserAsync(actorId, userId, new(current.DisplayName, role, current.IsActive, request.ExpectedVersion), ct);
        return await UserAsync(userId, ct);
    }
    public async Task<SystemUserDetails> SetStatusAsync(long actorId, long userId, ChangeUserStatusRequest request, CancellationToken ct)
    {
        if (!request.IsActive.HasValue) throw ApiException.BadRequest("Cần gửi isActive để khóa hoặc mở tài khoản.");
        var current = await UserAsync(userId, ct);
        await UpdateUserAsync(actorId, userId, new(current.DisplayName, current.SystemRole, request.IsActive.Value, request.ExpectedVersion), ct);
        return await UserAsync(userId, ct);
    }
    public async Task<SystemUserDetails> CreateUserAsync(long actorId, CreateSystemUserRequest request, CancellationToken ct)
    {
        var email = NormalizeEmail(request.Email); var name = (request.DisplayName ?? "").Trim();
        var role = NormalizeSystemRole(request.SystemRole); var password = request.Password ?? "";
        if (name.Length is < 2 or > 120) throw ApiException.BadRequest("Tên hiển thị cần từ 2 đến 120 ký tự.");
        if (password.Length is < 12 or > 200) throw ApiException.BadRequest("Mật khẩu tài khoản do Admin tạo cần từ 12 đến 200 ký tự.");
        var (hash, salt) = hasher.Hash(password);
        await using var cn = await db.OpenAsync(ct); await using var tx = await cn.BeginTransactionAsync(ct);
        await SystemAdminGuard.LockAsync(cn, tx, ct); await SystemAdminGuard.RequireAsync(cn, tx, actorId, ct);
        await using var cmd = new MySqlCommand("INSERT INTO users(email,display_name,password_hash,password_salt,system_role) VALUES(@email,@name,@hash,@salt,@role); SELECT LAST_INSERT_ID();", cn, tx);
        cmd.Parameters.AddWithValue("@email", email); cmd.Parameters.AddWithValue("@name", name); cmd.Parameters.AddWithValue("@hash", hash); cmd.Parameters.AddWithValue("@salt", salt); cmd.Parameters.AddWithValue("@role", role);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        await SystemAdminGuard.AuditAsync(cn, tx, actorId, "user.created", "user", id, System.Text.Json.JsonSerializer.Serialize(new { email, displayName = name, systemRole = role }), ct);
        await tx.CommitAsync(ct);
        return await UserAsync(id, ct);
    }
}
