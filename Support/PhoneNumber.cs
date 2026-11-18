using System.Linq;

namespace TranslationManager.Api.Support;

/// <summary>Chuẩn hóa số điện thoại về dạng E.164 (+84...). Số bắt đầu bằng 0 được coi là số Việt Nam.</summary>
public static class PhoneNumber
{
    public static bool TryNormalize(string? input, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(input)) return false;

        var raw = input.Trim();
        if (raw.Length > 25) return false;
        var plus = raw.StartsWith('+');
        var body = plus ? raw[1..] : raw;
        if (body.Any(c => !char.IsAsciiDigit(c) && c is not (' ' or '-' or '.' or '(' or ')'))) return false;
        var digits = new string(body.Where(char.IsAsciiDigit).ToArray());

        if (!plus)
        {
            if (digits.StartsWith("00")) digits = digits[2..];
            else if (digits.StartsWith('0')) digits = "84" + digits[1..];
            // Còn lại: coi như đã có mã quốc gia (vd. 84912345678).
        }

        if (digits.Length is < 9 or > 15 || digits.StartsWith('0')) return false;
        // Số Việt Nam: +84 + 9 chữ số.
        if (digits.StartsWith("84") && digits.Length != 11) return false;

        normalized = "+" + digits;
        return true;
    }

    public static string Normalize(string? input) =>
        TryNormalize(input, out var value) ? value : throw ApiException.BadRequest("Số điện thoại không hợp lệ.");

    /// <summary>Che bớt số khi ghi log hoặc hiển thị: +8491****678.</summary>
    public static string Mask(string phone) =>
        phone.Length <= 7 ? "***" : phone[..5] + new string('*', phone.Length - 8) + phone[^3..];
}