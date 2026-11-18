using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using MySqlConnector;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TranslationManager.Api.Data;
using TranslationManager.Api.Dtos;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services;

public sealed record ExternalProfile(string Provider, string ProviderUserId, string? Email, bool EmailVerified, string? Name);

/// <summary>
/// Đăng nhập bằng Google / Facebook.
/// - Google: frontend dùng Google Identity Services, gửi ID token (JWT). Backend tự kiểm tra chữ ký
///   bằng khóa công khai của Google, issuer, audience (= Google:ClientId) và hạn dùng.
/// - Facebook: frontend dùng Facebook JS SDK, gửi access token. Backend gọi Graph API debug_token
///   để xác nhận token thuộc đúng App (Facebook:AppId) rồi lấy id/name/email.
/// Sau khi xác thực: tìm user theo google_id/facebook_id → nếu chưa có thì theo email (đã xác minh)
/// để liên kết → nếu vẫn chưa có thì tạo tài khoản mới.
/// </summary>
public sealed class SocialAuthService(
    Db db,
    PasswordHasher hasher,
    JwtTokenService jwt,
    IConfiguration configuration,
    IHttpClientFactory httpClientFactory,
    ILogger<SocialAuthService> logger)
{
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> GoogleOidc = new(
        "https://accounts.google.com/.well-known/openid-configuration",
        new OpenIdConnectConfigurationRetriever(),
        new HttpDocumentRetriever { RequireHttps = true });

    // ---------------------------------------------------------------- Google

    public async Task<AuthResponse> LoginWithGoogleAsync(string? credential, CancellationToken ct)
    {
        var clientId = configuration["Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ApiException(503, "Đăng nhập Google chưa được cấu hình trên máy chủ (Google:ClientId).");
        if (string.IsNullOrWhiteSpace(credential) || credential.Length > 4096)
            throw ApiException.BadRequest("Thiếu thông tin đăng nhập Google.");

        var profile = await VerifyGoogleIdTokenAsync(credential, clientId.Trim(), ct);
        return await SignInExternalAsync(profile, ct);
    }

    private async Task<ExternalProfile> VerifyGoogleIdTokenAsync(string idToken, string clientId, CancellationToken ct)
    {
        TokenValidationResult result;
        try
        {
            result = await ValidateGoogleAsync(idToken, clientId, ct);
            if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
            {
                // Google xoay vòng khóa: tải lại danh sách khóa rồi thử thêm một lần.
                GoogleOidc.RequestRefresh();
                result = await ValidateGoogleAsync(idToken, clientId, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Không tải được cấu hình OpenID của Google.");
            throw new ApiException(503, "Không kết nối được tới Google. Vui lòng thử lại.");
        }

        if (!result.IsValid)
        {
            logger.LogInformation(result.Exception, "Google ID token không hợp lệ.");
            throw ApiException.Unauthorized("Phiên đăng nhập Google không hợp lệ hoặc đã hết hạn.");
        }

        var claims = result.Claims;
        var sub = ClaimString(claims, "sub");
        if (string.IsNullOrEmpty(sub)) throw ApiException.Unauthorized("Phiên đăng nhập Google không hợp lệ.");

        return new ExternalProfile("google", sub, ClaimString(claims, "email"), ClaimBool(claims, "email_verified"), ClaimString(claims, "name"));
    }

    private static async Task<TokenValidationResult> ValidateGoogleAsync(string idToken, string clientId, CancellationToken ct)
    {
        var config = await GoogleOidc.GetConfigurationAsync(ct);
        return await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidAudience = clientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        });
    }

    private static string? ClaimString(IDictionary<string, object> claims, string key) =>
        claims.TryGetValue(key, out var value) ? Convert.ToString(value) : null;

    private static bool ClaimBool(IDictionary<string, object> claims, string key) =>
        claims.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false
        };

    // -------------------------------------------------------------- Facebook

    public async Task<AuthResponse> LoginWithFacebookAsync(string? accessToken, CancellationToken ct)
    {
        var appId = configuration["Facebook:AppId"]?.Trim();
        var appSecret = configuration["Facebook:AppSecret"]?.Trim();
        if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(appSecret))
            throw new ApiException(503, "Đăng nhập Facebook chưa được cấu hình trên máy chủ (Facebook:AppId, Facebook:AppSecret).");
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 2048)
            throw ApiException.BadRequest("Thiếu thông tin đăng nhập Facebook.");

        var profile = await VerifyFacebookTokenAsync(accessToken.Trim(), appId, appSecret, ct);
        return await SignInExternalAsync(profile, ct);
    }

    private async Task<ExternalProfile> VerifyFacebookTokenAsync(string accessToken, string appId, string appSecret, CancellationToken ct)
    {
        var version = configuration["Facebook:GraphVersion"];
        var graph = "https://graph.facebook.com/" + (string.IsNullOrWhiteSpace(version) ? "v26.0" : version.Trim());
        var http = httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);

        // 1. Token có hợp lệ và được cấp cho đúng App không?
        using (var debug = await GetJsonAsync(http,
            $"{graph}/debug_token?input_token={Uri.EscapeDataString(accessToken)}&access_token={Uri.EscapeDataString(appId + "|" + appSecret)}", ct))
        {
            if (debug is null || !debug.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("is_valid", out var valid) || valid.ValueKind != JsonValueKind.True
                || !data.TryGetProperty("app_id", out var tokenAppId) || tokenAppId.GetString() != appId)
                throw ApiException.Unauthorized("Phiên đăng nhập Facebook không hợp lệ hoặc đã hết hạn.");
        }

        // 2. Lấy thông tin người dùng (kèm appsecret_proof theo khuyến nghị của Facebook).
        var proof = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(accessToken)));
        using var me = await GetJsonAsync(http,
            $"{graph}/me?fields=id,name,email&access_token={Uri.EscapeDataString(accessToken)}&appsecret_proof={proof}", ct)
            ?? throw ApiException.Unauthorized("Không lấy được thông tin tài khoản Facebook.");

        var root = me.RootElement;
        var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrEmpty(id)) throw ApiException.Unauthorized("Không lấy được thông tin tài khoản Facebook.");
        var email = root.TryGetProperty("email", out var emailEl) ? emailEl.GetString() : null;
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        // Facebook chỉ trả email đã được xác minh.
        return new ExternalProfile("facebook", id, email, !string.IsNullOrEmpty(email), name);
    }

    private async Task<JsonDocument?> GetJsonAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Facebook Graph trả về {Status}: {Body}", (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
                return null;
            }
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Không kết nối được tới Facebook Graph API.");
            throw new ApiException(503, "Không kết nối được tới Facebook. Vui lòng thử lại.");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ------------------------------------------------- Tìm / liên kết / tạo user

    private async Task<AuthResponse> SignInExternalAsync(ExternalProfile profile, CancellationToken ct)
    {
        // Tên cột lấy từ danh sách cố định, không lấy từ dữ liệu người dùng.
        var column = profile.Provider switch
        {
            "google" => "google_id",
            "facebook" => "facebook_id",
            _ => throw new InvalidOperationException("Unknown provider")
        };
        var providerLabel = profile.Provider == "google" ? "Google" : "Facebook";
        var providerId = profile.ProviderUserId.Trim();
        if (providerId.Length is 0 or > 64) throw ApiException.Unauthorized($"Tài khoản {providerLabel} không hợp lệ.");

        var email = profile.Email?.Trim().ToLowerInvariant();
        if (email is not null && (!email.Contains('@') || email.Length > 190)) email = null;

        await using var cn = await db.OpenAsync(ct);
        await using var tx = await cn.BeginTransactionAsync(ct);

        // 1. Đã từng đăng nhập bằng provider này.
        var user = await FindUserAsync(cn, tx, $"{column}=@value", providerId, ct);

        // 2. Có tài khoản cùng email (email đã được provider xác minh) → liên kết.
        if (user is null && email is not null && profile.EmailVerified)
        {
            user = await FindUserAsync(cn, tx, "email=@value", email, ct);
            if (user is not null)
            {
                if (user.LinkedId is not null && user.LinkedId != providerId)
                    throw ApiException.Conflict($"Email này đã được liên kết với một tài khoản {providerLabel} khác.");
                await using var link = new MySqlCommand($"UPDATE users SET {column}=@pid WHERE id=@id", cn, tx);
                link.Parameters.AddWithValue("@pid", providerId);
                link.Parameters.AddWithValue("@id", user.Id);
                await link.ExecuteNonQueryAsync(ct);
            }
        }

        // 3. Tạo tài khoản mới với mật khẩu ngẫu nhiên (người dùng có thể đặt mật khẩu qua "Quên mật khẩu").
        if (user is null)
        {
            var accountEmail = email is not null && profile.EmailVerified
                ? email
                : $"{profile.Provider}_{providerId}@no-email.invalid";
            var name = CleanName(profile.Name, accountEmail, providerLabel);
            var (hash, salt) = hasher.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            await using var insert = new MySqlCommand(
                $"INSERT INTO users(email,display_name,password_hash,password_salt,{column}) VALUES(@email,@name,@hash,@salt,@pid); SELECT LAST_INSERT_ID();",
                cn, tx);
            insert.Parameters.AddWithValue("@email", accountEmail);
            insert.Parameters.AddWithValue("@name", name);
            insert.Parameters.AddWithValue("@hash", hash);
            insert.Parameters.AddWithValue("@salt", salt);
            insert.Parameters.AddWithValue("@pid", providerId);
            try
            {
                var id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct));
                user = new UserRow(id, accountEmail, name, "user", true, providerId);
            }
            catch (MySqlException ex) when (ex.Number == 1062)
            {
                // Email chưa xác minh nhưng đã có người dùng → không tự liên kết để tránh chiếm tài khoản.
                throw ApiException.Conflict($"Email của tài khoản {providerLabel} đã được đăng ký. Hãy đăng nhập bằng mật khẩu.");
            }
        }

        if (!user.IsActive) throw ApiException.Unauthorized("Tài khoản đã bị khóa.");
        await tx.CommitAsync(ct);

        return new AuthResponse(user.Id, user.Email, user.DisplayName, jwt.Create(user.Id, user.Email, user.DisplayName), user.SystemRole);

        async Task<UserRow?> FindUserAsync(MySqlConnection connection, MySqlTransaction transaction, string where, string value, CancellationToken token)
        {
            await using var cmd = new MySqlCommand(
                $"SELECT id,email,display_name,system_role,is_active,{column} FROM users WHERE {where} LIMIT 1 FOR UPDATE",
                connection, transaction);
            cmd.Parameters.AddWithValue("@value", value);
            await using var rd = await cmd.ExecuteReaderAsync(token);
            if (!await rd.ReadAsync(token)) return null;
            return new UserRow(rd.GetInt64(0), rd.GetString(1), rd.GetString(2), rd.GetString(3), rd.GetBoolean(4),
                rd.IsDBNull(5) ? null : rd.GetString(5));
        }
    }

    private static string CleanName(string? name, string email, string providerLabel)
    {
        var value = (name ?? "").Trim();
        if (value.Length > 120) value = value[..120];
        if (value.Length >= 2) return value;
        var local = email.Split('@')[0];
        return local.Length >= 2 && !email.EndsWith(".invalid", StringComparison.Ordinal)
            ? (local.Length > 120 ? local[..120] : local)
            : $"Người dùng {providerLabel}";
    }

    private sealed record UserRow(long Id, string Email, string DisplayName, string SystemRole, bool IsActive, string? LinkedId);
}