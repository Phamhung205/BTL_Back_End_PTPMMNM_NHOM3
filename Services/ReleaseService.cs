using System.Data;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

/// <summary>
/// Phần Release (Phạm Thế Đức): chốt snapshot, sinh file JSON theo từng ngôn ngữ,
/// lưu release_files (nội dung + SHA-256) và tải lại gói đã phát hành.
/// Nội dung release nằm trong DB nên sửa bản dịch sau này không làm đổi release cũ.
/// </summary>
public sealed partial class ReleaseService(Db db, IConfiguration configuration, ILogger<ReleaseService> logger)
{
    // Ví dụ hợp lệ: 1.0.0, v2.1, 2026-10-release. Không bắt đầu/kết thúc bằng ký tự đặc biệt.
    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9._-]{0,58}[A-Za-z0-9])?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{1,20}$")]
    private static partial Regex SafeLocaleRegex();

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ===================== Kiểm tra điều kiện phát hành =====================

    public async Task<ReleaseValidation> ValidateAsync(long projectId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        return await ValidateCoreAsync(cn, null, projectId, ct);
    }

    private static async Task<ReleaseValidation> ValidateCoreAsync(MySqlConnection cn, MySqlTransaction? tx, long projectId, CancellationToken ct)
    {
        int activeStrings;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT COUNT(*) FROM source_strings WHERE project_id=@project AND status='active'";
            cmd.Parameters.AddWithValue("@project", projectId);
            activeStrings = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }

        long? sourceVersionId = null;
        string? sourceVersionName = null;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id,version_name FROM source_versions WHERE project_id=@project ORDER BY id DESC LIMIT 1";
            cmd.Parameters.AddWithValue("@project", projectId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            if (await rd.ReadAsync(ct)) { sourceVersionId = rd.GetInt64(0); sourceVersionName = rd.GetString(1); }
        }

        var locales = await TranslationService.LocaleProgressAsync(cn, tx, projectId, ct);

        var problems = new List<string>();
        if (activeStrings == 0) problems.Add("Dự án chưa có chuỗi nguồn. Hãy import phiên bản nguồn trước.");
        if (locales.Length == 0) problems.Add("Dự án chưa có ngôn ngữ đích để phát hành.");
        foreach (var l in locales.Where(l => l.Approved < l.Total))
        {
            problems.Add($"{l.LocaleCode}: mới duyệt {l.Approved}/{l.Total} chuỗi " +
                         $"(chưa dịch {l.Missing}, nháp {l.Draft}, chờ duyệt {l.Translated}, cần sửa {l.NeedsReview}).");
        }

        return new ReleaseValidation(problems.Count == 0, activeStrings, sourceVersionId, sourceVersionName, locales, [.. problems]);
    }

    // ===================== Chốt & phát hành =====================

    public async Task<ReleaseInfo> PublishAsync(long projectId, long userId, PublishReleaseRequest request, CancellationToken ct = default)
    {
        var version = (request.VersionName ?? "").Trim();
        if (version.Length == 0) throw ApiException.BadRequest("Thiếu tên phiên bản phát hành.");
        if (!VersionRegex().IsMatch(version))
            throw ApiException.BadRequest("Tên phiên bản chỉ gồm chữ, số, dấu chấm, gạch ngang, gạch dưới (ví dụ 1.0.0), tối đa 60 ký tự.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note?.Length > 1000) throw ApiException.BadRequest("Ghi chú phát hành tối đa 1000 ký tự.");

        string slug;
        long releaseId;
        var generated = new List<(ReleaseFileInfo Info, byte[] Bytes)>();
        try
        {
            await using var cn = await db.OpenAsync(ct);
            // REPEATABLE READ: mọi SELECT trong transaction đọc cùng một snapshot => các file nhất quán với nhau.
            await using var tx = await cn.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT slug FROM projects WHERE id=@project";
                cmd.Parameters.AddWithValue("@project", projectId);
                slug = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? throw ApiException.NotFound("Không tìm thấy dự án.");
            }

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT COUNT(*) FROM releases WHERE project_id=@project AND version_name=@version";
                cmd.Parameters.AddWithValue("@project", projectId);
                cmd.Parameters.AddWithValue("@version", version);
                if (Convert.ToInt32(await cmd.ExecuteScalarAsync(ct)) > 0) throw DuplicateVersion(version);
            }

            var validation = await ValidateCoreAsync(cn, tx, projectId, ct);
            if (!validation.Ready) throw ApiException.BadRequest("Chưa đủ điều kiện phát hành.", validation);

            var locales = new List<(string Code, bool IsSource)>();
            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT locale_code,is_source FROM project_locales WHERE project_id=@project ORDER BY is_source DESC,locale_code";
                cmd.Parameters.AddWithValue("@project", projectId);
                await using var rd = await cmd.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct)) locales.Add((rd.GetString(0), rd.GetBoolean(1)));
            }

            await using (var cmd = cn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO releases(project_id,version_name,source_version_id,note,key_count,published_by)
VALUES(@project,@version,@source,@note,@keys,@user); SELECT LAST_INSERT_ID();";
                cmd.Parameters.AddWithValue("@project", projectId);
                cmd.Parameters.AddWithValue("@version", version);
                cmd.Parameters.AddWithValue("@source", (object?)validation.LatestSourceVersionId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@keys", validation.ActiveStrings);
                cmd.Parameters.AddWithValue("@user", userId);
                releaseId = Convert.ToInt64(await cmd.ExecuteScalarAsync(ct));
            }

            foreach (var locale in locales)
            {
                var pairs = new List<KeyValuePair<string, string>>();
                await using (var cmd = cn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = locale.IsSource
                        ? "SELECT key_path,source_text FROM source_strings WHERE project_id=@project AND status='active' ORDER BY key_path"
                        : @"SELECT ss.key_path,t.value FROM source_strings ss JOIN translations t ON t.string_id=ss.id AND t.locale_code=@locale
WHERE ss.project_id=@project AND ss.status='active' AND t.status='approved' AND t.value IS NOT NULL ORDER BY ss.key_path";
                    cmd.Parameters.AddWithValue("@project", projectId);
                    if (!locale.IsSource) cmd.Parameters.AddWithValue("@locale", locale.Code);
                    await using var rd = await cmd.ExecuteReaderAsync(ct);
                    while (await rd.ReadAsync(ct)) pairs.Add(new(rd.GetString(0), rd.GetString(1)));
                }

                // Phòng thủ: mỗi file phải đủ toàn bộ khóa nguồn, không phát hành gói thiếu.
                if (pairs.Count != validation.ActiveStrings)
                    throw ApiException.Conflict($"Dữ liệu ngôn ngữ '{locale.Code}' thay đổi trong lúc phát hành. Hãy thử lại.");

                var json = JsonTree.Unflatten(pairs);
                var bytes = Encoding.UTF8.GetBytes(json);
                var sha = Sha256Hex(bytes);
                var fileName = $"{locale.Code}.json";

                await using (var insert = cn.CreateCommand())
                {
                    insert.Transaction = tx;
                    insert.CommandText = @"INSERT INTO release_files(release_id,locale_code,is_source,file_name,content_json,sha256,key_count)
VALUES(@release,@locale,@isSource,@file,@json,@sha,@keys)";
                    insert.Parameters.AddWithValue("@release", releaseId);
                    insert.Parameters.AddWithValue("@locale", locale.Code);
                    insert.Parameters.AddWithValue("@isSource", locale.IsSource);
                    insert.Parameters.AddWithValue("@file", fileName);
                    insert.Parameters.AddWithValue("@json", json);
                    insert.Parameters.AddWithValue("@sha", sha);
                    insert.Parameters.AddWithValue("@keys", pairs.Count);
                    await insert.ExecuteNonQueryAsync(ct);
                }
                generated.Add((new ReleaseFileInfo(locale.Code, locale.IsSource, fileName, sha, pairs.Count, bytes.Length), bytes));
            }

            await tx.CommitAsync(ct);
        }
        catch (MySqlException ex) when (ex.ErrorCode == MySqlErrorCode.DuplicateKeyEntry)
        {
            throw DuplicateVersion(version);
        }

        await ExportToDiskAsync(slug, version, generated, ct);
        return await GetAsync(projectId, releaseId, ct);
    }

    // ===================== Đọc release =====================

    public async Task<ReleaseInfo[]> ListAsync(long projectId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        return await QueryReleasesAsync(cn, projectId, null, ct);
    }

    public async Task<ReleaseInfo> GetAsync(long projectId, long releaseId, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        var list = await QueryReleasesAsync(cn, projectId, releaseId, ct);
        return list.Length > 0 ? list[0] : throw ApiException.NotFound("Không tìm thấy bản phát hành.");
    }

    public async Task<(string FileName, byte[] Content, string Sha256)> FileAsync(long projectId, long releaseId, string locale, CancellationToken ct = default)
    {
        await using var cn = await db.OpenAsync(ct);
        await using var cmd = cn.CreateCommand();
        cmd.CommandText = @"SELECT rf.file_name,rf.content_json,rf.sha256 FROM release_files rf JOIN releases r ON r.id=rf.release_id
WHERE r.id=@release AND r.project_id=@project AND rf.locale_code=@locale";
        cmd.Parameters.AddWithValue("@release", releaseId);
        cmd.Parameters.AddWithValue("@project", projectId);
        cmd.Parameters.AddWithValue("@locale", locale);
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        if (!await rd.ReadAsync(ct)) throw ApiException.NotFound("Không tìm thấy file trong bản phát hành.");

        var bytes = Encoding.UTF8.GetBytes(rd.GetString(1));
        var sha = rd.GetString(2);
        EnsureIntegrity(bytes, sha, rd.GetString(0));
        return (rd.GetString(0), bytes, sha);
    }

    /// <summary>Gói toàn bộ file JSON của một release + manifest.json vào một file .zip.</summary>
    public async Task<(string FileName, byte[] Content)> ZipAsync(long projectId, long releaseId, CancellationToken ct = default)
    {
        var release = await GetAsync(projectId, releaseId, ct);

        await using var cn = await db.OpenAsync(ct);
        string slug;
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = "SELECT slug FROM projects WHERE id=@project";
            cmd.Parameters.AddWithValue("@project", projectId);
            slug = Convert.ToString(await cmd.ExecuteScalarAsync(ct)) ?? throw ApiException.NotFound("Không tìm thấy dự án.");
        }

        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using var cmd = cn.CreateCommand();
            cmd.CommandText = "SELECT file_name,content_json,sha256 FROM release_files WHERE release_id=@release ORDER BY is_source DESC,locale_code";
            cmd.Parameters.AddWithValue("@release", releaseId);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var bytes = Encoding.UTF8.GetBytes(rd.GetString(1));
                EnsureIntegrity(bytes, rd.GetString(2), rd.GetString(0));
                await using var entry = zip.CreateEntry(SafeEntryName(rd.GetString(0)), CompressionLevel.Optimal).Open();
                await entry.WriteAsync(bytes, ct);
            }

            var manifest = JsonSerializer.Serialize(new
            {
                project = slug,
                version = release.Version,
                note = release.Note,
                publishedAt = release.PublishedAt,
                publishedBy = release.PublishedByName,
                sourceVersion = release.SourceVersionName,
                keyCount = release.KeyCount,
                files = release.Files.Select(f => new { locale = f.LocaleCode, file = f.FileName, isSource = f.IsSource, keys = f.KeyCount, sha256 = f.Sha256 })
            }, ManifestOptions);
            await using var manifestEntry = zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open();
            await manifestEntry.WriteAsync(Encoding.UTF8.GetBytes(manifest), ct);
        }

        return ($"{slug}-{release.Version}.zip", memory.ToArray());
    }

    // ===================== Helpers =====================

    private static async Task<ReleaseInfo[]> QueryReleasesAsync(MySqlConnection cn, long projectId, long? releaseId, CancellationToken ct)
    {
        var files = new Dictionary<long, List<ReleaseFileInfo>>();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"SELECT rf.release_id,rf.locale_code,rf.is_source,rf.file_name,rf.sha256,rf.key_count,LENGTH(rf.content_json)
FROM release_files rf JOIN releases r ON r.id=rf.release_id
WHERE r.project_id=@project AND (@release=0 OR r.id=@release)
ORDER BY rf.release_id,rf.is_source DESC,rf.locale_code";
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@release", releaseId ?? 0);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetInt64(0);
                if (!files.TryGetValue(id, out var list)) files[id] = list = [];
                list.Add(new ReleaseFileInfo(rd.GetString(1), rd.GetBoolean(2), rd.GetString(3), rd.GetString(4), rd.GetInt32(5), Convert.ToInt64(rd.GetValue(6))));
            }
        }

        var releases = new List<ReleaseInfo>();
        await using (var cmd = cn.CreateCommand())
        {
            cmd.CommandText = @"SELECT r.id,r.version_name,r.note,r.key_count,r.published_at,r.published_by,u.display_name,r.source_version_id,sv.version_name
FROM releases r JOIN users u ON u.id=r.published_by
LEFT JOIN source_versions sv ON sv.id=r.source_version_id
WHERE r.project_id=@project AND (@release=0 OR r.id=@release)
ORDER BY r.id DESC";
            cmd.Parameters.AddWithValue("@project", projectId);
            cmd.Parameters.AddWithValue("@release", releaseId ?? 0);
            await using var rd = await cmd.ExecuteReaderAsync(ct);
            while (await rd.ReadAsync(ct))
            {
                var id = rd.GetInt64(0);
                releases.Add(new ReleaseInfo(
                    id, rd.GetString(1), rd.IsDBNull(2) ? null : rd.GetString(2), rd.GetInt32(3), rd.GetDateTime(4),
                    rd.GetInt64(5), rd.GetString(6), rd.IsDBNull(7) ? null : rd.GetInt64(7), rd.IsDBNull(8) ? null : rd.GetString(8),
                    files.TryGetValue(id, out var list) ? [.. list] : []));
            }
        }
        return [.. releases];
    }

    /// <summary>Bản sao file ra đĩa (storage/releases/{slug}/{version}) — chỉ để tiện xem; DB mới là nguồn chuẩn.</summary>
    private async Task ExportToDiskAsync(string slug, string version, List<(ReleaseFileInfo Info, byte[] Bytes)> files, CancellationToken ct)
    {
        try
        {
            var root = Path.GetFullPath(configuration["Storage:ReleasePath"] ?? "storage/releases");
            var folder = Path.GetFullPath(Path.Combine(root, slug, version));
            if (!folder.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;

            Directory.CreateDirectory(folder);
            foreach (var (info, bytes) in files)
            {
                if (!SafeLocaleRegex().IsMatch(info.LocaleCode)) continue;
                await File.WriteAllBytesAsync(Path.Combine(folder, info.FileName), bytes, ct);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Không ghi được bản sao file phát hành ra đĩa cho {Slug}/{Version}", slug, version);
        }
    }

    private static void EnsureIntegrity(byte[] bytes, string expectedSha, string fileName)
    {
        if (!string.Equals(Sha256Hex(bytes), expectedSha, StringComparison.OrdinalIgnoreCase))
            throw new ApiException(500, $"File phát hành '{fileName}' không còn toàn vẹn (sai SHA-256).");
    }

    private static string Sha256Hex(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string SafeEntryName(string fileName) =>
        string.Concat(fileName.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    private static ApiException DuplicateVersion(string version) =>
        ApiException.Conflict($"Phiên bản '{version}' đã được phát hành. Bản phát hành cũ không bị thay đổi — hãy đặt tên phiên bản mới.");
}
