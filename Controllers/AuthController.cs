using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;
using TranslationManager.Api.Services;

namespace TranslationManager.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    Db db,
    PasswordHasher hasher,
    JwtTokenService jwt,
    AccessService access,
    SocialAuthService social,
    PasswordResetService passwordReset) : ControllerBase
{
    [Authorize, HttpGet("permissions"), ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Permissions(CancellationToken ct) => Ok(await access.ForUserAsync(User.UserId(), ct));

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var name = request.DisplayName.Trim();
        if (!email.Contains('@') || email.Length > 190) throw ApiException.BadRequest("Email không hợp lệ.");
        if (name.Length is < 2 or > 120) throw ApiException.BadRequest("Tên hiển thị cần từ 2 đến 120 ký tự.");
        if (request.Password.Length < 6) throw ApiException.BadRequest("Mật khẩu tối thiểu 6 ký tự.");
        // Số điện thoại không bắt buộc, dùng để nhận OTP khi quên mật khẩu.
        string? phone = string.IsNullOrWhiteSpace(request.Phone) ? null : PhoneNumber.Normalize(request.Phone);
        var (hash, salt) = hasher.Hash(request.Password);
        await using var cn = await db.OpenAsync(ct);
        if (phone is not null && await PhoneTakenAsync(cn, phone, null, ct))
            throw ApiException.Conflict("Số điện thoại đã được dùng cho tài khoản khác.");
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO users(email,display_name,phone,password_hash,password_salt) VALUES(@email,@name,@phone,@hash,@salt); SELECT LAST_INSERT_ID();";
        cmd.Parameters.AddWithValue("@email", email); cmd.Parameters.AddWithValue("@name", name); cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@hash", hash); cmd.Parameters.AddWithValue("@salt", salt);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return Ok(new AuthResponse(id, email, name, jwt.Create(id, email, name)));
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponse>> Login(LoginRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id,email,display_name,password_hash,password_salt,system_role,is_active FROM users WHERE email=@email LIMIT 1";
        cmd.Parameters.AddWithValue("@email", email);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || !rd.GetBoolean(6) || !hasher.Verify(request.Password, rd.GetString(3), rd.GetString(4)))
            throw ApiException.Unauthorized("Sai email hoặc mật khẩu.");
        var id = rd.GetInt64(0); var name = rd.GetString(2);
        return Ok(new AuthResponse(id, email, name, jwt.Create(id, email, name), rd.GetString(5)));
    }

    /// <summary>Đăng nhập bằng Google. Body: { "credential": "&lt;Google ID token&gt;" }.</summary>
    [HttpPost("google")]
    public async Task<ActionResult<AuthResponse>> Google(GoogleLoginRequest request, CancellationToken ct) =>
        Ok(await social.LoginWithGoogleAsync(request.Credential, ct));

    /// <summary>Đăng nhập bằng Facebook. Body: { "accessToken": "&lt;Facebook user access token&gt;" }.</summary>
    [HttpPost("facebook")]
    public async Task<ActionResult<AuthResponse>> Facebook(FacebookLoginRequest request, CancellationToken ct) =>
        Ok(await social.LoginWithFacebookAsync(request.AccessToken, ct));

    /// <summary>Bước 1 quên mật khẩu: gửi OTP tới số điện thoại. Body: { "phone": "0912345678" }.</summary>
    [HttpPost("forgot-password/request-otp")]
    public async Task<IActionResult> RequestPasswordResetOtp(ForgotPasswordOtpRequest request, CancellationToken ct)
    {
        var result = await passwordReset.RequestOtpAsync(request.Phone, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return Ok(new { success = true, message = result.Message, cooldownSeconds = result.CooldownSeconds, expiresInSeconds = result.ExpiresInSeconds });
    }

    /// <summary>Bước 2 quên mật khẩu: xác nhận OTP và đặt mật khẩu mới. Body: { "phone", "code", "newPassword" }.</summary>
    [HttpPost("forgot-password/reset")]
    public async Task<IActionResult> ResetPasswordWithOtp(ResetPasswordWithOtpRequest request, CancellationToken ct)
    {
        await passwordReset.ResetPasswordAsync(request.Phone, request.Code, request.NewPassword, ct);
        return Ok(new { success = true, message = "Đặt lại mật khẩu thành công. Bạn có thể đăng nhập bằng mật khẩu mới." });
    }

    /// <summary>Cập nhật / xóa số điện thoại nhận OTP của tài khoản đang đăng nhập. Body: { "phone": "0912345678" | null }.</summary>
    [Authorize, HttpPut("phone")]
    public async Task<IActionResult> UpdatePhone(UpdatePhoneRequest request, CancellationToken ct)
    {
        var userId = User.UserId();
        string? phone = string.IsNullOrWhiteSpace(request.Phone) ? null : PhoneNumber.Normalize(request.Phone);
        await using var cn = await db.OpenAsync(ct);
        if (phone is not null && await PhoneTakenAsync(cn, phone, userId, ct))
            throw ApiException.Conflict("Số điện thoại đã được dùng cho tài khoản khác.");
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "UPDATE users SET phone=@phone WHERE id=@id AND is_active=1";
        cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@id", userId);
        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw ApiException.Unauthorized();
        }
        catch (MySqlException ex) when (ex.Number == 1062)
        {
            throw ApiException.Conflict("Số điện thoại đã được dùng cho tài khoản khác.");
        }
        return Ok(new { success = true, phone });
    }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id,email,display_name,system_role,phone,google_id IS NOT NULL,facebook_id IS NOT NULL FROM users WHERE id=@id AND is_active=1";
        cmd.Parameters.AddWithValue("@id", User.UserId());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.Unauthorized();
        return Ok(new
        {
            userId = rd.GetInt64(0),
            email = rd.GetString(1),
            displayName = rd.GetString(2),
            systemRole = rd.GetString(3),
            phone = rd.IsDBNull(4) ? null : rd.GetString(4),
            googleLinked = Convert.ToInt64(rd.GetValue(5)) != 0,
            facebookLinked = Convert.ToInt64(rd.GetValue(6)) != 0
        });
    }

    private static async Task<bool> PhoneTakenAsync(MySqlConnection cn, string phone, long? exceptUserId, CancellationToken ct)
    {
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM users WHERE phone=@phone AND (@except IS NULL OR id<>@except)";
        cmd.Parameters.AddWithValue("@phone", phone);
        cmd.Parameters.AddWithValue("@except", exceptUserId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }
}