using System.Threading;
using System.Threading.Tasks;
using TranslationManager.Api.Support;

namespace TranslationManager.Api.Services.Sms;

/// <summary>
/// Bộ gửi SMS tạm thời khi CHƯA có API SMS: không gửi thật, chỉ ghi nội dung tin nhắn ra log
/// của server (Railway logs / console) để thử chức năng OTP.
/// Thay bằng nhà cung cấp thật trước khi dùng chính thức.
/// </summary>
public sealed class LogSmsSender(ILogger<LogSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phoneE164, string message, CancellationToken ct = default)
    {
        logger.LogWarning("[SMS-LOG] Chưa cấu hình nhà cung cấp SMS. Tin nhắn tới {Phone}: {Message}",
            PhoneNumber.Mask(phoneE164), message);
        return Task.CompletedTask;
    }
}