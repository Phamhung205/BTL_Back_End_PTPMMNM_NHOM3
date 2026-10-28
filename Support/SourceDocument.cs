using System.Text;
using System.Text.Json;
using TranslationManager.Api.Dtos;

namespace TranslationManager.Api.Support;

public static class SourceDocument
{
    public const int MaxBytes = 2 * 1024 * 1024;

    public static Dictionary<string, string> Read(ImportSourceRequest request)
    {
        if (request.RawJson is not null && request.Data.ValueKind != JsonValueKind.Undefined)
            throw ApiException.BadRequest("Chỉ gửi data hoặc rawJson, không gửi cả hai.");
        return Parse(request.RawJson ?? (request.Data.ValueKind == JsonValueKind.Undefined ? "" : request.Data.GetRawText()), "JSON nguồn");
    }

    /// <summary>Đọc JSON lồng nhau hoặc khóa có dấu chấm thành danh sách khóa phẳng; dùng chung cho nguồn và bản dịch.</summary>
    public static Dictionary<string, string> Parse(string? json, string label, int maxValueLength = 10000)
    {
        json ??= "";
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
            throw ApiException.BadRequest($"{label} tối đa 2 MB.");
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonTree.Flatten(document.RootElement, maxValueLength);
        }
        catch (JsonException)
        {
            throw ApiException.BadRequest($"{label} không hợp lệ hoặc vượt quá độ sâu 64 cấp.");
        }
    }
}
