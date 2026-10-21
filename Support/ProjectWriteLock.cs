using MySqlConnector;

namespace TranslationManager.Api.Support;

/// <summary>Serialize source import, locale changes and publishing within one project.</summary>
public static class ProjectWriteLock
{
    public static async Task AcquireAsync(MySqlConnection connection, MySqlTransaction transaction, long projectId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id FROM projects WHERE id=@project FOR UPDATE";
        command.Parameters.AddWithValue("@project", projectId);
        if (await command.ExecuteScalarAsync(ct) is null)
            throw ApiException.NotFound("Không tìm thấy dự án.");
    }
}
