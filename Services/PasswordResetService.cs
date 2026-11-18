using MySqlConnector;
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TranslationManager.Api.Data;
using TranslationManager.Api.Services.Sms;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

/// <summary>
/// Quên mật khẩu bằng mã OTP gửi qua SMS tới số điện thoại của tài khoản.
/// - Mã 6 chữ số, chỉ lưu dạng băm HMAC-SHA256, hết hạn sau PasswordReset:OtpTtlSeconds (mặc định 300 giây).
/// - Mỗi mã cho phép nhập sai tối đa PasswordReset:MaxAttempts lần (mặc định 5).
/// - Mỗi số điện thoại phải chờ PasswordReset:ResendCooldownSeconds giữa hai lần gửi (mặc định 60 giây)
///   và tối đa PasswordReset:MaxPerHour mã mỗi giờ (mặc định 5).
/// </summary>
public sealed class PasswordResetService(Db db, PasswordHasher hasher, ISmsSender sms, IConfiguration configuration, ILogger<PasswordResetService> logger)
{
    private const string InvalidCodeMessage = "Mã OTP không đúng hoặc đã hết hạn.";

    private int OtpTtlSeconds => Setting("PasswordReset:OtpTtlSeconds", 300, 60, 1800);
    private int MaxAttempts => Setting("PasswordReset:MaxAttempts", 5, 1, 10);
    private int ResendCooldownSeconds => Setting("PasswordReset:ResendCooldownSeconds", 60, 0, 600);
    private int MaxPerHour => Setting("PasswordReset:MaxPerHour", 5, 1, 50);

    public sealed record OtpRequestResult(string Message, int CooldownSeconds, int ExpiresInSeconds);

    public async Task<OtpRequestResult> RequestOtpAsync(string? phoneInput, string? requestIp, CancellationToken ct)
    {
        var phone = PhoneNumber.Normalize(phoneInput);
        // Luôn trả cùng một thông báo dù số có tồn tại hay không, để không lộ số điện thoại nào đã đăng ký.
        var generic = new OtpRequestResult(
            "Nếu số điện thoại đã được đăng ký, mã OTP sẽ được gửi tới trong giây lát.",
            ResendCooldownSeconds, OtpTtlSeconds);

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        long userId;
        await using (var find = new MySqlCommand("SELECT id FROM users WHERE phone=@phone AND is_active=1 LIMIT 1 FOR UPDATE", cn, tx))
        {
            find.Parameters.AddWithValue("@phone", phone);
            var found = await find.ExecuteScalarAsync(ct);
            if (found is null)
            {
                await tx.CommitAsync(ct);
                return generic;
            }
            userId = Convert.ToInt64(found);
        }

        // Giới hạn tần suất gửi.
        await using (var limit = new MySqlCommand(
            @"SELECT COUNT(*), COALESCE(MAX(TIMESTAMPDIFF(SECOND, created_at, UTC_TIMESTAMP()) < @cooldown), 0)
              FROM password_reset_otps
              WHERE user_id=@uid AND created_at > DATE_SUB(UTC_TIMESTAMP(), INTERVAL 1 HOUR)", cn, tx))
        {
            limit.Parameters.AddWithValue("@uid", userId);
            limit.Parameters.AddWithValue("@cooldown", ResendCooldownSeconds);
            await using var rd = await limit.ExecuteReaderAsync(ct);
            await rd.ReadAsync(ct);
            var sentLastHour = rd.GetInt64(0);
            var inCooldown = Convert.ToInt64(rd.GetValue(1)) != 0;
            if (inCooldown)
                throw new ApiException(429, $"Vui lòng đợi {ResendCooldownSeconds} giây trước khi yêu cầu mã mới.");
            if (sentLastHour >= MaxPerHour)
                throw new ApiException(429, "Bạn đã yêu cầu quá nhiều mã OTP. Vui lòng thử lại sau 1 giờ.");
        }

        // Vô hiệu hóa các mã cũ còn hiệu lực.
        await using (var revoke = new MySqlCommand("UPDATE password_reset_otps SET consumed_at=UTC_TIMESTAMP() WHERE user_id=@uid AND consumed_at IS NULL", cn, tx))
        {
            revoke.Parameters.AddWithValue("@uid", userId);
            await revoke.ExecuteNonQueryAsync(ct);
        }

        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        long otpId;
        await using (var insert = new MySqlCommand(
            @"INSERT INTO password_reset_otps(user_id,phone,code_hash,attempts,expires_at,created_at,request_ip)
              VALUES(@uid,@phone,@hash,0,DATE_ADD(UTC_TIMESTAMP(), INTERVAL @ttl SECOND),UTC_TIMESTAMP(),@ip);
              SELECT LAST_INSERT_ID();", cn, tx))
        {
            insert.Parameters.AddWithValue("@uid", userId);
            insert.Parameters.AddWithValue("@phone", phone);
            insert.Parameters.AddWithValue("@hash", HashOtp(userId, code));
            insert.Parameters.AddWithValue("@ttl", OtpTtlSeconds);
            insert.Parameters.AddWithValue("@ip", Truncate(requestIp, 64));
            otpId = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
        }
        await tx.CommitAsync(ct);

        // SMS không dấu để tránh bị tính phí tin Unicode.
        var minutes = Math.Max(1, OtpTtlSeconds / 60);
        var message = $"Ma OTP dat lai mat khau Translation Manager cua ban la {code}. Ma co hieu luc {minutes} phut. Khong chia se ma nay cho bat ky ai.";
        try
        {
            await sms.SendAsync(phone, message, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Gửi SMS OTP thất bại tới {Phone}", PhoneNumber.Mask(phone));
            await using var cleanup = await db.OpenAsync(CancellationToken.None);
            await using var del = new MySqlCommand("DELETE FROM password_reset_otps WHERE id=@id", cleanup);
            del.Parameters.AddWithValue("@id", otpId);
            await del.ExecuteNonQueryAsync(CancellationToken.None);
            throw new ApiException(503, "Không gửi được SMS lúc này. Vui lòng thử lại sau.");
        }

        return generic;
    }

    public async Task ResetPasswordAsync(string? phoneInput, string? codeInput, string? newPassword, CancellationToken ct)
    {
        var phone = PhoneNumber.Normalize(phoneInput);
        var code = (codeInput ?? "").Trim();
        if (code.Length != 6 || !code.All(char.IsAsciiDigit)) throw ApiException.BadRequest("Mã OTP gồm 6 chữ số.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6) throw ApiException.BadRequest("Mật khẩu tối thiểu 6 ký tự.");
        if (newPassword.Length > 200) throw ApiException.BadRequest("Mật khẩu quá dài.");

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        long userId;
        await using (var find = new MySqlCommand("SELECT id FROM users WHERE phone=@phone AND is_active=1 LIMIT 1 FOR UPDATE", cn, tx))
        {
            find.Parameters.AddWithValue("@phone", phone);
            var found = await find.ExecuteScalarAsync(ct) ?? throw ApiException.BadRequest(InvalidCodeMessage);
            userId = Convert.ToInt64(found);
        }

        long otpId; string storedHash; int attempts;
        await using (var otp = new MySqlCommand(
            @"SELECT id,code_hash,attempts FROM password_reset_otps
              WHERE user_id=@uid AND consumed_at IS NULL AND expires_at > UTC_TIMESTAMP()
              ORDER BY id DESC LIMIT 1 FOR UPDATE", cn, tx))
        {
            otp.Parameters.AddWithValue("@uid", userId);
            await using var rd = await otp.ExecuteReaderAsync(ct);
            if (!await rd.ReadAsync(ct)) throw ApiException.BadRequest(InvalidCodeMessage);
            otpId = rd.GetInt64(0); storedHash = rd.GetString(1); attempts = rd.GetInt32(2);
        }

        var match = CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(storedHash), Encoding.ASCII.GetBytes(HashOtp(userId, code)));

        if (!match)
        {
            attempts++;
            var exhausted = attempts >= MaxAttempts;
            await using (var fail = new MySqlCommand(
                "UPDATE password_reset_otps SET attempts=@attempts, consumed_at=IF(@exhausted, UTC_TIMESTAMP(), consumed_at) WHERE id=@id", cn, tx))
            {
                fail.Parameters.AddWithValue("@attempts", attempts);
                fail.Parameters.AddWithValue("@exhausted", exhausted);
                fail.Parameters.AddWithValue("@id", otpId);
                await fail.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
            throw ApiException.BadRequest(exhausted
                ? "Bạn đã nhập sai quá nhiều lần. Vui lòng yêu cầu mã OTP mới."
                : $"Mã OTP không đúng. Bạn còn {MaxAttempts - attempts} lần thử.");
        }

        var (hash, salt) = hasher.Hash(newPassword);
        await using (var update = new MySqlCommand(
            "UPDATE users SET password_hash=@hash, password_salt=@salt, security_version=security_version+1 WHERE id=@uid", cn, tx))
        {
            update.Parameters.AddWithValue("@hash", hash);
            update.Parameters.AddWithValue("@salt", salt);
            update.Parameters.AddWithValue("@uid", userId);
            await update.ExecuteNonQueryAsync(ct);
        }
        await using (var consume = new MySqlCommand(
            "UPDATE password_reset_otps SET consumed_at=UTC_TIMESTAMP() WHERE user_id=@uid AND consumed_at IS NULL", cn, tx))
        {
            consume.Parameters.AddWithValue("@uid", userId);
            await consume.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private string HashOtp(long userId, string code)
    {
        var key = configuration["Jwt:Key"] ?? throw new InvalidOperationException("Missing Jwt:Key");
        return Convert.ToHexStringLower(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes("otp-reset:" + key),
            Encoding.UTF8.GetBytes($"{userId}:{code}")));
    }

    private int Setting(string key, int fallback, int min, int max) =>
        int.TryParse(configuration[key], out var value) ? Math.Clamp(value, min, max) : fallback;

    private static string? Truncate(string? value, int length) =>
        value is null ? null : value.Length <= length ? value : value[..length];
}