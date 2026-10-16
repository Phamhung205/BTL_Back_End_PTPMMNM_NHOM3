using Microsoft.AspNetCore.Mvc;
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
        if (!email.Contains('@')) throw ApiException.BadRequest("Email không hợp lệ.");
        if (name.Length < 2) throw ApiException.BadRequest("Tên hiển thị quá ngắn.");
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
        cmd.CommandText = "SELECT id,email,display_name,password_hash,password_salt FROM users WHERE email=@email LIMIT 1";
        cmd.Parameters.AddWithValue("@email", email);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct) || !hasher.Verify(request.Password, rd.GetString(3), rd.GetString(4)))
            throw ApiException.Unauthorized("Sai email hoặc mật khẩu.");
        var id=rd.GetInt64(0); var name=rd.GetString(2);
        return Ok(new AuthResponse(id,email,name,jwt.Create(id,email,name)));
    }
}
