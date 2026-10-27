using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(Db db, PasswordHasher hasher, JwtTokenService jwt) : ControllerBase
{
    [HttpPost("register")]
    public async Task<ActionResult<AuthResponse>> Register(RegisterRequest request, CancellationToken ct)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        var name = request.DisplayName.Trim();
        if (!email.Contains('@') || email.Length > 190) throw ApiException.BadRequest("Email không hợp lệ.");
        if (name.Length is < 2 or > 120) throw ApiException.BadRequest("Tên hiển thị cần từ 2 đến 120 ký tự.");
        if (request.Password.Length < 6) throw ApiException.BadRequest("Mật khẩu tối thiểu 6 ký tự.");
        var (hash, salt) = hasher.Hash(request.Password);
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "INSERT INTO users(email,display_name,password_hash,password_salt) VALUES(@email,@name,@hash,@salt); SELECT LAST_INSERT_ID();";
        cmd.Parameters.AddWithValue("@email", email); cmd.Parameters.AddWithValue("@name", name); cmd.Parameters.AddWithValue("@hash", hash); cmd.Parameters.AddWithValue("@salt", salt);
        var id = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
        return Ok(new AuthResponse(id,email,name,jwt.Create(id,email,name)));
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
        var id=rd.GetInt64(0); var name=rd.GetString(2);
        return Ok(new AuthResponse(id,email,name,jwt.Create(id,email,name),rd.GetString(5)));
    }

    [Authorize, HttpGet("me")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = "SELECT id,email,display_name,system_role FROM users WHERE id=@id AND is_active=1";
        cmd.Parameters.AddWithValue("@id", User.UserId());
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.Unauthorized();
        return Ok(new { userId = rd.GetInt64(0), email = rd.GetString(1), displayName = rd.GetString(2), systemRole = rd.GetString(3) });
    }
}
