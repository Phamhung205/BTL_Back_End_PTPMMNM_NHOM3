using MySqlConnector;
using System;
using System.Threading;
using System.Threading.Tasks;
using TranslationManager.Api.Data;

namespace TranslationManager.Api.Services;

/// <summary>
/// Tự bổ sung cột/bảng cho đăng nhập Google/Facebook và quên mật khẩu bằng OTP khi backend khởi động.
/// Chạy được trên cả MySQL 8 và MariaDB (kiểm tra information_schema thay vì dùng "ADD COLUMN IF NOT EXISTS").
/// </summary>
public sealed class AuthSchemaUpgrader(Db db, ILogger<AuthSchemaUpgrader> logger)
{
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);

        if (!await TableExistsAsync(cn, "users", ct))
        {
            logger.LogWarning("Chưa có bảng users; bỏ qua nâng cấp schema đăng nhập. Hãy import database/schema.sql trước.");
            return;
        }

        await EnsureColumnAsync(cn, "users", "phone", "ALTER TABLE users ADD COLUMN phone VARCHAR(20) NULL AFTER display_name", ct);
        await EnsureColumnAsync(cn, "users", "google_id", "ALTER TABLE users ADD COLUMN google_id VARCHAR(64) NULL AFTER password_salt", ct);
        await EnsureColumnAsync(cn, "users", "facebook_id", "ALTER TABLE users ADD COLUMN facebook_id VARCHAR(64) NULL AFTER google_id", ct);

        await EnsureIndexAsync(cn, "users", "uq_users_phone", "ALTER TABLE users ADD UNIQUE INDEX uq_users_phone (phone)", ct);
        await EnsureIndexAsync(cn, "users", "uq_users_google_id", "ALTER TABLE users ADD UNIQUE INDEX uq_users_google_id (google_id)", ct);
        await EnsureIndexAsync(cn, "users", "uq_users_facebook_id", "ALTER TABLE users ADD UNIQUE INDEX uq_users_facebook_id (facebook_id)", ct);

        await ExecAsync(cn, @"CREATE TABLE IF NOT EXISTS password_reset_otps (
  id BIGINT UNSIGNED AUTO_INCREMENT PRIMARY KEY,
  user_id BIGINT UNSIGNED NOT NULL,
  phone VARCHAR(20) NOT NULL,
  code_hash CHAR(64) NOT NULL,
  attempts INT NOT NULL DEFAULT 0,
  expires_at DATETIME NOT NULL,
  consumed_at DATETIME NULL,
  created_at DATETIME NOT NULL,
  request_ip VARCHAR(64) NULL,
  INDEX ix_pro_user_created (user_id, created_at),
  CONSTRAINT fk_pro_user FOREIGN KEY (user_id) REFERENCES users(id) ON DELETE CASCADE
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4 COLLATE=utf8mb4_unicode_ci", ct);

        // Dọn mã OTP cũ hơn 7 ngày.
        await ExecAsync(cn, "DELETE FROM password_reset_otps WHERE created_at < DATE_SUB(UTC_TIMESTAMP(), INTERVAL 7 DAY)", ct);

        logger.LogInformation("Schema đăng nhập mạng xã hội / OTP đã sẵn sàng.");
    }

    private static async Task<bool> TableExistsAsync(MySqlConnection cn, string table, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t", cn);
        cmd.Parameters.AddWithValue("@t", table);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0;
    }

    private async Task EnsureColumnAsync(MySqlConnection cn, string table, string column, string ddl, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND COLUMN_NAME=@c", cn);
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@c", column);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0) return;
        await ExecAsync(cn, ddl, ct);
        logger.LogInformation("Đã thêm cột {Table}.{Column}", table, column);
    }

    private async Task EnsureIndexAsync(MySqlConnection cn, string table, string index, string ddl, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(
            "SELECT COUNT(*) FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME=@t AND INDEX_NAME=@i", cn);
        cmd.Parameters.AddWithValue("@t", table);
        cmd.Parameters.AddWithValue("@i", index);
        if (Convert.ToInt64(await cmd.ExecuteScalarAsync(ct)) > 0) return;
        await ExecAsync(cn, ddl, ct);
        logger.LogInformation("Đã thêm chỉ mục {Index}", index);
    }

    private static async Task ExecAsync(MySqlConnection cn, string sql, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand(sql, cn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}