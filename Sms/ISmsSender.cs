using System.Threading;
using System.Threading.Tasks;

namespace TranslationManager.Api.Services.Sms;

/// <summary>
/// Gửi SMS. Khi có nhà cung cấp SMS (eSMS, SpeedSMS, Twilio, ...), tạo một lớp mới
/// implement interface này rồi đăng ký trong Program.cs thay cho LogSmsSender.
/// </summary>
public interface ISmsSender
{
    /// <param name="phoneE164">Số điện thoại dạng +84xxxxxxxxx.</param>
    Task SendAsync(string phoneE164, string message, CancellationToken ct = default);
}