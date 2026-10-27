using MySqlConnector;

namespace TranslationManager.Api.Support;

public static class SystemAdminGuard
{
    public static async Task LockAsync(MySqlConnection cn, MySqlTransaction tx, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT id FROM system_admin_guard WHERE id=1 FOR UPDATE", cn, tx);
        if (await cmd.ExecuteScalarAsync(ct) is null) throw new InvalidOperationException("Missing system admin migration.");
    }

    public static async Task RequireAsync(MySqlConnection cn, MySqlTransaction tx, long actorId, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("SELECT COUNT(*) FROM users WHERE id=@actor AND system_role='admin' AND is_active=1", cn, tx);
        cmd.Parameters.AddWithValue("@actor", actorId);
        if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) != 1) throw ApiException.Forbidden("Chỉ Admin hệ thống được thực hiện thao tác này.");
    }

    public static async Task AuditAsync(MySqlConnection cn, MySqlTransaction tx, long actorId, string action, string targetType, long? targetId, string summary, CancellationToken ct)
    {
        await using var cmd = new MySqlCommand("INSERT INTO admin_audit(actor_id,action,target_type,target_id,summary) VALUES(@actor,@action,@type,@target,@summary)", cn, tx);
        cmd.Parameters.AddWithValue("@actor", actorId); cmd.Parameters.AddWithValue("@action", action);
        cmd.Parameters.AddWithValue("@type", targetType); cmd.Parameters.AddWithValue("@target", targetId); cmd.Parameters.AddWithValue("@summary", summary);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
